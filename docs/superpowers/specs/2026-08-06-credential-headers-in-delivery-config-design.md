# Refusing a credential-bearing header in `config_json`

**Date:** 2026-08-06
**Branch:** `security/refuse-credential-headers-in-delivery-config`
**Based on:** `security/validate-revision-delivery-config` (PR #157, open) — **not** `main`

---

## 1. The defect

`SupplierDeliveryConfig.ConfigJson` documents an invariant in prose
(`ProcuLink.Core/Entities/SupplierDeliveryConfig.cs`, doc comment on `ConfigJson`):

> INVARIANT to preserve: never write a credential/secret into ConfigJson — if a new delivery option
> needs a secret, add it to the encrypted credential payload instead.

The HTTP delivery channel's extra-headers map violates it. `HttpDeliveryDispatcher` deserializes
`config_json` into `HttpConfig(Url, Method, Headers, TimeoutSeconds)` and applies every entry of
`Headers` to the outbound request:

```csharp
if (httpCfg.Headers is not null)
    foreach (var (k, v) in httpCfg.Headers)
        if (!HttpHeaderGuard.TryAdd(request.Headers, k, v))
            _logger.LogWarning("Skipping invalid delivery header name '{HeaderName}' …", k);
```

An operator who types `Authorization: Bearer <token>` — or `X-Api-Key`, or `Proxy-Authorization` —
into that map has the token stored in **cleartext** in `config_json`, returned verbatim by `GET`,
and copied wherever that blob travels, including connection-revision snapshots.

The correct route already exists and is a few lines above in the same method: the dispatcher applies
auth from the **decrypted** credential payload via `_auth.ApplyAsync(request, creds, client,
requestCt)`, which supports bearer, basic, apikey and oauth2. The operator has somewhere right to
put it. Nothing tells them so, and nothing stops them putting it in the wrong place.

This was found and deliberately deferred by PR #157, which named it as its own packet. This is that
packet.

### Why it is not fixed at dispatch

Refusing at dispatch would strand orders for every customer already doing this. The rule lands on
the **write** paths only. Pre-existing configs keep delivering, exactly as #151 and #157 both chose
for pre-enforcement endpoints.

---

## 2. The constraint that shapes the whole design

**The frontend has no headers field.**

`headers` is an *unmanaged* key in `DeliveryConfigEditor.tsx` (frontend repo):

```ts
const MANAGED_CONFIG_KEYS: Record<DeliveryProtocol, readonly string[]> = {
  http: ["url", "method", "timeoutSeconds"],
  …
```

Anything not in that list is carried through every save untouched, by design and pinned by a test
(`DeliveryConfigEditor.unknownKeys.test.tsx`: *"HTTP: per-supplier custom request headers survive an
unchanged save"*).

So a flat refuse-on-any-write would lock an affected operator out of **every** delivery-config save —
changing a timeout, fixing a URL, switching output format — with no UI anywhere to remove the
offending header. That is an outage, and it is precisely the trade #151 and #157 each refused to make.

The resolution is to refuse what the caller **introduces**, not what they merely echo back.

---

## 3. Architecture

### 3.1 One primitive, three consumers

Extends the existing `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs`. No new file for
the logic, no second hand-rolled copy — the divergence between two copies of a security rule is what
#157 exists to remove.

```csharp
/// True when this header name is one that conventionally carries a credential.
public static bool IsCredentialHeaderName(string? name)

/// Offending header names in the blob, document order, deduped case-insensitively.
/// Empty ⇒ allowed. `storedConfigJson` grandfathers a name+value pair already persisted.
public static IReadOnlyList<string> FindCredentialHeaders(
    string? configJson, string? storedConfigJson = null)

/// Operator-facing message naming the offending headers — never their values. Null when clean.
public static string? DescribeCredentialHeaders(string? configJson)

/// The composed read-path warning: transport fault ⊕ credential-header fault, joined.
public static string? DescribeConfigWarnings(string? protocol, string? configJson)
```

Every consumer reaches the same `FindCredentialHeaders`:

| Consumer | Call | Purpose |
|---|---|---|
| `DeliveryConfigService.UpsertAsync` | `FindCredentialHeaders(incoming, stored)` | refuse a new/changed header |
| `SupplierConnectionService.ApplyScalars` | `FindCredentialHeaders(incoming)` | refuse caller-supplied bundle |
| `DeliveryConfigService.ToResponse` | via `DescribeConfigWarnings` | tell the operator |
| `ConnectionsController.ToRevisionDto` | via `DescribeConfigWarnings` | tell the operator |
| `HttpDeliveryDispatcher` | via `DescribeCredentialHeaders` | log every attempt |

"May this config hold this header?" therefore has exactly one answer no matter who asks.

### 3.2 Three traps carried over from #157

**Duplicate `headers` key.** A JSON object may repeat a key and `System.Text.Json` keeps both:
`JsonDocument.EnumerateObject` lists them in document order, while `JsonSerializer.Deserialize` —
what the dispatcher uses — binds the **last**. So

```json
{"headers":{"X-Ok":"1"},"headers":{"Authorization":"Bearer secret"}}
```

would be validated as the clean map and delivered with the credential one. `FindCredentialHeaders`
inspects **every** `headers`-keyed object, not the first, exactly as `ExtractUrls` inspects every
url-keyed value. This is not reasoned about in the tests — it is confirmed against the real
`JsonSerializer`.

**Case-insensitive key lookup.** The dispatchers deserialize with `PropertyNameCaseInsensitive =
true`, so `{"HEADERS":{"AUTHORIZATION":…}}` reaches the wire. Both the outer key match and the header
name classification are case-insensitive.

**Not protocol-scoped.** A `headers` map is read only by `HttpDeliveryDispatcher` today, and declared
only on the http connector (`ConnectorManifestCatalog.cs:71`, *"JSON object of additional HTTP request
headers to include"*). Scoping the guard to a protocol list is a guard that goes stale in one
direction — a future protocol that grows a headers map inherits no protection and nothing fails.
Inspecting the `headers` key wherever it appears costs nothing, cannot produce a false refusal (no
other protocol stores anything under that key), and needs no list kept in sync.

### 3.3 The classifier

Case-insensitive, on the trimmed name. Two rules.

**Exact names:**

```
authorization           proxy-authorization     authentication
cookie                  set-cookie
x-api-key               api-key                 apikey              x-apikey
x-auth-token            x-authorization         x-access-token      x-auth-key
x-amz-security-token    x-goog-api-key          x-functions-key
ocp-apim-subscription-key                       private-token
x-shopify-access-token
```

**Segment rule.** Split the name on `-` and `_`. Refuse when any single segment is

```
token   secret   password   passwd   pwd   credential   credentials   apikey
```

or any adjacent pair, rejoined with `-`, is

```
api-key   access-key   secret-key   private-key   signing-key   session-key
```

Deliberately **not** bare `auth` and **not** bare `key`. That is the whole line between the two kinds
of mistake this guard can make:

| Header | Verdict | Why |
|---|---|---|
| `Content-Type` | allow | — |
| `X-Correlation-Id` | allow | — |
| `X-Supplier-Account` | allow | real tenant header already on the wire |
| `X-Idempotency-Key` | allow | `idempotency-key` is not a listed pair; bare `key` is not a segment |
| `X-Auth-Email` | allow | bare `auth` is not a segment (Cloudflare pairs it with the key) |
| `X-Sort-Key`, `X-Partition-Key` | allow | pair not listed |
| `Authorization` | refuse | exact |
| `X-Supplier-Token` | refuse | segment `token` |
| `X-Acme-Secret` | refuse | segment `secret` |
| `X-Foo-Api-Key` | refuse | pair `api-key` |
| `X-Client-Password` | refuse | segment `password` |

A false negative leaves a secret in cleartext that the operator chose to name obscurely. A false
positive hard-blocks a legitimate save with no UI workaround. Given §2, the list is deliberately
precise rather than aggressive.

### 3.4 Grandfathering (live delivery-config path only)

`FindCredentialHeaders(incoming, stored)` refuses an offending header **unless the identical
`(name, value)` pair is already present in `stored`.**

- name compared case-insensitively
- value compared by decoded string when both sides are JSON strings, otherwise by raw JSON text —
  so a re-serialisation that changes only escaping is not treated as a change
- `stored` null or absent ⇒ nothing is grandfathered

| Operator action | Result |
|---|---|
| saves an unchanged config that already had `Authorization` | **allowed** — not a write of a secret |
| adds `Authorization` | **refused** |
| changes the value of an existing `Authorization` (token rotation) | **refused** |
| removes `Authorization` | **allowed** |

This is the same old-vs-new shape `UpsertAsync` already uses for
`DeliveryHostKeyConfig.PreserveRecordedFingerprints`, in the same method.

---

## 4. Write paths

| Path | Reaches | Behaviour | Rationale |
|---|---|---|---|
| Live delivery-config upsert | `DeliveryConfigService.UpsertAsync` | refuse **added or changed** | no UI to remove the header (§2) |
| Revision create (with bundle) | `ApplyScalars` | **refuse** | caller-supplied, mirrors #157 |
| Revision update | `ApplyScalars` | **refuse** | caller-supplied, mirrors #157 |
| Clone-from-active | internal copy | allowed, warned | #157: refusing turns a weakness into an outage |
| Rollback | internal copy | allowed, warned | restoring a version that was live before |
| Republish-from-live | internal copy | allowed, warned | **the path the delivery-config editor triggers** |
| Publish | status flip | allowed, warned | no endpoint written |
| V1 backfill | internal copy | allowed, warned | mirrors the live row |

Republish-from-live never passes through `ApplyScalars`, so the ordinary operator flow — edit the
delivery config, which republishes a revision — keeps working after a grandfathered save. The
revision path's flat refusal therefore strands only the case #157 already accepted and pinned: hand
-editing a draft bundle that predates enforcement.

### Placement

`UpsertAsync` currently validates before it fetches:

```csharp
var protocol = NormalizeProtocol(request.Protocol);
ValidateConfigJson(request.ConfigJson);
ValidateTransportSecurity(protocol, request.ConfigJson);

var now = DateTime.UtcNow;
var existing = await _db.SupplierDeliveryConfigs…FirstOrDefaultAsync(ct);
```

The new check needs `existing`, so it goes immediately after the fetch and **before** the
`if (existing is null)` block that reassigns `existing` to a fresh entity whose `ConfigJson` is
`"{}"`. Before any mutation, matching the ordering comment `ApplyScalars` already carries.

In `ApplyScalars` it joins the two existing validators, ahead of every assignment.

### Refusal shape

New `CredentialHeaderInConfigException : ArgumentException`, co-located in
`DeliveryConfigTransport.cs` (the precedent: `OutboundUrlPolicyException` lives in
`OutboundUrlPolicy.cs`, `ClientSuppliedCredentialsRefException` in `ISupplierConnectionService.cs`).
Carries `Code`, `PolicyMessage` and `HeaderNames`.

- `Code` = `credential_header_in_delivery_config`
- `PolicyMessage`, one header:

  > Delivery config header 'Authorization' holds a credential. This config is stored in cleartext, so
  > credentials belong in this supplier's delivery credentials — set the auth type there to bearer,
  > basic, apikey or oauth2_client_credentials — where they are encrypted. Remove the header and save
  > the token as a credential instead.

- multiple: `headers 'Authorization', 'X-Api-Key' hold credentials.` then the same guidance
- **never** the value

The destination named in the message is real and already described in the connector manifest: the
credentials block carries `type` (`none | apikey | bearer | basic | oauth2_client_credentials`) and,
for apikey, `header` + `value` — *"Header name for apikey auth (e.g. X-Api-Key)"*
(`ConnectorManifestCatalog.cs:74-77`). An operator following the refusal lands on a field that exists.

`400 { error, message }` from explicit catches in `ConnectionsController.CreateDraft` /
`UpdateDraft` (beside the two #157 added) **and** in `SuppliersController`, placed before its
existing generic `catch (ArgumentException)` so this endpoint returns the same machine-readable
shape rather than falling through to `{ error: "<message> (Parameter 'ConfigJson')" }`. Deriving
from `ArgumentException` keeps any handler that is not updated at 400 rather than 500.

---

## 5. Read and dispatch surfaces

`DeliveryConfigResponse.InsecureTransportWarning` and `ConnectionRevisionDto.InsecureTransportWarning`
both switch from `DescribeInsecureTransport` to `DescribeConfigWarnings`, so the field carries either
fault or both joined. Each DTO has exactly one build site — `DeliveryConfigService.ToResponse` and
`ConnectionsController.ToRevisionDto` — so they cannot drift. Doc comments on both fields are widened
to say the field now reports two kinds of fault.

Reusing the field rather than adding a sibling is what gets the message in front of operators
immediately: the frontend already renders it, so nothing is invisible while a second field waits on a
frontend release.

### 5.1 The banner wrapper must be made fault-agnostic (companion frontend change)

Reuse has one consequence that has to be fixed rather than accepted. The frontend does not render the
warning alone — it wraps it in transport-specific copy (`DeliveryConfigEditor.tsx:1474-1485`):

> **This supplier's saved endpoint is not secure.** *{insecureTransportWarning}* Orders are still
> being delivered to it, so nothing has stopped — but until **the address is corrected here**, every
> order and its credentials cross the network in the clear.

For a credential-header fault that heading is wrong and the trailer actively misdirects: it tells the
operator to correct the address, which is not the fix, and "corrected here" points at a field that has
nothing to do with the header. A correct message inside misleading chrome is worse than no message.

So this packet ships **two PRs**, backend first:

1. **Backend (this branch)** — the primitive, both write paths, both DTOs, the dispatch log.
2. **Frontend (companion, small)** — make the banner heading and trailer fault-agnostic, or render the
   two faults as two banners. `DeliveryConfigEditor.tls.test.tsx:140-162` pins the current wording and
   is updated with it; a new case covers the header fault. The only files touched are
   `DeliveryConfigEditor.tsx` and its tests, so it stays file-disjoint from any other chip in flight.

The backend change is safe to merge before the frontend one — a header-fault warning rendered under
the old heading is imprecise, not false, and no secret is echoed either way — but the frontend PR is
part of this packet and not a follow-up.

`HttpDeliveryDispatcher` gains `WarnIfCredentialHeaders(config)` beside the existing
`WarnIfInsecureTransport(config, endpoint)` — once per delivery attempt, **header names only, never a
value**, with its own wording (the transport line's "predates TLS enforcement" is wrong here).

The ERP connectors build their own headers and never read a config headers map, so they are not
touched.

---

## 6. Migration

1. **Every affected operator is told, immediately.** The frontend already renders
   `insecureTransportWarning` (`DeliveryConfigEditor.tsx:1474`, pinned by
   `DeliveryConfigEditor.tls.test.tsx`), so the composed message reaches the delivery editor the
   moment the backend ships. It names the header and the destination, never the token. The banner's
   surrounding copy is corrected by the companion frontend PR in §5.1.
2. **They are never blocked meanwhile.** Grandfathering means unrelated edits keep saving while they
   move the token.
3. **The refusal bites at exactly the right moment.** Rotating the token requires changing the value,
   which is refused, with a message saying where it goes instead.
4. **Anything nobody opens still surfaces.** The dispatch-time log fires on every attempt, naming the
   header.
5. **The invariant doc becomes true.** `SupplierDeliveryConfig.ConfigJson`'s comment is extended to
   record that the header case is now enforced on write, and that pre-existing pairs are grandfathered
   until they change.

---

## 7. Tests

Five files. Every guard mutation-checked: remove the call, confirm red, restore **by editing** until
`git diff HEAD` is empty — never `git checkout <file>`, which has destroyed uncommitted work in this
repo.

| File | Covers |
|---|---|
| `ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs` | classifier table (both verdicts), extraction, duplicate-key, grandfather matrix, real-`JsonSerializer` cross-check |
| `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs` | `UpsertAsync` refuse/allow/grandfather, DB re-read, `GET` warning |
| `ProcuLink.Api.Tests/Services/ConnectionRevisionCredentialHeaderTests.cs` | `ApplyScalars` flat refusal; internal-copy paths still allowed |
| `ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs` | 400 body shape, revision DTO warning |
| `ProcuLink.Api.Tests/Controllers/SuppliersControllerDeliveryConfigCredentialHeaderTests.cs` | `SuppliersController` 400 body shape (no existing delivery-config controller test file to extend) |
| `ProcuLink.Infrastructure.Tests/Services/Dispatchers/HttpDeliveryDispatcherTests.cs` (extended) | log names the header, not the value |

The companion frontend PR (§5.1) carries its own: `DeliveryConfigEditor.tls.test.tsx` updated for the
fault-agnostic wording, plus a case asserting a credential-header warning renders without the
"correct the address" trailer and without echoing a token.

Non-negotiable properties:

- **Allowance asserted alongside every refusal.** `Content-Type`, `X-Correlation-Id`,
  `X-Supplier-Account`, `X-Idempotency-Key`, `X-Auth-Email` all still save. A rule that refused
  everything would otherwise pass a refusal-only suite.
- **Refusal asserted before any `NotContain(token)`.** Disabling a check must not leave the
  "does not contain" assertions passing vacuously.
- **Duplicate-`headers` bypass confirmed against the real serializer** — assert that
  `JsonSerializer.Deserialize<…>` binds the credential-bearing map, then assert the guard catches it.
- **Grandfather matrix** — unchanged saves, added refuses, value-changed refuses, removed saves.
- **DB re-read** asserting the token appears nowhere in the persisted `config_json`, with a positive
  control in the same test so it cannot pass by refusing everything.
- **Anti-vacuity floor** on any walk over a name list, so an empty list cannot make a walk assert
  nothing.

---

## 8. Out of scope

- **A frontend headers editor / removal affordance.** Grandfathering removes the need for it as a
  blocker. Worth its own frontend packet so an operator can clear the header from the UI instead of
  waiting for a rotation.
- **A broader `config_json` secret scan.** This guard is header-scoped by design. Whether any other
  config key on any other protocol can carry a secret is a separate question and a separate packet.
- **AAD binding on delivery credentials** (`DeliveryEncryptionService`) — a ciphertext format change,
  already logged by #157 as its own packet.
- **Retro-scrubbing stored headers.** Nothing is rewritten. Deleting a customer's live credential out
  from under them, without their knowing where it went, would break delivery.
