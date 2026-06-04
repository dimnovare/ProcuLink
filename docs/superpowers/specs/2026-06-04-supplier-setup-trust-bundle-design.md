# Supplier-setup trust bundle — design spec

**Date:** 2026-06-04
**Status:** Draft for review
**Author:** Claude Code (with founder)
**Goal:** Make the supplier-setup experience credible for the first paying client by
closing the gaps between what the product *claims* (and what the walkthrough video will
claim) and what a customer can actually self-serve. Almost all of this is wiring the
**frontend** up to a backend that already works.

---

## Background

A read of both repos showed that the supplier-setup pain is mostly a frontend surface
problem over a proven backend:

- **SFTP / FTPS / email delivery dispatchers already exist, are SSRF-guarded, and the
  delivery-config API accepts them** (`SftpDeliveryDispatcher`, `FtpsDeliveryDispatcher`,
  `SmtpDeliveryDispatcher`). The editor UI hides SFTP/FTPS ("later") and omits email.
- **Delete supplier already exists** (`DELETE /api/suppliers/{id}`, soft-delete;
  `apiClient.deleteSupplier` is in the client) — but no UI ever calls it.
- **Validation-rule help text exists** but doesn't land, and the rule editor lets users
  build rules against field paths the backend never evaluates.
- The **"Configure delivery"** button is a redundant second route to a tab that's already
  one click away.

One genuinely new capability is required (founder request): HTTP delivery to suppliers
whose APIs need a **bearer token fetched first** (OAuth2 client-credentials), because the
token is short-lived and can't be stored statically. This is the only item needing
backend work.

---

## Channel truth matrix (offer ⇔ works)

Audited 2026-06-04. **Founder directive: every import and delivery channel we offer or
promise must work as intended — anything that doesn't work must not be offered.** Verdict:
the engines are real; the gaps are offer/promise-side.

**Delivery (egress):**

| Channel | Backend | Offered in UI | Action |
|---|---|---|---|
| HTTP webhook | real, proven live (200) | yes | keep; add OAuth mode (②b) |
| SFTP | real | disabled "later" | **offer (②)** |
| FTPS | real | disabled "later" | **offer (②)** |
| Email (SMTP) | real | absent | **offer (②)** |
| Erply ERP | real (auth HTTP POST) | yes | keep; verify test-fire |
| Directo ERP | real (form POST + creds) | yes | keep; verify test-fire |
| plain `ftp` | no dispatcher | id bug only | never offer; fix `ftp`→`ftps` id |

**Import (ingest):**

| Channel | Backend | Offered self-serve | Action |
|---|---|---|---|
| Upload CSV / XLSX / PDF(text) | real | yes | keep; verify per-format |
| Upload cXML / UBL / EDIFACT / X12 | parsers real | accepted, but copy says only CSV/XLSX/PDF | align copy honestly (⑦) |
| Scanned PDF (OCR) | gated (NoOp unless Azure) | implied | keep parse-fail copy honest |
| IMAP email polling | real | yes (Integration+) | keep; verify |
| REST ingress (Zapier/Make/M2M) | real | partial (API keys) | keep as assisted |
| Webhook-in (ACK) | real | no UI | assisted, not self-serve |
| SFTP / S3 pull | real | no UI | assisted, not self-serve |

**Promise vs reality:** `/library/standards` + `catalog.ts` are already the honest SoT
(EDIFACT/X12 marked `planned`). The marketing homepage (`how-it-works`) and `help` over-claim:
"any format", "SFTP/email" delivery, "EDIFACT/X12" outputs. Reconciliation = make marketing
conform to the catalog + the bundle's now-true SFTP/email; the same honest claim-set then
feeds the walkthrough video.

---

## Scope

**In scope (this bundle):**
- ② Multi-channel delivery in the editor: SFTP, FTPS, Email (SMTP). *(frontend only)*
- ②b HTTP "fetch token first" auth — OAuth2 client-credentials. *(backend + frontend)*
- ③ Make per-supplier "Validation rules" self-explanatory + safe. *(frontend only)*
- ④ Remove the redundant "Configure delivery" button. *(frontend only)*
- ⑤ Delete supplier UI. *(frontend only)*
- ⑦ **Claims reconciliation (copy):** align `FileUploadZone` copy, `how-it-works`, and
  `help` to the verified capability set + the `/library/standards` catalog — so we promise
  exactly what works. Honestly *expand* the upload copy (we really parse cXML/UBL/EDIFACT/X12);
  honestly *qualify* EDIFACT/X12 outputs and assisted-only inbound channels. *(frontend only)*
