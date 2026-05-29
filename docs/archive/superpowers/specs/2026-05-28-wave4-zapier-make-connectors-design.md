# Wave 4 — Zapier + Make.com Connectors Design

_Date: 2026-05-28. Approved by founder. Implementation via writing-plans → executing-plans._

---

## Summary

Add a production-grade integration distribution layer to ProcuLink: per-tenant API keys, an inbound REST ingress endpoint for B2B push integrations, a proper webhook subscription model, Hangfire-backed outbound trigger delivery, and Zapier + Make.com connector definition files ready for platform submission.

---

## 1. API key model + auth

### Entity: `TenantApiKey`

Table: `tenant_api_keys` (migration: `AddTenantApiKeys`)

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `organisation_id` | uuid FK | |
| `name` | text | user-assigned label |
| `key_prefix` | text | first 8 chars of plaintext key, stored for display |
| `key_hash` | text | HMAC-SHA256 of full key, stored |
| `scopes` | text[] | `"ingress"`, `"subscriptions"` |
| `is_active` | bool | default true |
| `created_at` | timestamptz | |
| `last_used_at` | timestamptz nullable | stamped on each authenticated request |
| `expires_at` | timestamptz nullable | null = no expiry |

### Key generation

Format: `plk_` + 32 random bytes as base64url. Returns plaintext **once only** to the caller — never stored. Stored value is `HMAC-SHA256(key, DELIVERY_ENCRYPTION_KEY)` — reuses the existing encryption key infrastructure.

### `ApiKeyAuthHandler`

`ProcuLink.Infrastructure/Auth/ApiKeyAuthHandler.cs` implementing `AuthenticationHandler<ApiKeyAuthSchemeOptions>`:

1. Read `X-Api-Key` header. Missing → `NoResult`.
2. Hash presented key. Lookup `tenant_api_keys` where `key_hash = ? AND is_active = true AND (expires_at IS NULL OR expires_at > now())`.
3. On match: build `ClaimsPrincipal` with `OrganisationId` claim, stamp `last_used_at`, return `Success`.
4. On miss: return `Fail("Invalid or expired API key")`.

Registered as second auth scheme (`"ApiKey"`) alongside Clerk in `Program.cs`. Applied only to controllers decorated `[Authorize(AuthenticationSchemes = "ApiKey")]`.

### `ApiKeyController`

Clerk-authenticated (org members manage their own keys):

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/api-keys` | Generate key — returns `{ id, name, key (plaintext, once), prefix, scopes }` |
| `GET` | `/api/api-keys` | List — returns `{ id, name, prefix, scopes, lastUsedAt, expiresAt }` (never hash) |
| `DELETE` | `/api/api-keys/{id}` | Revoke — sets `is_active = false`, org-scoped |

---

## 2. Inbound REST API + tenant slug

### Tenant slug

Add `Slug` (text, unique index, lowercase-kebab) to `Organisation`. Migration: `AddSlugToOrganisations`.

- Auto-generated from org name on creation (e.g. `acme-procurement`, suffixed with random 4 chars on collision)
- Editable via `PUT /api/settings/organisation` (new endpoint or extend existing settings)
- Exposed in org settings response DTO

### `IngressController`

`[Authorize(AuthenticationSchemes = "ApiKey")]`

**`POST /api/ingress/{slug}/orders`**
- Validates `{slug}` resolves to an org
- Validates resolved org matches API key's `OrganisationId` (prevents cross-org posting)
- Validates key has scope `"ingress"`
- Accepts `multipart/form-data` with `file` field
- Passes to `IOrderService.CreateStubAsync` (existing)
- Enqueues `ParseOrderJob` (existing)
- Idempotency-keyed via `Idempotency-Key` header (reuses `IdempotencyKey` infrastructure)
- Returns `202 Accepted` with `{ orderId, status: "queued" }`

**`GET /api/ingress/{slug}/health`**
- No auth
- Returns `200 OK` with `{ status: "ok", org: slug }`
- Used by Zapier/Make during connector setup to verify endpoint reachability

### Error response shape

Structured JSON (not default ProblemDetails) for Zapier/Make parser compatibility:

```json
{ "error": "Invalid or expired API key", "code": "api_key_invalid" }
```

Codes: `api_key_invalid`, `scope_insufficient`, `org_not_found`, `cross_org_forbidden`, `order_limit_reached` (reuses existing 429 billing response shape).

---

## 3. Integration subscription layer

### Entity: `IntegrationSubscription`

Table: `integration_subscriptions` (migration: `AddIntegrationSubscriptions`)

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `organisation_id` | uuid FK | |
| `platform` | text | `"zapier"` / `"make"` / `"custom"` |
| `event_type` | text | `"order.created"` / `"order.delivered"` / `"order.failed"` |
| `target_url` | text | webhook endpoint registered by Zapier/Make |
| `secret_encrypted` | text | AES-GCM encrypted secret, via `DeliveryEncryptionService` |
| `is_active` | bool | default true |
| `created_at` | timestamptz | |
| `last_fired_at` | timestamptz nullable | |
| `failure_count` | int | default 0; set `is_active = false` after 3 consecutive failures |

### `IntegrationController`

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/integrations/subscriptions` | ApiKey (scope `"subscriptions"`) | Register subscription — Zapier/Make call this during setup |
| `GET` | `/api/integrations/subscriptions` | Clerk | List org subscriptions |
| `DELETE` | `/api/integrations/subscriptions/{id}` | Clerk or ApiKey | Deactivate subscription |
| `GET` | `/api/integrations/subscriptions/verify` | None | Echo `?challenge=` back as `{ challenge }` — Zapier liveness check |

