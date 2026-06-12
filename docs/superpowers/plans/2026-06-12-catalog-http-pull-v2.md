# Catalog HTTP-API pull + auth methods (v2 addendum)

Extends 2026-06-12-catalog-import-channels.md. Founder ask: query a supplier's catalogue
from their API over HTTP(S) with common auth methods (not just SFTP/FTP pull / push).

## What exists to reuse (do NOT reinvent)
- `OutboundRequestGuard.CreateGuardedHttpHandler()` — connect-time-revalidating SocketsHttpHandler;
  blocks DNS-rebind + redirect-to-private-IP at TCP connect. THIS is the only HTTP handler to use.
- `HttpDeliveryDispatcher.ApplyAuthAsync` + `FetchOAuthTokenAsync` — the exact auth model:
  none | apikey (header+value) | bearer (token) | basic (user+pass) | oauth2_client_credentials
  (token_url, client_id, client_secret, scope?). EXTRACT this into a shared helper
  (e.g. Core/Infra `HttpAuthApplier`) so catalog + delivery share ONE implementation (no drift).
- `CatalogPullService` (Infra/Services/Ingress) — guard→deadline→bounded read→parse→upsert sink.
  Add an `http`/`https` branch to its `DownloadAsync` protocol switch.
- `BoundedRead.CopyAsync` (cap+1) — response stream read.
- `SupplierCatalogFileParser` — CSV/XLSX. ADD a JSON-array parser (objects → SupplierProduct):
  alias-detect code/description/price/uom/currency keys (mirror the CSV alias auto-detect);
  bounded element count (reuse MaxCatalogRows). Content-type/extension routes csv/xlsx/json.
- `SupplierCatalogSource` entity + the catalog-source CRUD/test-fetch endpoints + FE
  CatalogSourceEditor (protocol picker, write-only creds, Test fetch, last-sync).

## Schema (ONE additive migration — `AddCatalogHttpSource`)
Add to `supplier_catalog_sources` (all nullable; sftp/ftp rows leave them null):
- `url` text — full request URL for http/https (scheme+host+path+query). Host/port/path stay for sftp/ftp.
- `auth_method` text — 'none'|'apikey'|'bearer'|'basic'|'oauth2_client_credentials' (null for sftp/ftp).
- `auth_config_encrypted` text — AES-GCM blob of the auth secrets JSON (write-only; never returned).
- `http_method` text nullable default 'GET' (allow POST for APIs that require it; body optional later — v2 = GET + optional configured body string is OUT of scope, GET only unless trivial).
Protocol column now also accepts 'http'|'https'.

## Security (MANDATORY — these are the deferred v1 H5/L4 dispositions, now in scope)
1. Fetch ONLY through `OutboundRequestGuard.CreateGuardedHttpHandler()` (redirect+rebind safe).
   Plus an up-front `OutboundRequestGuard.ValidateAsync(url)` at save AND before each fetch.
2. REJECT `new Uri(url).UserInfo != ""` at save (400 `credentials_in_url_not_allowed`) — creds go in
   auth_config, never the URL (no leak to logs/Sentry/db).
3. auth_config AES-GCM encrypted at rest; GET masks (hasAuth + method only, never secrets) like delivery.
4. Bounded read (cap+1 → CatalogFileTooLargeException); 30s timeout + the existing 5-min overall deadline.
5. Sanitized enumerated transport errors (no host/cred/token leak); raw at Debug only; reuse the
   catalog error-mapper. OAuth token-fetch failures → safe "authentication failed" message.
6. https strongly preferred — warn (not block) on plain http in FE (like the plain-FTP warning);
   never disable cert validation.
7. Org-scoped; billing-gated same as sftp/ftp catalog sync (SftpIngestion → Growth+).

## Tasks
BE (worktree, ONE migration, full dotnet test gate):
- B1. Extract shared `HttpAuthApplier` from HttpDeliveryDispatcher.ApplyAuthAsync/FetchOAuthTokenAsync;
  rewire the dispatcher to it (behaviour byte-identical; its tests stay green).
- B2. Migration AddCatalogHttpSource (4 columns). Entity + DbContext config.
- B3. CatalogPullService http/https branch: guarded client + auth applier + bounded read + JSON/CSV/XLSX parse.
- B4. JSON catalog parser in Transform (alias-detected fields, row cap).
- B5. Settings service: http save path (url required, Uri.UserInfo reject, auth_config encrypt write-only,
  guard pre-check); GET masks auth; test-fetch supports http (honesty report: status, content-type,
  detected format, parsed rows, sample). 
- B6. Tests: auth applier (all 5) shared; SSRF-blocked http host + redirect-to-private blocked (no fetch);
  Uri.UserInfo rejected; oversize bounded; JSON parse + alias; masked GET (no secret/ciphertext);
  oauth token-fetch failure → safe message; existing delivery dispatcher tests still green.
FE (after BE merge — CatalogSourceEditor): add HTTP/HTTPS to the protocol picker; when http, show
URL field + auth-method picker (none/api key header+value/bearer/basic/oauth2 client-credentials with
token URL+client id+secret+scope) — write-only credential fields mirroring DeliveryConfigEditor's auth UI
(that component already has this exact auth form — reuse it); plain-http cleartext warning; Test fetch
shows the honesty report. Honesty: claim only the tested auth methods.

OUT of scope v2: GraphQL, paginated catalog APIs (multi-page fetch), per-supplier response field-mapping
UI (use alias auto-detect + a later mapping editor), request body templating, mTLS.