- **Verification bar (acceptance gate, not optional):** every channel left *offered* after
  this bundle must be proven to work:
  - Each delivery protocol (http, http+oauth, sftp, ftps, smtp, erp_erply, erp_directo) —
    unit/integration test + a local Test-fire where an external endpoint can be faked.
  - Each accepted upload format (csv, xlsx, pdf-text, cxml, ubl, edifact, x12) — a parser
    test proving a representative file produces a parsed order.
  - Channels that can only be proven against a real external system (ERP sandbox, real
    mailbox, real supplier SFTP/token server) get a working Test-fire affordance **and** an
    honest "needs your real endpoint" note. Those final live checks are the founder's side.

**Out of scope (deferred to the readiness audit, item ⑥):**
- Consolidating the duplicate org-wide `ValidationRule` system vs. per-supplier
  `SupplierAcceptanceProfile`. The bundle treats the per-supplier acceptance tab as the
  single user-facing concept and does not touch the org-wide system.
- Building self-serve UI for SFTP-pull / S3-pull ingress and inbound webhook (stay assisted;
  not advertised as one-click).
- Building EDIFACT/X12 **outbound** transformers — they stay `planned`. *Open question for
  the founder: is any not-yet-production-ready claimed capability (EDIFACT/X12 output,
  SFTP/S3 inbound) actually needed for the FIRST supplier? If yes, it moves into scope; if
  no, we soften the copy.*
- OAuth token caching-until-expiry (v1 fetches per delivery).
- Cascade/cleanup of orphaned config on supplier soft-delete (acceptable: orders retained
  for audit; flag in audit).

**No backend migrations.** No database schema changes anywhere in this bundle.

---

## ② Multi-channel delivery (SFTP / FTPS / Email)

All changes in `project-proculink/src/components/bridge/DeliveryConfigEditor.tsx` plus the
`DeliveryProtocol` type in `src/lib/api/types`.

### Two bugs to fix
1. **`DeliveryConfigEditor.tsx:25`** declares the FTPS option as `{ id: "ftp", label: "FTPS" }`.
   The id `"ftp"` has **no backend dispatcher** (only `ftps`), so enabling it as-is would
   fail at delivery with "no dispatcher registered." → change id to `"ftps"`.
2. The editor is **URL-centric**: `canSave = Boolean(url) && …` and the body only renders an
   "Endpoint URL" field. SFTP/FTPS/SMTP have a **host**, not a URL. Without protocol-aware
   fields + validation the Save button stays disabled. → make fields, `canSave`,
   `buildConfigObject`, `buildCredentialsJson`, `hydrateConfig` all protocol-aware.

### Protocol list (replaces `PROTOCOLS`, all `enabled: true`)
`http` · `erp_erply` · `erp_directo` · `sftp` · `ftps` · `smtp` (label "Email (SMTP)").

### Fields + emitted JSON per protocol
JSON is **camelCase** (dispatchers deserialize case-insensitive camelCase). `configJson`
is non-secret; secrets go only in `credentialsJson` (AES-encrypted server-side, returned
masked).

**SFTP** — default port 22
- Fields: Host, Port, Remote path, Make directories (checkbox), Timeout (s), Auth mode
  (Password ⇄ Private key). Password mode: Username + Password. Key mode: Username +
  Private key (textarea) + Passphrase (optional).
- `configJson`: `{ "host", "port", "remotePath", "makeDirectories", "timeoutSeconds" }`
- `credentialsJson` (password): `{ "username", "password" }`
- `credentialsJson` (key): `{ "username", "privateKey", "privateKeyPassphrase" }`

**FTPS** — default port 21
- Fields: Host, Port, Remote path, Make directories, Timeout (s), Allow invalid certificate
  (checkbox + "self-signed only" warning), Username, Password.
- `configJson`: `{ "host", "port", "remotePath", "makeDirectories", "timeoutSeconds", "allowInvalidCertificate" }`
- `credentialsJson`: `{ "username", "password" }`

**Email (SMTP)** — default port 587
- Fields: Host, Port, Use SSL (checkbox), From address, Recipients (comma-separated),
  Timeout (s). Advanced disclosure: Subject template, Body template, Attachment file name
  (templates support `{poNumber}` and `{fileName}`).
- `configJson`: `{ "host", "port", "useSsl", "fromAddress", "toAddresses", "timeoutSeconds", "subjectTemplate"?, "bodyTemplate"?, "attachmentFileName"? }`
  — `toAddresses` sent as the comma-separated string (dispatcher splits it).
- `credentialsJson`: `{ "username", "password" }`