### Trigger payload shape

```json
{
  "event": "order.created",
  "timestamp": "2026-05-28T12:00:00Z",
  "orgSlug": "acme-procurement",
  "data": {
    "orderId": "uuid",
    "status": "pending_review",
    "supplierName": "Acme Supplies",
    "buyerName": "Acme Procurement",
    "createdAt": "2026-05-28T12:00:00Z"
  }
}
```

Signed with `X-ProcuLink-Signature: sha256=<HMAC-SHA256(body, decrypted secret)>` header — standard Zapier/Make webhook verification pattern.

---

## 4. Outbound trigger service

### `IIntegrationTriggerService`

```csharp
public interface IIntegrationTriggerService
{
    Task FireAsync(Guid orgId, string eventType, object payload, CancellationToken ct);
}
```

Lives in `ProcuLink.Core/Services/`.

### `IntegrationTriggerService`

`ProcuLink.Infrastructure`:
1. Loads all `IntegrationSubscription` rows where `organisation_id = orgId AND event_type = eventType AND is_active = true`
2. Enqueues one `FireIntegrationTriggerJob` per subscription (Hangfire `BackgroundJob.Enqueue`)
3. Fire-and-forget from caller — no await on delivery

### `FireIntegrationTriggerJob`

`ProcuLink.Worker` Hangfire job:

1. Load subscription by id; if not found or `is_active = false` → no-op
2. Decrypt secret via `DeliveryEncryptionService`
3. Serialize payload to JSON
4. Compute `X-ProcuLink-Signature: sha256=<HMAC-SHA256(body, secret)>`
5. POST to `TargetUrl` — 30-second timeout, `HttpClient` via `IHttpClientFactory`
6. On `2xx`: stamp `LastFiredAt`, reset `FailureCount = 0`
7. On non-`2xx` or timeout: increment `FailureCount`; Hangfire retry policy: 3 attempts, exponential backoff (1 min → 10 min → 1 hr)
8. After 3rd failure: `IsActive = false` + log warning

### Hook points in existing services

**`OrderService.CreateStubAsync`** — after stub is persisted and before return, call `IIntegrationTriggerService.FireAsync(orgId, "order.created", { orderId, status, ... })`.

**`DeliveryService`** — after status transitions:
- `delivered` → `FireAsync(orgId, "order.delivered", { orderId, supplierId, ... })`
- `delivery_failed` → `FireAsync(orgId, "order.failed", { orderId, failureReason, ... })`

Both injected via constructor DI. Both fire-and-forget.

---

## 5. Connector definition files + frontend

### `docs/integrations/`

**`zapier-app.json`** — Zapier Platform CLI app manifest:
- Auth type: `custom` (API key via `X-Api-Key` header)
- Test endpoint: `GET /api/integrations/subscriptions/verify`
- Triggers:
  - `order_created` — REST hook; subscribe: `POST /api/integrations/subscriptions`; unsubscribe: `DELETE /api/integrations/subscriptions/{id}`
  - `order_delivered` — same pattern
- Actions:
  - `upload_order` — `POST /api/ingress/{slug}/orders` multipart
  - `set_mapping` — `POST /api/suppliers/{id}/mappings`

**`make-connector.json`** — Make.com connector mirroring the same four modules in Make's module schema format.

**`docs/integrations/SUBMISSION.md`** — platform submission checklist:
- Zapier CLI commands (`zapier push`, `zapier submit`)
- Make partner portal steps
- Expected review timelines (Zapier ~6 weeks, Make ~2 weeks)
- Required screenshots and descriptions

### Frontend additions (project-proculink)

**Settings page — API Keys tab** (new tab alongside existing Billing/Email tabs):
- "Generate new key" button → modal with name input + scope checkboxes → shows plaintext key once with copy button and explicit "this will not be shown again" warning
- Key list: name, prefix (`plk_abc123...`), scopes, last used, expires, revoke button
- No key regeneration — revoke + create new

**`/operations/connectors` — Zapier/Make section** (extends existing connector panel):
- Active subscriptions list: platform, event type, target URL (truncated), last fired, failure count
- "Copy Zapier webhook URL" helper showing `GET /api/ingress/{slug}/health` endpoint
- Link to `docs/integrations/SUBMISSION.md` for setup guide

No new routes. Both additions slot into already-built UI surfaces.

---

## 6. Tests

- `ApiKeyAuthHandler`: valid key → 200; invalid key → 401; revoked key → 401; wrong-org key → 403
- `IngressController`: valid upload → 202 with orderId; missing scope → 403; slug mismatch → 403; billing limit → 429
- `IntegrationTriggerService.FireAsync`: enqueues one job per active subscription; skips inactive
- `FireIntegrationTriggerJob`: 2xx response → stamps `LastFiredAt`; 3 failures → `IsActive = false`
- `IntegrationController`: subscription CRUD is org-scoped; verify endpoint echoes challenge

---

## 7. Out of scope for Wave 4

- OAuth2 auth for connector (API key is sufficient for initial platform submission)
- Make.com full implementation (connector definition file only; live Make approval separate)
- Shopify/WooCommerce webhook receivers (Wave 5)
- Zapier `order_confirmed` / `invoice_received` triggers (Wave 3 entities needed first)
- Frontend subscription management beyond the connector panel addition
- Zapier app listing / Make.com directory listing (post-approval, external)
