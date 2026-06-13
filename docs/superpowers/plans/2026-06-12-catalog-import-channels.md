# FINAL: Supplier Catalog Import Channels — implementation plan (v1, post-review)

**Merged from:** design 2026-06-12 + adversarial security review + scope review. Scope-review's minimal v1 is adopted wholesale. All accepted security fixes are applied inline in the task specs below. FluentFTP 54.2.0 API facts re-verified against the local package (`FtpDataConnectionType.PASVEX`, `FtpConfig.DataConnectionEncryption`, `FtpConfig.ValidateAnyCertificate`, `FtpConfig.ConnectTimeout/ReadTimeout/DataConnectionConnectTimeout/DataConnectionReadTimeout` all exist). Rate-limit partition fact re-verified: `ProcuLink.Api\Program.cs:217-220` keys on `sub` claim → IP, and `ProcuLink.Api\Auth\ApiKeyAuthHandler.cs:92-95` issues `key_id` but no `sub` — the M3 fix below is therefore one line.

---

## A. Review-finding dispositions

### Security review

| # | Finding | Decision | Why / how |
|---|---|---|---|
| H1 | FTP/FTPS PASV data-channel SSRF | **ACCEPTED** | Real bypass; guard only checks control host. Fix: new FTP factory sets `Config.DataConnectionType = FtpDataConnectionType.PASVEX` (passive, but **ignores the server-advertised PASV IP and reuses the already-validated control-connection host** — verified present in FluentFTP 54.2.0). Config built by a pure, unit-testable `BuildFtpConfig()` helper. (BE-4) |
| H2 | Size cap after full materialization → OOM | **ACCEPTED** (merged with scope cut #6) | One bounded-read path: seam method `DownloadAsync(path, maxBytes, ct)` copies at most `MaxFileBytes + 1` bytes and throws `CatalogFileTooLargeException`; no pre-flight `SIZE`/attr probes at all. SFTP gains additive `ISftpSession.OpenRead(string remotePath)` (SSH.NET `SftpClient.OpenRead`) so the catalog path streams; existing `DownloadFile` untouched. Sibling order-poller fix → SEC-1. (BE-4) |
| H3 | No SFTP connect timeout → Worker thread exhaustion | **ACCEPTED** | `RenciSftpClientFactory`: set `ConnectionInfo.Timeout` + `OperationTimeout` ≈ 30 s (strict improvement; also hardens the existing order poller — acceptable shared-factory change). FluentFTP factory: `ConnectTimeout`/`ReadTimeout`/`DataConnectionConnectTimeout`/`DataConnectionReadTimeout` = 30 s. `CatalogPullService` wraps each pull in a linked CTS with a 5-minute overall deadline. (BE-4) |
| H4 | XLSX decompression bomb — row cap runs too late | **ACCEPTED** | Guard **before** `new XLWorkbook`: open the stream as `ZipArchive`, reject if any entry's declared uncompressed `Length` > 64 MB or total > 128 MB; after load, reject if declared used-range row count > `MaxCatalogRows` before iterating cells; count rows during iteration with early break as backstop. (BE-1) |
| H5 | HTTP-pull creds in plaintext URL → DB/Sentry leak | **MOOT** | HTTP pull is cut from v1 (scope cut #1). Recorded as a v2 requirement: first-class AES-GCM-encrypted auth header + reject `Uri.UserInfo != ""` at save. |
| M1 | `sync-now` enqueue flooding | **MOOT + residual accepted** | `sync-now` endpoint is cut (scope cut #4). The replacement (PUT enqueues on `IsEnabled` false→true transition) no-ops when `LastSyncStatus == "running"` or `LastSyncAt` within the last 5 min, and PUT is Clerk-authed under the global limiter. (BE-3) |
| M2 | Non-unique `Supplier.Code` → wrong-supplier push | **MOOT** | Code resolution is cut; push is GUID-only (scope cut #3). The unique-index suggestion is rejected for v1 — no code lookup exists to protect. Revisit if/when code resolution ships. |
| M3 | ApiKey rate limit partitions by IP → evadable; push unmetered | **STILL OPEN — accepted residual** (security review 2026-06-12 corrected the earlier "resolved" wording) | `ApiKeyAuthHandler` adds `new Claim("sub", $"apikey:{apiKey.Id}")`, BUT given the middleware order (`UseAuthentication` [default scheme = JwtBearer] → TenantResolution → `UseRateLimiter` → `UseAuthorization`) the ApiKey scheme only authenticates lazily at `[Authorize(Schemes="ApiKey")]` time — AFTER the limiter. So at partition time `ctx.User` is anonymous and `PartitionKey` falls back to remote IP; the `sub` claim exists but the limiter never reads it. The regression test only proves the claim is on the principal, not that the limiter partitions by it. **Impact: low/accepted** — every IP partition stays capped and the sink is an idempotent upsert, so it's bounded, not exploitable beyond this documented residual. **To actually close it** (deliberately, NOT as a launch rider — reordering global middleware affects the whole API): move `app.UseRateLimiter()` after `app.UseAuthorization()`, OR run ApiKey during `UseAuthentication` via a forwarding default scheme, then re-confirm. Per-org daily catalog budget: **REJECTED for v1** — push requires the org's own API key, the sink is an idempotent upsert, and there is no catalog-quota billing machinery to hang it on. Noted as v2 if abuse appears. (BE-7) |
| M4 | Raw FluentFTP/SSH.NET errors leak host/username to `last_sync_error`/Sentry | **ACCEPTED** | Map transport exceptions to a small enumerated set of safe messages (precedent: `FtpsDeliveryDispatcher.cs:144-169`); persist only the mapped message; log the raw exception at Debug; rethrow `CatalogSyncException(safeMessage)` **without** the inner exception so Hangfire/Sentry capture only sanitized text. (BE-4/BE-6) |
| M5 | `Delivery:AllowPrivateNetworkTargets` = global SSRF kill-switch | **ACCEPTED-LITE** | Loud Error-level startup log (+ Sentry message when environment is Production) when the flag is true. Hard fail-closed-in-Production: **REJECTED for this batch** — it changes the behavior of an existing shared security control under all delivery dispatchers; do it deliberately in SEC-1 review, not as a rider. (SEC-1) |
| M6 | FTPS data-channel cleartext + invalid-cert escape hatch | **ACCEPTED** | New factory sets `Config.DataConnectionEncryption = true` and `Config.ValidateAnyCertificate = false` for ftps; no `AllowInvalidCertificate` field exists on the entity (and stays out). Retrofitting `FtpsDeliveryDispatcher` itself → SEC-1 note (don't change live delivery behavior inside a catalog feature). (BE-4) |
| L1 | DNS-rebinding TOCTOU on non-HTTP | **ACCEPTED-RISK** | Same documented residual as the delivery dispatchers; PASVEX (H1) closes the separate PASV hole. Document in code comment, as `SftpDeliveryDispatcher.cs:66-69` does. |
| L2 | test-fetch as timing oracle | **ACCEPTED-RISK** | Guard blocks private ranges; keep the `"upload"` 20/min policy. No change. |
| L3 | Plain FTP cleartext | **PARTIAL** | FE warning banner kept (was already in design). Org-level "forbid ftp" policy flag: **REJECTED v1** — config surface with no current customer demand; revisit on request. |
| L4 | HTTP redirect handling | **N/A** | HTTP pull cut; the note (never replace the guarded handler's redirect behavior) is preserved for v2. |
| X-cut 1 | One hardened fetch helper for all channels | **ACCEPTED** | `CatalogPullService` owns guard-before-connect, deadline, bounded read, hash, parse, upsert; per-protocol seams only open streams. |
| X-cut 2 | BE-8 sibling fix must include H2/H3, not just guard calls | **ACCEPTED** | Folded into SEC-1 spec below. |
| X-cut 3 | New SSRF regression tests (PASV, oversize, stall, forged XLSX dimension) | **ACCEPTED** | Distributed into BE-1/BE-4 test lists. |

### Scope review

| # | Item | Decision |
|---|---|---|
| 1 | Cut HTTP pull; keep FTPS | **ACCEPTED** — protocols are `sftp | ftp | ftps`. Also moots H5/L4. Schema unchanged (Protocol is text; HTTP later is code-only). |
| 2 | Cut JSON push body | **ACCEPTED** — multipart + raw CSV only; both reuse the extracted parser byte-for-byte. |
| 3 | GUID-only supplier segment on push | **ACCEPTED** — moots M2. FE prints the GUID endpoint anyway. |
| 4 | Cut `sync-now`; PUT enqueues on enable-transition | **ACCEPTED** — with the M1-residual dedupe guard. |
| 5 | Cut partial `WHERE is_enabled` index | **ACCEPTED** — dozens of rows for years; additive later. |
| 6 | Replace dual size cap with one bounded read | **ACCEPTED** — and it is also the H2 fix; FTP seam shrinks to a single download method. |
| 7 | Decouple BE-8 sibling SSRF fix into its own PR | **ACCEPTED** — now **SEC-1**, expanded per security cross-cutting rec #2 (guard + bounded read + timeouts), plus the M5 startup warning. |
| — | Keep all schema columns; trim UI instead (no format selector, no interval select) | **ACCEPTED** |
| — | Keep test-fetch honesty report, hash-skip, soft lock, persist-failed-before-rethrow, save-time guard, per-source child jobs | **ACCEPTED** |
| — | Single-method FTP seam; `ICatalogSourceSettingsService` acceptable house style | **ACCEPTED** |

---

## B. Final architecture (one paragraph)

Three transports — **API push (key-authed), SFTP pull, FTP/FTPS pull** — feed one idempotent sink: `ISupplierCatalogService.UpsertManyAsync` (unique `(org_id, supplier_id, code)`, `ProcuLinkDbContext.cs:553`). The CSV/XLSX parser is extracted from `SuppliersController.cs:795-918` into `ProcuLink.Transform\Catalog\SupplierCatalogFileParser` (shared by Api upload, Api push, Worker pull) and hardened against zip bombs (H4) with a 50k row cap. Pull config lives in one new table `supplier_catalog_sources` (one migration, one source per supplier via unique `(org_id, supplier_id)`), credentials AES-256-GCM via `DeliveryEncryptionService`, masked on GET, blank-keep/empty-clear on PUT (precedent `PullIngressSettingsService.cs:58-63`). `CatalogPullService` is the single hardened fetch pipeline: `IOutboundRequestGuard.ValidateHostAsync` immediately before connect → 30 s connect/op timeouts + 5-min job deadline (H3) → bounded streaming read capped at `IngressLimits.MaxFileBytes` (H2) → SHA-256 vs `LastFileHash` skip → parse → upsert → honest `last_sync_*` status with enumerated safe error messages (M4). FTP/FTPS uses a new single-method FluentFTP seam configured `PASVEX` + `DataConnectionEncryption=true` + `ValidateAnyCertificate=false` (H1/M6). Worker runs an hourly dispatcher fanning out one Hangfire child per due source with the `running` soft lock. Push is one `IngressController` route (GUID-only, multipart or raw CSV, 10 MB / 50k caps, `"upload"` rate policy now per-key via the new `sub` claim — M3). Test-fetch is a saved-config, read-only probe returning `mappedFields`/`unmappedColumns`/≤5 sample rows.

## C. Schema (BE-2) — unchanged from design except: no partial index; protocol set is `sftp|ftp|ftps`

`ProcuLink.Core\Entities\SupplierCatalogSource.cs` → table `supplier_catalog_sources`:

```
Id uuid PK · OrgId uuid FK organisations · SupplierId uuid FK suppliers
Protocol text NOT NULL            -- 'sftp' | 'ftp' | 'ftps'   (http reserved for v2; column is text)
Host text NOT NULL · Port int NOT NULL (defaults 22 / 21 / 21)
Username text NULL                -- required sftp/ftps; ftp may be 'anonymous'
EncryptedPassword text NULL       -- AES-GCM envelope (DeliveryEncryptionService format)
RemotePath text NOT NULL          -- exact remote FILE path
FileFormat text NOT NULL DEFAULT 'auto'        -- 'auto'|'csv'|'xlsx' (no FE selector in v1)
SyncIntervalHours int NOT NULL DEFAULT 24      -- server-clamped [1, 336] (no FE selector in v1)
IsEnabled bool NOT NULL DEFAULT false
LastSyncAt timestamptz NULL · LastSyncStatus text NULL  -- 'running'|'ok'|'unchanged'|'failed'
LastSyncError text NULL           -- ≤500 chars, enumerated safe messages only (M4)
LastSyncCreated int NULL · LastSyncUpdated int NULL · LastSyncSkipped int NULL
LastFileHash text NULL            -- SHA-256 hex
CreatedAt, UpdatedAt timestamptz NOT NULL
UNIQUE (org_id, supplier_id)
```

Snake_case per `ProcuLinkDbContext.cs:534-568` conventions; add to InMemory test `Ignore` lists if the harness requires; **verify migration on real Postgres** (memory: InMemory masks FK/migration issues).

---

## D. Ordered task list

Dependencies: BE-1 → (BE-7 parallel) · BE-2 → BE-3 ∥ BE-4 · BE-4 → BE-5 ∥ BE-6 · FE-1 after BE-3/BE-5 contracts settle (can start from the DTO shapes in this doc). SEC-1 is a fully independent worktree/PR. Each BE task = its own worktree.

### BE-1 — Shared parser extraction + row cap + zip-bomb guard (H4)
**Files:**
- NEW `ProcuLink.Transform\Catalog\SupplierCatalogFileParser.cs`
- EDIT `ProcuLink.Api\Controllers\SuppliersController.cs` (delegate import endpoint :727-774; delete private statics :795-918)
- NEW `ProcuLink.Transform.Tests\Catalog\SupplierCatalogFileParserTests.cs`

**Scope:** Move `CatalogColumnAliases`, `RowToDraft`, CSV parser, XLSX parser verbatim into a public static class in Transform (it references Core + has ClosedXML; Infrastructure already references Transform → visible everywhere). Public surface: `ParseCsv(Stream)`, `ParseXlsx(Stream)`, `ParseByFileName(Stream, string fileName)` (extension routing identical to current :747-753). Add `MaxCatalogRows = 50_000` → typed `CatalogTooLargeException`. **H4 guard, before `new XLWorkbook`:** open the stream as `System.IO.Compression.ZipArchive`; reject if any entry's declared uncompressed `Length` > 64 MB or summed > 128 MB; after workbook load, reject if the declared used-range row count > `MaxCatalogRows` **before** reading cells; count rows during iteration with early break as backstop. CSV path counts rows during read and aborts at the cap (never materializes >50k drafts).

**Tests:** existing `ProcuLink.Api.Tests\Controllers\SuppliersControllerCatalogTests.cs` stays green **unmodified** (behavior-preservation gate). New Transform tests: alias mapping parity CSV+XLSX; row-cap abort on 50_001-row CSV; XLSX with forged dimension (small file declaring ~1M-row used range) rejected before cell iteration; oversized-declared-zip-entry rejected; EU comma-decimal regression rows (locale-bug memory) still parse identically.

### BE-2 — Entity + ONE migration
**Files:**
- NEW `ProcuLink.Core\Entities\SupplierCatalogSource.cs`
- EDIT `ProcuLink.Infrastructure\ProcuLinkDbContext.cs` (entity config, snake_case, unique `(org_id, supplier_id)`)
- NEW migration `AddSupplierCatalogSources` (+ snapshot)
- EDIT test-scoped InMemory `Ignore` lists if the harness requires

**Scope:** Exactly §C. No partial index. No other schema change.
**Tests:** migration applies cleanly on real Postgres (Testcontainers or local 5435 — not InMemory); unique constraint enforced; FK to suppliers/organisations valid.

### BE-3 — Config endpoints + settings service
**Files:**
- NEW `ProcuLink.Core\Services\Catalog\ICatalogSourceSettingsService.cs` (+ request/response records)
- NEW `ProcuLink.Infrastructure\Services\Catalog\CatalogSourceSettingsService.cs`
- EDIT `ProcuLink.Api\Controllers\SuppliersController.cs` (3 endpoints)
- EDIT `ProcuLink.Api\Program.cs` (DI)
- NEW `ProcuLink.Api.Tests\Controllers\SuppliersControllerCatalogSourceTests.cs`

**Scope:** All routes behind the existing `SupplierExistsAsync` org-scope + soft-delete check (404 on foreign supplier).
- `GET /api/suppliers/{id}/catalog/source` → `{ source: null }` or masked DTO (`hasPassword`, `passwordDisplay: "********"`, never ciphertext — mirror `PullIngressSettingsService.ToSftpResponse`).
- `PUT /api/suppliers/{id}/catalog/source` — upsert. Password semantics per precedent: `null`=keep, `""`=clear, value=re-encrypt. Validation: protocol ∈ {sftp, ftp, ftps}; Host+Port required; Username required for sftp/ftps (ftp anonymous OK); `SyncIntervalHours` clamped [1,336]. **Save-time SSRF pre-check** `ValidateHostAsync(host, port)` → 400 `{ error: "host_not_allowed" }`. **Billing gate on `IsEnabled=true`:** `BillingFeature.SftpIngestion` → 403 `{ error: "catalog_sync_requires_integration", upgradeUrl }` (mirror `SettingsController.cs:130`). **Enable-transition enqueue (replaces sync-now):** when `IsEnabled` flips false→true, enqueue `CatalogSyncSourceJob(orgId, sourceId)` via Hangfire client — **no-op if `LastSyncStatus == "running"` or `LastSyncAt` within 5 min** (M1 residual).
- `DELETE /api/suppliers/{id}/catalog/source` → `{ deleted: true }`.

**Tests:** keep/clear/replace password semantics; masking (no ciphertext in GET); billing-gate 403; foreign-supplier 404; guard-reject 400; clamp; enable-transition enqueues exactly once and dedupes when running/recent; disable does not enqueue.

### BE-4 — Hardened fetchers + `CatalogPullService` (H1, H2, H3, M4, M6 inline)
**Files:**
- EDIT `ProcuLink.Infrastructure\Services\Ingress\ISftpClientFactory.cs` (additive `Stream OpenRead(string remotePath)` on `ISftpSession`)
- EDIT `ProcuLink.Infrastructure\Services\Ingress\RenciSftpClientFactory.cs` (H3: `ConnectionInfo.Timeout` + `OperationTimeout` = 30 s; implement `OpenRead` via `SftpClient.OpenRead`)
- NEW `ProcuLink.Infrastructure\Services\Ingress\IFtpFetchClientFactory.cs` — single-method seam: `IFtpFetchSession Connect(host, port, user, pass, bool explicitTls)`; `IFtpFetchSession : IDisposable { Task<MemoryStream> DownloadAsync(string remotePath, long maxBytes, CancellationToken ct); }`
- NEW `ProcuLink.Infrastructure\Services\Ingress\FluentFtpFetchClientFactory.cs` (+ internal static `BuildFtpConfig(bool explicitTls)` pure helper)
- NEW `ProcuLink.Core\Services\Catalog\ICatalogPullService.cs`
- NEW `ProcuLink.Infrastructure\Services\Catalog\CatalogPullService.cs` (+ `CatalogSyncException`, bounded-copy helper)
- EDIT `ProcuLink.Api\Program.cs` + `ProcuLink.Worker\Program.cs` (DI: `IFtpFetchClientFactory`, `ICatalogPullService`)
- NEW tests in `ProcuLink.Infrastructure.Tests\Services\Catalog\CatalogPullServiceTests.cs` + `...\Ingress\FluentFtpConfigTests.cs`

**Scope:**
- **FluentFTP config (H1/M6/H3):** `BuildFtpConfig` sets `DataConnectionType = FtpDataConnectionType.PASVEX` (pin data connection to the validated control host — the PASV-SSRF fix), `DataConnectionEncryption = true` and `ValidateAnyCertificate = false` when ftps (`EncryptionMode = Explicit`), `ConnectTimeout = ReadTimeout = DataConnectionConnectTimeout = DataConnectionReadTimeout = 30_000`. No invalid-cert opt-in anywhere.
- **Bounded read (H2):** all downloads stream through one helper copying at most `IngressLimits.MaxFileBytes + 1` bytes; on overflow throw `CatalogFileTooLargeException` → status `failed` / "Catalog file exceeds 10 MB". No `SIZE`/attr pre-probes. SFTP path uses the new `OpenRead` + the same helper.
- **Pipeline:** reload source org-scoped → decrypt password (`Decrypt` null → `failed` / "Stored credentials could not be read — re-enter the password") → supplier soft-deleted → `failed` / "Supplier no longer exists" → `ValidateHostAsync(host, port)` **immediately before connect** (every poll AND test-fetch; comment the documented DNS-rebind TOCTOU residual, L1) → connect under a linked CTS with a **5-minute overall deadline** (H3) → bounded download → SHA-256; if equal to `LastFileHash` and last status ok/unchanged → record `unchanged`, stop → `SupplierCatalogFileParser.ParseByFileName` (or forced `FileFormat`) → `UpsertManyAsync` → persist counts + hash.
- **Error mapping (M4):** map `SshException`/`FtpException`/socket/timeout/parse failures to enumerated safe messages (precedent `FtpsDeliveryDispatcher.cs:144-169`); raw exception logged at Debug only; rethrow `CatalogSyncException(safeMessage)` **without inner exception** so Hangfire/Sentry never see host/username/banner text. `last_sync_error` stores only mapped messages, truncated ≤500.

**Tests (fake factories):** happy path counts+hash persisted; unchanged-hash short-circuit (no parse call); guard-reject before connect (factory never invoked); oversize stream aborts mid-copy at cap+1 (fake stream longer/lying — H2 regression); decrypt-null and deleted-supplier statuses; deadline cancellation on a stalling fake connect (H3 regression); error-mapping test proving a raw exception containing `user@host` never reaches the persisted message or the rethrown exception (M4); `BuildFtpConfig` asserts PASVEX + DataConnectionEncryption + ValidateAnyCertificate=false + all four timeouts (H1/M6 regression).

### BE-5 — Test-fetch endpoint (read-only honesty probe)
**Files:**
- EDIT `ProcuLink.Api\Controllers\SuppliersController.cs` (`POST /api/suppliers/{id}/catalog/source/test-fetch`, `[EnableRateLimiting("upload")]`)
- EDIT `ProcuLink.Infrastructure\Services\Catalog\CatalogPullService.cs` (`TestFetchAsync`)
- NEW tests in `ProcuLink.Api.Tests` + `ProcuLink.Infrastructure.Tests`

**Scope:** Uses the **saved** config and the **same** `CatalogPullService` fetch path (so guard/timeouts/bounded-read run identically), but read-only: no upsert, no `last_sync_*` mutation. Response per design §5: `ok, fileName, bytes, detectedFormat, headerColumns, mappedFields` (computed from `CatalogColumnAliases`), `unmappedColumns, parsedRows, rowsWithCode, sampleRows` (≤5). `ok:false` carries only the M4-mapped safe error.

**Tests:** mapping-report shape (mapped vs unmapped columns); sample rows ≤5; **no DB writes** (source row byte-identical after probe); guard-reject and oversize return `ok:false` with the safe message; 404 foreign supplier.

### BE-6 — Worker jobs + DI
**Files:**
- NEW `ProcuLink.Infrastructure\Jobs\CatalogSyncDispatcherJob.cs` + `CatalogSyncSourceJob.cs`
- EDIT `ProcuLink.Worker\Worker.cs` (recurring registration alongside existing pollers :21-34)
- EDIT `ProcuLink.Worker\Program.cs` (DI foot-gun, precedent `4607d6d`: register `ISupplierCatalogService` — currently Api-only at `Api\Program.cs:393` — plus `ICatalogPullService`, `IFtpFetchClientFactory` if not done in BE-4)
- NEW tests in `ProcuLink.Infrastructure.Tests\Jobs\CatalogSyncJobTests.cs`

**Scope:**
- `CatalogSyncDispatcherJob` — `[Queue("polling")]`, `[AutomaticRetry(Attempts = 0)]`, recurring `"catalog-sync"`, cron `0 * * * *` (hourly). Due query: `IsEnabled && (LastSyncAt == null || LastSyncAt <= now.AddHours(-SyncIntervalHours))`, select `{Id, OrgId}`, enqueue one child **per source** (per-supplier isolation, `SftpPollingJob` precedent).
- `CatalogSyncSourceJob(orgId, sourceId)` — `[Queue("polling")]`, `[AutomaticRetry(Attempts = 2)]`: (1) reload org-scoped, bail silently if deleted/disabled; (2) soft lock: set `LastSyncAt = utcnow`, `LastSyncStatus = "running"`, save (next dispatch sees not-due; crash self-heals at next window); (3) `PullAsync`; (4) success → `ok`/`unchanged` + counts + hash + `LastSyncError = null`; (5) failure → persist `failed` + safe message **before rethrowing** the sanitized `CatalogSyncException` (Hangfire retries ×2; final failure → dashboard + Sentry, leak-free per M4). Idempotent by upsert key; hash-skip makes retries of `unchanged` free.

**Tests:** due-query selection (due / not-due / disabled / null-LastSyncAt); soft lock prevents double-dispatch; failed-status-persisted-before-rethrow; deleted/disabled bail; rethrown exception message contains no host/username.

### BE-7 — Push endpoint + per-key rate-limit identity (M3)
**Files:**
- EDIT `ProcuLink.Api\Controllers\IngressController.cs`
- EDIT `ProcuLink.Api\Auth\ApiKeyAuthHandler.cs` (one line after :95: `new Claim("sub", $"apikey:{apiKey.Id}")`)
- EDIT `docs\integrations\ORDER_APIS.md` ("Supplier catalog push" section)
- NEW tests in `ProcuLink.Api.Tests\Controllers\IngressControllerCatalogTests.cs` (+ extend ApiKeyAuthHandler tests)

**Scope:** `POST /api/ingress/{slug}/catalog/{supplierId}` — `[Authorize(AuthenticationSchemes = "ApiKey")]`, existing `SlugMatchesCallerAsync` → 403. **GUID-only** supplier segment (org-scoped + `DeletedAt == null` → 404). Bodies: `multipart/form-data` + `file` → `ParseByFileName`; `text/csv` or `application/octet-stream` raw body → CSV parser. (JSON body cut — v2.) Caps: `[RequestSizeLimit(IngressLimits.MaxFileBytes)]` → 413; `MaxCatalogRows` → 400. `[EnableRateLimiting("upload")]` — now per-key thanks to the `sub` claim. Response byte-compatible with the upload import: `200 { created, updated, skipped, total }`. No `Idempotency-Key` (natural-key upsert; replay is a no-op — say so in docs). Docs: auth header `X-ProcuLink-Key`, two body forms, response shape, idempotency note.

**Tests:** multipart and raw-CSV both upsert; replay-is-noop (second identical push → 0 created); slug mismatch 403; unknown/foreign/soft-deleted supplier 404; non-GUID segment 404; oversize 413; >50k rows 400; ApiKey principal now carries `sub = "apikey:{id}"` (M3 regression).

### FE-1 — Single frontend task (`project-proculink`)
**Files:**
- NEW `src/lib/api/catalogSources.ts` (+ re-export from `api-client.ts` ~:1747-1750 region; `USE_MOCK` branch + mocks)
- NEW `src/components/bridge/CatalogSourceEditor.tsx`
- EDIT `src/components/bridge/SupplierDockProfile.tsx` (CatalogTab embed, ~:952)

**Scope (trimmed per scope review):** API module: types `CatalogSource`, `UpsertCatalogSourcePayload` (`password: string | null` = keep, `""` = clear), `CatalogSourceTestResult`; fns `getCatalogSource`, `upsertCatalogSource`, `deleteCatalogSource`, `testFetchCatalogSource` (60 s timeout); authHeader + fetchWithTimeout per `settings.ts`/`delivery.ts` conventions. Editor mirrors `DeliveryConfigEditor.tsx`: protocol rail **SFTP / FTPS / FTP** (3, not 4; FTPS before FTP to default-discourage plaintext); conditional fields Host/Port/Username/Password/RemotePath; masked-secret convention (`hasPassword`, blank-means-keep, clear-after-save); **no format selector, no interval select, no sync-now button** (PUT-on-enable triggers the first sync); enabled toggle; **Test fetch** button → result panel with `mappedFields` table + `unmappedColumns` + ≤5 sample rows + honesty note ("ProcuLink will read only the mapped columns"); **last-sync status line** (`lastSyncAt`/`lastSyncStatus`/`lastSyncError` + created/updated counts — `running` renders honestly as "running since …"); **FTP plaintext warning banner**; billing-gated enable → `UpgradeNotice` pattern (`PullIngressSettings.tsx:176-186`). Embed in CatalogTab under a collapsed "Automatic import" disclosure — manual upload stays primary. Below: static read-only **"Push from your system"** block: copy-able `POST {API_BASE_URL}/api/ingress/{orgSlug}/catalog/{supplierId}`, "authenticate with an API key (Settings → API Keys)", docs link — no claims beyond what BE-7 tests prove (multipart + raw CSV only). Invalidate `["supplier-catalog", ...]` + `["supplier-catalog-codes"]` after save/test (helper at :916-919).

**Tests/verification:** `bun run build` clean; mock-mode rendering of all states (unset / configured / running / ok-with-counts / failed / billing-gated); existing CatalogTab e2e/QA unaffected.

### SEC-1 — SEPARATE PR (parallel worktree, not part of the feature's review surface)
**Files:** `ProcuLink.Infrastructure\Services\Ingress\SftpIngressService.cs` (~:95, :154-163), `S3IngressService.cs` (~:94-101, :177-188), `ProcuLink.Infrastructure\Jobs\EmailPollOrgJob.cs` (~:116-119), `RenciSftpClientFactory.cs` (shared with BE-4 — coordinate; whoever lands second rebases), `PullIngressSettingsService.cs` / `SettingsController.cs` (save-time pre-checks), `OutboundRequestGuard.cs` / both `Program.cs` (M5 warning), NEW tests mirroring `ErpConnectorSsrfTests.cs`.

**Scope (security cross-cutting rec #2 — all three fixes, not just guard calls):** (a) `ValidateHostAsync` before `_sftpClientFactory.Connect` in `SftpIngressService`; `ValidateAsync(serviceUrl)` in `S3IngressService` when a custom `ServiceUrl` is set; `ValidateHostAsync` before MailKit `ConnectAsync` in `EmailPollOrgJob`; matching save-time pre-checks in the settings PUTs. (b) **H2 in the order pollers:** route their downloads through the same bounded-read helper (cap during copy, not after materialization). (c) **H3:** confirm/extend the `RenciSftpClientFactory` timeouts land for the order poller path. (d) **M5:** Error-level startup warning (+ Sentry message in Production) when `Delivery:AllowPrivateNetworkTargets` is true; hard fail-closed deliberately deferred. **Tests:** SSRF regression per path (private host rejected before connect); bounded-read abort; existing ingress tests green (regression gate for three live production paths).

---

## E. Explicitly OUT of scope (v1) — pure-code v2 additions against the same table

HTTP pull (with the H5 requirements attached: encrypted auth header + reject URL userinfo + Sentry breadcrumb scrubbing); JSON push body; supplier-code resolution on push (+ the M2 unique-code index decision); `sync-now` endpoint/button; per-org daily catalog-import budget (M3 remainder); replace/deactivate-missing mode; directory+glob/newest-file selection; SFTP key auth; PGP files; per-supplier custom column mapping; org-level catalog sources; multiple sources per supplier; failure notifications/webhooks; catalog diff/history; multi-sheet XLSX; delta files; org-level forbid-plain-FTP policy flag (L3); hard fail-closed `AllowPrivateNetworkTargets` in Production (M5 remainder); FE format/interval selectors (columns exist, default `auto`/24h); partial `WHERE is_enabled` index; new `BillingFeature` member (pull reuses `SftpIngestion`; push needs only an API key).

**Build order summary:** BE-1 → BE-2 → {BE-3 ∥ BE-4} → {BE-5 ∥ BE-6} → BE-7 (only needs BE-1; can run any time after it) → FE-1. SEC-1 fully parallel. Every H-severity finding is closed inside BE-1/BE-4; M3/M4/M6 inside BE-4/BE-7; M1/M2/H5 mooted by the accepted scope cuts; M5-lite + the sibling-poller hardening ship in SEC-1.