### Behaviour
- `canSave`: http/erp require `url` (+ directo requires database); sftp/ftps require `host`;
  smtp requires `host` + `fromAddress` + at least one recipient.
- `hydrateConfig`: extend to repopulate host/port/remotePath/useSsl/fromAddress/etc. when an
  existing config of each protocol loads.
- The existing **Test-fire** button is the trust-builder: configure a real SFTP / mailbox,
  hit Test-fire, confirm the artifact lands.
- Credential editing keeps the existing "leave blank to keep saved secret" pattern
  (`hasSavedCredentials` → emit `null` to preserve).

---

## ②b HTTP "fetch token first" — OAuth2 client-credentials

### Backend — `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs`
- Add auth `type` `"oauth2_client_credentials"`. Credentials arrive as the existing generic
  `JsonElement`, so this is a new branch — no new POCO needed elsewhere.
- `ApplyAuth(request, creds)` becomes `await ApplyAuthAsync(request, creds, client, ct)`
  (static→instance; it now may make an HTTP call). Existing apikey/bearer/basic cases
  unchanged.
- OAuth2 branch, before the delivery request:
  1. Read `tokenUrl`; **SSRF-guard it** with `_guard.ValidateAsync(tokenUrl, ct)` (same
     protection as the delivery URL — non-negotiable).
  2. Build the token request (defaults = standard OAuth2 client-credentials):
     - `requestStyle` `"form"` (default): `application/x-www-form-urlencoded` body
       `grant_type=<grantType|client_credentials>` (+ `scope` if set).
     - `authStyle` `"body"` (default): add `client_id` + `client_secret` to the body.
       `"basic"`: send `Authorization: Basic base64(clientId:clientSecret)`, omit from body.
     - `requestStyle` `"json"`: POST JSON with the same fields.
  3. Send via the existing `delivery` `HttpClient`. On non-2xx → fail with
     `"OAuth token request failed: HTTP {code}."`
  4. Extract the token at `tokenResponsePath` (default `"access_token"`; support a simple
     dotted path like `data.token`). Missing → fail with
     `"OAuth token response did not contain a token at '{path}'."`
  5. Set `Authorization: Bearer <token>` on the delivery request.
- **Security:** token fetched **fresh per delivery** (no storage at rest); token never
  logged; client secret stays in the AES-encrypted credentials blob and is masked in API
  responses like every other secret.

### OAuth2 credential JSON (frontend → backend)
```json
{
  "type": "oauth2_client_credentials",
  "tokenUrl": "https://supplier.example/oauth/token",
  "clientId": "…",
  "clientSecret": "…",
  "scope": "orders.write",
  "grantType": "client_credentials",
  "authStyle": "body",
  "requestStyle": "form",
  "tokenResponsePath": "access_token"
}
```
`scope`, `grantType`, `authStyle`, `requestStyle`, `tokenResponsePath` are optional with the
defaults shown.

### Frontend — `DeliveryConfigEditor.tsx`
- New HTTP auth-type option: **"OAuth2 — fetch token first."**
- Primary fields: Token URL, Client ID, Client secret, Scope (optional).
- "Advanced" disclosure: Grant type (default `client_credentials`), Request format
  (form/json), Client auth (in body / Basic header), Token response field
  (default `access_token`).
- `buildCredentialsJson` emits the JSON above.

### Test (backend integration)
- Stub `IHttpClientFactory`/`HttpMessageHandler`: token endpoint returns
  `{ "access_token": "abc", "expires_in": 3600 }`; delivery endpoint asserts the inbound
  `Authorization: Bearer abc` then returns 200. Assert `DeliveryResult.Success`.
- A failure case: token endpoint returns 401 → `DeliveryResult` fails with the OAuth
  message and the delivery endpoint is never called.
- **Gotcha:** `OutboundRequestGuard` blocks loopback/private IPs by default. Tests must set
  `Delivery:AllowPrivateNetworkTargets=true` (dev/test config) or use the same harness the
  existing HTTP dispatcher tests use.

---

## ③ Validation rules — clarity + safety

All in `SupplierDockProfile.tsx` (the `AcceptanceTab`).

### Plain-language explainer (always visible at top of tab)
> **How validation works.** Before an order is sent to *{supplier}*, ProcuLink checks it
> against these rules. **Error** rules block delivery until they're fixed; **Warning** rules
> only flag and never block. Validation never changes the order — it's a gate.

Plus a one-line worked example near the empty state:
> e.g. *Currency must be EUR* (error) · *Every line needs a supplier code* (error).

### Constrain Field to what the backend evaluates (kills dead rules)
Replace the free-text Field input with a **per-scope dropdown**:
- Scope **order** → `currency`, `buyerName`
- Scope **line** → `supplierItemCode`, `buyerItemCode`, `description`, `quantity`, `unitPrice`

(Adding a field later means updating both the dropdown **and** `EvaluateOrderField`/
`EvaluateLineField` in `SupplierAcceptanceService` — call this out in a code comment.)

### Align operators with the backend
Operator dropdown must offer exactly what `Evaluate` supports, with friendly labels:
`required` (is present) · `equals` · `not_equals` · `in` (is one of, comma list) ·
`contains` · `greater_than` · `less_than` · `min` (≥) · `max` (≤) · `max_length`.
(Currently missing: `in`, `min`, `max`.)

### "+ Add common rule" quick-pick
Each inserts a prefilled rule (all use resolvable field paths):
| Label | scope | fieldPath | operator | expected | severity | block |
|---|---|---|---|---|---|---|
| Currency must be EUR | order | currency | equals | EUR | error | yes |
| Every line has a supplier code | line | supplierItemCode | required | — | error | yes |
| Quantity must be greater than 0 | line | quantity | greater_than | 0 | error | yes |
| Unit price is required | line | unitPrice | required | — | error | yes |
| Every line has a description | line | description | required | — | warning | no |

---

## ④ Remove the redundant "Configure delivery" button

- Delete the button at `SupplierDockProfile.tsx:651-663` (it only switches to the Delivery
  tab, which is already in the tab bar).
- Optional polish: show a small "not set up" dot on the **Delivery** tab label when the
  supplier has no delivery config yet (the profile already loads supplier data; gate on the
  delivery-config presence). Core requirement is removal; the dot is nice-to-have.

---

## ⑤ Delete supplier

- Add a low-prominence **"Delete supplier"** action in the `SupplierDockProfile` header.
- Confirm dialog copy: *"Delete {supplier}? This removes it from your supplier list. Past
  orders are kept for audit. This can't be undone here."* Confirm / Cancel.
- On confirm: call `apiClient.deleteSupplier(id)`, then `useRouter().push("/suppliers")` and
  invalidate the suppliers query (`queryClient.invalidateQueries({ queryKey: ["suppliers"] })`
  — confirm the actual key during implementation).
- Handle the in-flight/disabled state and surface an inline error if the call fails.

---

## Files touched

**Backend (`ProcuLink`):**
- `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs` — OAuth2 branch.
- `ProcuLink.Infrastructure.Tests/...` — OAuth2 dispatcher tests (happy + 401).

**Frontend (`project-proculink`):**
- `src/components/bridge/DeliveryConfigEditor.tsx` — ② + ②b UI (bulk of the work).
- `src/lib/api/types` — add `"smtp"` to `DeliveryProtocol`; ensure `sftp`/`ftps` present.
- `src/components/bridge/SupplierDockProfile.tsx` — ③ explainer/fields/quick-add, ④ button
  removal, ⑤ delete action + dialog.

---

## Testing & verification

- **Backend:** `dotnet test` for the new OAuth2 tests; full suite stays green.
- **Frontend:** `bun run build` clean; verify against the live dev stack on `:8082` via
  DOM/HTTP checks (not screenshots — preview server contends on `.next` in this env).
- **②/②b proof:** Test-fire a real SFTP target and a real mailbox; for OAuth2, Test-fire
  against a token-protected endpoint and confirm the fetched token is used.
- **⑤ proof:** delete a throwaway supplier → it leaves the list → past orders still load.
- **Channel verification bar (per the matrix):** a checklist asserting every still-offered
  delivery protocol and upload format has a passing test (or a working Test-fire + honest
  "needs real endpoint" note). Erply/Directo: confirm their existing dispatcher path is
  exercised by a test-fire path even if the live ERP call is founder-side.

---

## Risks & mitigations

- **Wrong JSON field names → silent delivery failures.** Mitigated: field names in this spec
  are taken verbatim from the dispatcher POCOs (`SftpConfig/Credentials`, `FtpsConfig/…`,
  `SmtpConfig/…`).
- **OAuth token URL as an SSRF vector.** Mitigated: token URL runs through the same
  `OutboundRequestGuard` as the delivery URL.
- **Editor regressions for existing http/erp configs.** Mitigated: keep current http/erp
  branches intact; add new protocols alongside; `hydrateConfig` covers round-trip load.
- **Quick-add rules that never fire.** Mitigated: Field constrained to the resolver's known
  paths; quick-add templates use only those paths.
