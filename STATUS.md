# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

---

## Where we are: **2026-06-02 — PO reliability Task 6 closed · live happy/error browser QA green**

Active focus is no longer "add more breadth"; it is **make upload -> parse -> review -> transform -> deliver boringly reliable** for the buyer/procurement PO workflow. Current plan:
`docs/superpowers/plans/2026-06-01-boringly-reliable-po-loop.md`.

Group J/live deployment hardening has started:
- **Live edge check:** `https://proculink.eu/` and `https://www.proculink.eu/` return 200 from Vercel; `https://api.proculink.eu/health` returns 200 from Railway.
- **Deployed defect found:** live `/upload` returned a signed-out protected-route 404 instead of a clean sign-in redirect; live `/sitemap.xml` returned 404.
- **Frontend fix pushed + verified live:** `src/middleware.ts` now explicitly redirects signed-out protected app routes to `/sign-in?redirect_url=...`; `src/app/sitemap.ts` and `public/robots.txt` expose public marketing/help pages and disallow protected workspace paths.
- **Verification:** `bun run build` passed; local production server returned `/upload` as `307 -> /sign-in?redirect_url=%2Fupload`, `/sitemap.xml` as 200 XML, and `robots.txt` with the sitemap URL. After push, live `https://proculink.eu/upload` returns 307 and live `https://proculink.eu/sitemap.xml` returns 200 XML.
- **Still not complete:** the deployed signed-in PO path still needs a real Clerk production session/test user and production-like Railway env verification before Group J can close.

Landed in this pass:
- **Async XML parser routing fixed:** `OrderService.CreateStubAsync` and `ParseStoredFileAsync` now use content-aware parser selection for ambiguous files like `.xml`, so UBL/Peppol XML is not accidentally sent through the cXML parser because of DI registration order.
- **Returned parse entity fixed:** `ParseStoredFileAsync` no longer duplicates newly parsed lines in the returned tracked entity after EF relationship fixup.
- **Regression test added:** `EndToEndPipelineTests.ParseStoredFileAsync_UblXml_RoutesToUblParserEvenWhenCxmlRegisteredFirst`.
- **Manual review E2E guardrail added:** `EndToEndPipelineTests.ReviewResolveTransformDeliver_UnmappedLine_BlocksThenSavesMappingAndDelivers` proves an unresolved line blocks transform, manual resolution saves the mapping, and the order then transforms/delivers.
- **Docs added:** `docs/integrations/ORDER_APIS.md` explains browser upload, IMAP, hosted inbound email webhook, inbound REST API, SFTP/S3 polling status, outbound webhook signing, and OCR setup.
- **Assisted SFTP/S3 pull ingress hardened:** `sftp_ingress_configs` and `s3_ingress_configs` now carry nullable `default_supplier_id`; pollers validate the supplier belongs to the same org and is active before connecting/listing. Unsafe configs return zero and never call `CreateStubAsync` with `Guid.Empty`.
- **Local live-QA backend auth added:** `PROCULINK_QA_BYPASS_AUTH=true` enables a development-only ASP.NET auth scheme for browser/API smoke tests. It is gated by `IHostEnvironment.IsDevelopment()` and must not be used in production.
- **CSV alias reliability fixed:** `CsvOrderParser` now normalizes header punctuation and supports common procurement aliases such as `po_number`, `PO Number`, `po`, `line_no`, `qty`, `unit_price`, `sku`, and `buyer_code`.
- **Live frontend/API upload smoke green:** with local API on `http://localhost:5223`, local frontend on `http://localhost:8082`, QA auth bypass, one seeded supplier, and a real CSV upload, Playwright reaches `/upload/preview/<orderId>` successfully.
- **Parsing race guard added:** `POST /api/orders/{id}/transform` now returns `409` while an order is still `parsing` or has zero parsed lines, so the UI cannot queue transforms before the Worker finishes.
- **Mapping preview state clarified:** `GET /api/orders/{id}/mapping-preview` now returns `orderStatus` and `resolvedSupplierCode`; the preview page polls while parsing and shows an explicit no-lines state instead of an empty mapping table.
- **Missing delivery config is now auditable:** delivery without supplier config records failed `delivery_attempts` with channel `missing_config`, marks the order `delivery_failed`, and `GET /api/orders/{id}` surfaces the latest attempt error as `errorMessage`.
- **Review send action is real:** the review page's primary send action now calls transform, waits for `ready_to_deliver`, triggers delivery, and surfaces delivered/failed/rejected states rather than only advancing local UI state.
- **Live browser PO loop guardrail added:** `tests/e2e/live-po-loop.spec.ts` runs only with `PLAYWRIGHT_LIVE=1` and drives the real UI through CSV upload, mapping preview, manual supplier-code entry, save/continue to review, send/transform/deliver, missing delivery-config failure panel, and retry feedback.
- **Live failure-state browser guardrail added:** `project-proculink/tests/e2e/live-po-failure-states.spec.ts` verifies no-supplier upload blocking, unsupported-format guidance, scanned/textless PDF parse-failure guidance when OCR is disabled, and supplier HTTP 4xx rejection visibility.
- **Supplier rejection detail surfaced:** `GET /api/orders/{id}` now includes the latest supplier rejection response as `errorMessage` for `rejected_by_supplier` orders, and the review UI shows a red rejected state plus the supplier response copy.

Verification:
- `dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ParseStoredFileAsync_UblXml"` ✅ 1 passed.
- `dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ReviewResolveTransformDeliver"` ✅ 1 passed.
- `dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~EndToEndPipelineTests"` ✅ 3 passed.
- `dotnet test ProcuLink.Infrastructure.Tests\ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~Ingress"` ✅ 12 passed.
- `dotnet test ProcuLink.Transform.Tests\ProcuLink.Transform.Tests.csproj --no-restore --filter "FullyQualifiedName~CsvOrderParserTests"` ✅ 2 passed.
- `PLAYWRIGHT_API_URL=http://localhost:5223 bun run test:e2e:live -- tests/e2e/magic-mapping-preview.spec.ts -g "upload a file and land"` ✅ 1 passed.
- `dotnet test ProcuLink.Infrastructure.Tests\ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~DeliveryServiceTests"` ✅ 5 passed.
- `dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~OrdersMappingPreviewTests"` ✅ 3 passed.
- `dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~OrdersControllerErrorMessageTests"` ✅ 3 passed.
- `bun run test:e2e -- tests/e2e/magic-mapping-preview.spec.ts` ✅ 7 passed.
- `PLAYWRIGHT_API_URL=http://localhost:5223 bun run test:e2e:live -- tests/e2e/live-po-loop.spec.ts` ✅ 1 passed.
- `PLAYWRIGHT_API_URL=http://localhost:5223 bun run test:e2e:live -- tests/e2e/live-po-failure-states.spec.ts` ✅ 4 passed.
- `dotnet build ProcuLink.slnx --no-restore` ✅ build passed (existing nullable warnings only in test projects).
- `bun run build` ✅ production build completed.
- Local API + Worker live smoke ✅ upload -> parse (`pending_review`) -> mapping preview (3 unresolved lines) -> resolve/save mappings (`ready`) -> transform artifact -> missing-config `delivery_failed` with auditable delivery attempts.
- Local API race smoke ✅ immediate transform on a still-parsing order returns `409` with "Order is still parsing. Wait until parsing finishes before transforming."
- Local browser live smoke ✅ upload page -> `/upload/preview/<orderId>` -> explicit unresolved-line review -> save mapping -> `/inbox/<orderId>` -> send to supplier -> missing-config failure panel -> retry button preserves clear failure guidance.

Important product/implementation guidance:
- Hosted inbound email webhook and inbound REST API have backend support, but should be offered as assisted setup until customer-facing setup docs/screens are complete.
- SFTP/S3 polling backend exists and now requires a valid configured default supplier before import. It remains assisted/internal until customer-facing setup/test-fire UX exists.
- Scanned PDF OCR is possible through `IDocumentOcrService` / Azure Document Intelligence. Use OCR provider extraction first; use AI for interpretation, mapping suggestions, confidence, and review. Do not treat the LLM itself as the OCR engine.
- Task 6 of `2026-06-01-boringly-reliable-po-loop.md` is closed locally. Next gate is Group J/live deployment hardening: repeat the same PO happy/error path against Railway/Vercel with production-like env vars, then only broaden standards/engines once the deployed bridge is equally boring.

---

## Where we are: **2026-05-29 (late) — chip-collision recovery complete · all build/cleanup chips merged + pushed · 328 backend tests green**

The 7 build/cleanup chips had run in the **shared** repo checkouts (not isolated worktrees) and collided — committing to `main` directly and leaving intermingled, partially-reverted, non-building working trees (repeated `git clean`/reset wipes). After the chips were stopped, this session recovered + completed + merged + **pushed** everything from ground truth (git + build + test), ignoring the contradictory chip narratives.

**Backend (`de6b774`, pushed) — 328 tests (123 Transform + 161 Infrastructure + 44 Api), 0 failures:**
- **#5 schema-fingerprint moat v1:** `SchemaFingerprint` entity + service + `SchemaFingerprintHasher` + `FingerprintBoost`; `ParseStoredFileAsync` returns `ParsedFileOutput` (entity + column headers + format); `ParseOrderJob` records org-scoped layout fingerprints; `POST /api/upload/detect-format` boosts confidence + returns `seenCount`. DI registered in **Api + Worker**.
- **#2 delivery reliability:** `IDeliveryService.RetryDeliveryAsync` (attempt-count + `delivery_dead_letter`), `DeliveryAttempt.AttemptNumber`, `RetryDeliveryJob` + `POST /api/orders/{id}/retry-delivery`, `StuckOrderDetectionService` + recurring `StuckOrderDetectionJob` (15 min).
- **#6 cleanup:** removed dead Phase-0 Canonical POCOs, never-injected EF repos, orphan `SupplierProfilesController`.
- EF migration `AddSchemaFingerprintsAndDeliveryRetry` — model/snapshot consistent (`has-pending-model-changes` → none). (#1 redirect + #3 backend parse-failure committed earlier this day.)

**Frontend (`51e4ea9`, pushed):** #3 `ParseFailedPanel`/`FailedPanel` in `SpineReview`; #1 `/orders→/inbox` e2e test; #4 J2 mock-purge (fabricated landing stat removed; CrossingsLog date from latest real event; drafts/MOCK_LOG/LaneDrawer/UploadWorkbench/SpineReview demo all gated behind `isApiMockMode` with honest live-user states); #7 legacy `src/views` removed; SupplierDockProfile live-wired. `bun run build` green.

**Process lesson:** chips MUST run in isolated git worktrees — shared-checkout collisions caused the wipes. Recovery used a dangling commit + an out-of-repo backup (both deleted now the work is on `main`). Only `project-proculink/.claude/launch.json` (local config) left uncommitted.

### Safe-parallel batch via isolated worktrees (2026-05-29, validated · `870fb0a`)

Proved the safe parallel workflow that prevents a repeat of the collision: **2 agents ran concurrently in isolated git worktrees** (zero file collision), each verified green alone, then merged **sequentially behind a build+test gate**. Shipped:
- **FTPS TLS cert hardening** — secure-by-default validation + opt-in `allowInvalidCertificate` per-supplier flag (replaces the `ValidateAnyCertificate=true` hole).
- **Bulk-accept high-confidence AI suggestions** — `POST /api/orders/{id}/accept-ai-suggestions?minConfidence=` + `OrderService.AcceptAiSuggestionsAsync`.

**339 backend tests** green (123 Transform + 168 Infrastructure + 48 Api). New EF migration verified applying cleanly to dev Postgres (`dotnet ef database update`).

### Safe-parallel batch #2 — 4 agents, 2 repos (2026-05-29 · backend `b131235` / frontend `e494260`)

Pushed parallelism further: **3 backend agents in isolated worktrees + 1 frontend writer (the main session)**, disjoint files, exactly one migration in flight, merged sequentially behind a build+test gate. **355 backend tests** green (123 Transform + 176 Infrastructure + 56 Api); frontend `bun run build` green.
- **Supplier-rejection / ACK** (`delivered ≠ accepted`): dispatcher 4xx → `rejected_by_supplier` + `RejectionReason` captured; 5xx/network → `delivery_failed`; manual `POST /api/orders/{id}/mark-rejected`. Migration `AddDeliveryRejectionReason`.
- **Delivery-attempts history**: `GET /api/orders/{id}/delivery-attempts` (`DeliveriesController`) — surfaces retry/dead-letter attempt history for ops.
- **`BuyerService.OrderCount`**: real per-buyer counts + `LastOrderAge` from canonical JSON, org-scoped (was hardcoded 0).
- **Frontend**: "Accept all high-confidence (N)" bulk-accept button (→ `accept-ai-suggestions`); delivery retry now uses the dead-letter-aware `/retry-delivery` endpoint.

**Process note:** subagents are sandboxed to the backend (session) repo — cross-repo frontend writes are blocked, so the **frontend writer must be the main session**, not a backend-scoped subagent. Backend agents → isolated worktrees; frontend → main session as sole writer.

---

## Where we are: **2026-05-29 P0 parse-failure UX complete · 295 backend tests green · frontend builds clean**

### P0 fix: parse-failure UX (2026-05-29)

Resolved the P0 gap logged in the audit (STATUS.md line: "parse-error UX"). An order in `status=failed` from a parse failure now shows an actionable `ParseFailedPanel` instead of the generic "Order Not Found / Failed to load" gate.

- **Backend:** `ParseFailureExplain` static helper (3 format-specific error generators). Closed two audit-event gaps in `OrderService.ParseStoredFileAsync` — unsupported-format and empty-lines paths now write a `ParseFailed` audit event with a human-readable message. Added `ErrorMessage?: string | null` to `OrderDto`; `GET /api/orders/{id}` queries the newest `*Failed` audit event and surfaces the message.
- **Frontend:** New `FailedPanels.tsx` — `ParseFailedPanel` (danger left-border panel, format chip, error message from DTO/audit fallback, "Re-upload — try a different format" CTA with supplier pre-selected, `sessionStorage` detect-format result caching) + `FailedPanel` (amber for `transform_failed`, red for `delivery_failed`, "Back to review" / "Retry delivery" CTAs wired to `POST /api/orders/{id}/redeliver`). Wired into `SpineReview` (the primary order detail view at `/inbox/[orderId]`). `UploadWorkbench` now caches detect-format results after upload and honours `?supplierId=` URL param for pre-selection.
- **Tests:** 295 backend tests (123 Transform + 44 Api.Tests + 128 Infrastructure), 0 failures. Frontend `bun run build` clean.

---

## Where we were: **2026-05-29 Phase 6 wave merged — Group M/N/L features all on `main` · 272 backend tests green · 43 frontend Playwright tests**

### Production fixes landed on `main` (2026-05-29)

Railway/Vercel were broken; all P0s now fixed and on `main`:

- **Worker DI gap** (`3028286`) — `IAnalyticsService` + Phase 6 services were unregistered in `ProcuLink.Worker/Program.cs`, so every Hangfire job crashed with "Unable to resolve service IAnalyticsService while activating StripeBillingService". Registered the full transitive graph in the Worker.
- **Migration idempotency** (`06a8963`) — startup migration runner now tolerates partially-applied state (was crashing on `42701: column "slug" already exists`).
- **DataProtection key ring** (`6b3f6a0`) — keys now persist to Postgres with optional AES-GCM at-rest encryption (was warning "key may be persisted unencrypted", losing keys on restart).
- **Vercel hang** (`18b9c55`, `3505c48`) — TanStack Query retries were defaulting to 3× exponential backoff, freezing the UI ~30s when the API was slow. Capped retries + added fetch timeouts. Added `no-hang.spec.ts` regression test + an `(app)` layout error boundary.

### New features shipped to `main` (2026-05-29)

- **UBL 2.1 Order outbound transformer** (Group M) — Peppol BIS 3.0-compatible, round-trips through `UblOrderParser`. `OutputFormat.Ubl`.
- **SFTP delivery dispatcher** (Group N) — SSH.NET, password + private-key auth, per-supplier AesGcm credentials.
- **HMAC-verified webhook receive ingress** (Group N) — `POST /api/webhook-ingress/{slug}/{ping,acknowledge,status}`, replay protection (5-min timestamp window + nonce cache), `Organisation.WebhookSecretEncrypted` (AES-GCM).
- **Smart file-format auto-detect** — `POST /api/upload/detect-format` (magic bytes + content peek + PO metadata extraction) wired into `/upload` as a confidence pill before submit.
- All Phase 6 services wired in API + Worker DI (`b29da2b`).

### Direction change (2026-05-29): dual-persona dropped → One Great UX

The "default vs expert mode" toggle was **removed before adoption** (`5d6f82e` frontend, `6034413` backend docs). Successful B2B SaaS (Linear, Stripe, Notion, Vercel, Railway) ship ONE great experience with smart defaults + progressive disclosure + a Command Palette for power features — not user-mode toggles. Deleted `useViewMode`/`ViewModeToggle`/`proculink_view_mode_v1`. Power-user affordances (standards mappings, raw-view, hotkeys, density) now surface via Command Palette (Cmd+K) + info popovers + per-table column selectors. Locked rule in `CLAUDE.md` "One great experience rule" and `docs/design-system/00-agent-quick-brief.md` "One Great UX (Phase 6+)".

### Build wave merged to `main` (2026-05-29) — all green

- **Group M: ANSI X12 850 parser + transformer** — opens the US market; hand-rolled, no commercial EDI library. Both directions, round-trip tested. `OutputFormat.X12`, registered in Api + Worker, detected by `OrderParserFactory`. `docs/standards-matrix.md` → X12 "supported".
- **Group N: SMTP send-out + FTPS delivery dispatchers** — covers the email-only + FTPS supplier tails. Registered in Api + Worker delivery DI. `DeliveryProtocolConstants` += smtp, ftps.
- **Group L: Magic mapping preview** — side-by-side source→canonical→supplier preview with AI suggestions (confidence + provenance + accept/edit/reject) before commit. `/upload/preview/[orderId]` route + `MagicMappingPreview` component, backed by `GET /api/orders/{id}/mapping-preview` (+ DTO + tests).
- **Group M: `/library/standards` comparison screen + field standards popovers** — typed standards catalog, sidebar + Command Palette entries, `StandardsFieldPopover` surfacing UBL/EDIFACT/X12/cXML refs on demand (no expert-mode gate). Honest status: X12 transform shown "supported", anything not shipped marked "planned".
- **CI Playwright fix** — mock-mode tests that used `page.route()` network interception are guarded behind `PLAYWRIGHT_LIVE=1` (mock api-client never issues real fetches, so interception was a no-op → false failures).

**Note on chip isolation:** these chips ran in the *shared* repo working directory, not isolated worktrees, so backend X12/SMTP/FTPS work landed intermingled and was salvaged → verified green → merged (commit `ec237f3`). Two compile errors fixed during integration (X12 `ReadOnlySpan` across await CS9202; SMTP nonexistent `SmtpErrorCode.MailboxUnavailable`). Future chips should use isolated worktrees.

### Backend test baseline: **272** (123 Transform + 21 Api.Tests + 128 Infrastructure), 0 failures. Frontend: **43 Playwright tests** across 8 spec files. Both repos build clean.

### Founder configuration still pending (unchanged)

PostHog keys, Clerk post-signup redirect, `Frontend:Url`, `NEXT_PUBLIC_STATUS_URL`, `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`, `NEXT_PUBLIC_BOOK_DEMO_URL`, optional supplier-delivery SMTP. See `docs/group-l-go-live-playbook.md`.

---

## Where we were: **2026-05-28 Group L FULLY SHIPPED — Waves 1+2+3 all merged to `main` both repos · 213 backend tests green · only founder configuration remaining**

### Group L Wave 3 — fully merged to `main` (2026-05-28)

**Backend — Phase 7.2 — Stripe Checkout `success_url` redirect:**
- Checkout `success_url` now routes to the primary `{Frontend:Url}/welcome?upgraded={plan}&interval={monthly|yearly}&session_id={CHECKOUT_SESSION_ID}`, `cancel_url` to `{Frontend:Url}/settings`. If `Frontend:Url` is comma-separated for CORS, billing uses the first origin as the absolute redirect origin.
- Checkout accepts `billingInterval=monthly|yearly`; Stripe webhook price-id mapping recognizes both monthly and yearly price IDs for Growth, Operations, Integration, and Distributor.
- Stripe webhook handlers (`checkout.session.completed`, `customer.subscription.updated`, `customer.subscription.deleted`) now invoke `StripeBillingService.EmitBillingUpgradedAsync` / `EmitBillingDowngradedAsync` / `EmitBillingCancelledAsync` via a cast `_billing as StripeBillingService` in `BillingController` webhook handlers.
- Plan downgrades detected via rank ordering: `pilot < growth < operations < integration < enterprise`.

**Backend — Phase 9.2 — Support contact form backend:**
- `POST /api/support/contact` (allow-anonymous) accepts `SupportContactRequest` (Category, Subject, Message, UserEmail, UserAgent, Route).
- New `IEmailSender` abstraction in `Core` with two implementations: `MailKitEmailSender` (SMTP, sends to `support@proculink.eu`) and `ConsoleEmailSender` (dev fallback, logs to console when `Smtp:Host` is empty).
- `ISupportContactService` (Core) + `SupportContactService` (Infrastructure) formats email as `[support][{category}] {subject}`, includes org/user/route/agent context headers.
- Org-scoped submissions emit `support_form_submitted` analytics event with `category` and `route` properties.
- MailKit 4.8.0 added to `ProcuLink.Infrastructure.csproj`; new `FakeEmailSender` test double in `ProcuLink.Infrastructure.Tests/TestDoubles/`.
- 2 new unit tests in `SupportContactServiceTests`: happy path + anonymous (no analytics).
- **Backend test count: 213** (102 Transform + 11 Api.Tests + 100 Infrastructure), 0 failures.

**Frontend — Phase 6.3 + 10.3 + 10.4 cleanup + 9.2 support form:**
- `apiClient.runSampleOrder()` + "Try with sample order" button on `/upload` + amber `?sample=1` banner on `SpineReview` when `order.isSample`.
- `/watch` page (Client Component) — Loom iframe when `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL` set, dashed-border placeholder card otherwise; captures `watch_demo_started` analytics.
- Pilot Book-a-demo CTA card on `/upload` and Billing settings, visible only when `billing.plan === "pilot"` AND `NEXT_PUBLIC_BOOK_DEMO_URL` is set; emits `book_demo_clicked` analytics on click.
- New env vars: `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`, `NEXT_PUBLIC_BOOK_DEMO_URL` added to `.env` + `.env.example` (empty defaults).
- Dead-code removal: `src/components/onboarding/OnboardingWizard.tsx`, `src/views/Dashboard.tsx`, orphaned `src/app/(app)/dashboard/page.tsx` + `loading.tsx`.
- `apiClient.submitSupportRequest()` + `ContactForm.tsx` Client Component mounted on `/support` below the FAQ list; existing mailto link kept visible.
- `Order.isSample?: boolean` added to TS contract for sample banner gate.

**Branches cleaned up after merge:** `feat/group-l-w3-backend-finalization`, `feat/group-l-wave-3-phase-6.3-10.3-10.4`, `feat/group-l-w3-support-form-cross-repo` — all deleted local + remote. Wave 2/3 chip stashes (5 backend + 3 frontend) all cleared. Extra frontend worktree (`project-proculink-supportform-wt`) removed from disk.

---

## Where we are: **2026-05-28 Group L Wave 2 fully merged to `main` — 211 tests green (102 Transform + 11 Api.Tests + 98 Infrastructure)**

### Dev stack smoke test (2026-05-28, this session)

- **Wave 3/4 EF migrations applied**: 4 duplicate migrations from the overnight agents were resolved — `AddInvoicesAndLines` ran clean; the 3 identical duplicates (`AddAdvanceShippingNotices`, `AddTenantApiKeysAndOrgSlug`, `AddIntegrationSubscriptions`) were fake-applied via `INSERT INTO "__EFMigrationsHistory"` since they contained no new SQL.
- **Worker DI fix**: Wave 4 added `IIntegrationTriggerService` as a constructor dependency to `OrderService` and `DeliveryService` but didn't register it in `ProcuLink.Worker/Program.cs`. Fixed and committed (`4607d6d fix(worker): register IIntegrationTriggerService`).
- **Org auto-seeded**: `TenantResolutionMiddleware` created org `370ca357-a72d-424a-b739-c90d4ec0ba4c` ("Personal workspace", pilot plan) on first authenticated API request.
- **PipelineStrip live-verified**: `SpineReview` at `/inbox/[orderId]` correctly fetches real order from `https://localhost:7230`, maps `pending_review` → Stage 3 of 5 (Validate), and renders all 5 stages (Parse → Normalize → Validate → Transform → Deliver). Screenshot captured via Playwright: `pipeline-strip-screenshot.png`.
- **Known issue — `/orders/[id]` shows "Order Not Found"**: `OrderDetailPage` at the `/orders/[id]` route makes no API calls and shows "Order Not Found". The same order loads correctly at `/inbox/[orderId]` via `SpineReview`. Root cause: likely stale TanStack Query error cache from a CORS failure on `http://localhost:5223` during first navigation (the HTTP port redirects to HTTPS, breaking CORS preflight). Logged as a separate fix task.
- **Wave 1 + Wave 2 code completeness verified** (2026-05-28):
  - Wave 1 (`EdifactOrderParser`, `UblOrderParser`): real parsing logic — no `NotImplementedException`. Committed `c395b6c`, `2bd4ecd`.
  - Wave 2 (SFTP/S3 ingress, `AzureDocumentIntelligenceOcrService`, `IEmailBodyOrderExtractor`): all committed and wired. OCR config-gated (`NoOpOcrService` fallback when `Ocr:Azure:Endpoint` absent — by design). `IEmailBodyOrderExtractor` intentionally API-only (HttpContext dependency; Worker comment documents this).
  - Stubs that exist (`EdifactInvoiceParser`, `EdifactDesadvParser` throwing `NotImplementedException`) are Wave 3/invoice domain — out of scope.
- **End-to-end smoke test confirmed**: background task logs show CSV upload → `ParseOrderJob` (Worker) → `status=pending_review` in one run. All three recurring jobs (email-polling, sftp-polling, s3-polling) fired without errors.
- **API running**: `https://localhost:7230` (HTTPS Kestrel), `http://localhost:5223` redirects to HTTPS. Worker running. Frontend at `http://localhost:8082`.

---

## Where we were: **2026-05-28 Wave 3 + Wave 4 shipped — Invoice/ASN canonical models + Zapier/Make.com integration layer**

### Wave 3 + Wave 4 (2026-05-28)

**Wave 3 — Invoice + ASN canonical models** (commit `3fbff22`):
- `ParsedInvoice` / `ParsedAsn` / `ParsedAsnPackage` / `ParsedAsnLine` records
- `IInvoiceParser` / `IDesadvParser` interfaces
- `UblInvoiceParser` — full UBL 2.1 Invoice XML parser with `IsUblInvoiceDocument` peek helper
- `EdifactInvoiceParser` / `EdifactDesadvParser` — stubs (EdiFabric licence required; drop-in ready)
- `InvoiceParserFactory` + `DesadvParserFactory`
- `InvoiceEntity` / `InvoiceLineEntity` / `AdvanceShippingNoticeEntity` / `AsnPackageEntity` / `AsnPackageLineEntity`
- `IInvoiceService` + `InvoiceService` (upload, parse-job, approve, forward CSV/XML/JSON)
- `IDesadvService` + `DesadvService` (file store stub; parsing deferred)
- `CsvInvoiceTransformService` / `XmlInvoiceTransformService` / `JsonInvoiceTransformService`
- `ParseInvoiceJob` — Hangfire idempotent job, 3 retries
- `InvoiceController` (upload/list/get/approve/download) + `DesadvController` (202 Accepted)
- 4 EF migrations: `AddInvoicesAndLines`, `AddAdvanceShippingNotices`, `AddTenantApiKeysAndOrgSlug`, `AddIntegrationSubscriptions`
- Tests: `UblInvoiceParserTests` (7), `EdifactStubTests` (2), `CsvInvoiceTransformServiceTests` (3) — 102/102 Transform.Tests pass; 93/93 Infrastructure.Tests pass (195 total, after JsonDocument fix)

**Wave 4 — Zapier/Make.com integration layer** (commit `3fbff22`):
- `ApiKeyHasher` utility in `Core.Security` (no circular project refs)
- `TenantApiKey` entity + `Organisation.Slug` (unique kebab-case, auto-generated)
- `IntegrationSubscription` entity (platform, eventType, targetUrl, AES-GCM encrypted HMAC secret)
- `ApiKeyAuthHandler` — second ASP.NET Core auth scheme alongside Clerk JWT Bearer
- `IApiKeyService` + `ApiKeyService` — `plk_` prefix, HMAC-SHA256 hash, plaintext never stored
- `ApiKeyController` — Clerk-auth CRUD for org members
- `IngressController` — machine-to-machine `POST /api/ingress/{slug}/orders` + `GET /api/ingress/{slug}/ping`
- `IntegrationController` — CRUD + toggle for subscriptions
- `IIntegrationTriggerService` + `IntegrationTriggerService` + `FireIntegrationTriggerJob`
  (HMAC-SHA256 `X-ProcuLink-Signature`, 3 retries, auto-deactivates subscription after 3 failures)
- Hooks: `OrderService.CreateStubAsync` → `order.created`; `DeliveryService` → `order.delivered` / `order.failed`
- `docs/integrations/SUBMISSION.md` — Zapier + Make.com submission checklist and webhook security docs
- Frontend: Settings → API Keys (create/list/revoke with one-time raw key display) + Settings → Connectors (Zapier/Make.com CTAs + custom webhook CRUD)
- Tests: `ApiKeyServiceTests` (3), `ApiKeyHasherTests` (3)
- **Post-wave fixes (commit `367c07f`, `19078e2`):**
  - `JsonDocument?` value converter added to `ProcuLinkDbContext` — resolved 48 pre-existing EF InMemory test failures.
  - `AddTenantApiKeysAndOrgSlug` migration now backfills `kebab(name)-{first4uuid}` slugs for existing orgs before unique index is added.
  - `IIntegrationTriggerService` registered in `ProcuLink.Worker/Program.cs` (commit `4607d6d` — fixes Worker startup crash).

---

## Where we were: **Phase 5 in progress — overnight 2026-05-28 closed Group J P0 backend gaps + dropped UI jargon + landed ROI calc + trust pack**

### Overnight 2026-05-28 (uncommitted; review `docs/agent-reports/2026-05-28-overnight-summary.md`)

- **Backend P0 gaps closed**: Idempotency-Key on `/upload`, per-org AI token cap (`Ai:OpenAI:MonthlyTokenLimitPerOrg`, default 100k), startup config validator. New tables `idempotency_keys` + `ai_usage_monthly` via migration `20260527230444_AddIdempotencyKeysAndAiUsageMonthly`. New endpoint `GET /api/billing/ai-usage`. **108 tests pass** (60 transform + 48 infrastructure).
- **Marketing landing page**: fabricated stats (84% / 1m 42s / €4.20 / 99.7%) removed. ROI calculator at `project-proculink/src/components/marketing/ROICalculator.tsx` mounted between value-prop and CTA. Feature descriptions rewritten to drop Wire/Spine/Crossing jargon.
- **Internal jargon swept from 17 user-facing files**: Bridge → Dashboard, Crossings → Orders/Deliveries, Cross the bridge → Send to supplier, Supplier docks → Suppliers, Buyer docks → Buyers, Crossings Log → Delivery Log, Spine Review → Order Review. Component / type / file / route names intentionally untouched.
- **Trust pack**: `docs/trust/security.md`, `gdpr.md`, `reliability.md` written. Honest, no marketing fluff.
- **Format/channel roadmap**: `docs/format-channel-roadmap.md` (3995 words) — 12-month plan for "any input → any output, any channel" vision with effort/priority/library specifics.
- **GTM enablement pack**: `docs/gtm/icp-target-list-template.md`, `outreach-scripts.md`, `demo-script.md`, `pilot-onboarding-checklist.md`, `first-100-users-strategy.md`.
- **Both repos build clean**. Nothing committed; founder reviews and commits in 4 logical groups per `docs/agent-reports/2026-05-28-overnight-summary.md`.

---



**Strategic correction (May 25 2026):** first paying ICP is the **buyer/procurement team sending orders out** to many suppliers, not the supplier/distributor receiving buyer orders. Keep the platform vision broad, but build the next 6 weeks around outbound PO reliability: buyer order source → canonical PO → supplier-specific validation/mapping → supplier-ready delivery.

**Production direction (May 26 2026):** ProcuLink is no longer being treated as a throwaway MVP. The next work should make the product feel trustworthy and usable end-to-end: UI/UX polish, mobile responsiveness, live QA of billing/delivery/email, and then engine hardening for broader input/output standards.

### Phase 6 — international standard + dual-persona UX (current)

Source of truth for the forward plan:
`docs/superpowers/plans/2026-05-28-phase-6-international-standard-roadmap.md`.
Positioning rationale: `docs/strategy/international-standard-thesis.md`.

ProcuLink's product thesis as of 2026-05-28: become the international
standard for outbound B2B purchase order routing — any input format /
channel → canonical PO → any output format / channel. Best-in-class for
30-year procurement veterans, effortless for first-time users,
standards-fit for every supplier shape, and cost-effective versus
SPS Commerce / TrueCommerce / Babelway / Pagero. The Learn loop
(`Parse → Normalize → Validate → Review → Transform → Deliver → Learn`)
remains the long-term moat; standards depth + channel breadth + dual-persona
UX are the next 6 months of execution.

| Horizon | Theme | Timeline | Status |
|---|---|---|---|
| **1** | Production Ready + Effortless | next 4–6 weeks | In progress |
| **2** | Standards Backbone + Channel Breadth | Q4 2026 | Planned |
| **3** | Network Effects | Q1 2027+ | Planned |

#### Horizon 1 — Production Ready + Effortless (next 4–6 weeks)

| Group | Workstream | Status |
|---|---|---|
| **J** | Live end-to-end QA + deployment hardening | In progress — code gaps fixed, live deployed QA remaining |
| **J2** | Purge mock / demo residue from frontend (sample PO `008412`, mock dashboard rows, hardcoded UUIDs) so prospects don't see staged data | Planned |
| **L (expanded)** | Trust + onboarding wizard + dual-persona UX (default novice + expert toggle) + magic mapping preview + in-app help + per-industry templates + analytics | In progress — Waves 1+2+3 shipped (cookie banner, PostHog SDK frontend+backend, event emitters, sample-order endpoint, 4-step wizard, `/welcome`, `/watch`, `/help`, `/support`, Pilot Book-a-demo CTAs); dual-persona / magic mapping preview / per-industry templates / standards-visibility chrome new for Phase 6 |

#### Horizon 2 — Standards Backbone + Channel Breadth (Q4 2026)

| Group | Workstream | Status |
|---|---|---|
| **M** | Standards depth: UBL 2.1 + Peppol BIS 3.0 Order (parse + transform), EDIFACT ORDERS d.96A real parser (evaluate EdiFabric vs open-source), ANSI X12 850, generic JSON/REST PO output transformer, ISO 20022 reference, in-app standards comparison screen | Planned |
| **N** | Channel expansion: SFTP out, FTPS out, SMTP send-out (PO as attachment + body), AS2/AS4 (partner-wrap first via mendelson / DragonAS2, in-house later), PEPPOL Access Point (partner-wrap first via Pagero / Tradeshift, in-house migration on roadmap), generic HMAC-verified webhook receive | Planned |
| **O** | Delivery feedback loop: retry/replay queue UI, supplier rejection capture (manual + email-in), ACK round-trip (APERAK for EDIFACT, MDN for AS2, DESADV correlation), per-supplier SLA timer | Planned |

#### Horizon 3 — Network Effects (Q1 2027+)

| Group | Workstream | Status |
|---|---|---|
| **P** | RBAC within org (Owner / Admin / Operator / Viewer, per-supplier delegation, audit log per user, SCIM 2.0 for Enterprise) | Planned |
| **Q** | Supplier mapping library: passive anonymised accumulation starts in Horizon 2 data model; public catalog ships in Horizon 3 | Planned |
| **R** | i18n (EN / DE / FR / ES / IT / PL UI + AI mapping in any language) | Planned |
| **S** | P2P loop closure (Invoice send via UBL Invoice + Peppol BIS Invoice 3.0; ASN / DESADV round-trip; 3-way match prep) | Planned |

### Phase 5 grouped roadmap (audit trail — superseded by Phase 6 above)

Phase 5 (production hardening) framing has been superseded by Phase 6.
Phase 5 history is preserved below as the audit trail. Groups I, K, and L
(Waves 1+2+3) shipped; Group J carries forward into Horizon 1; Groups M–S
are new in Phase 6.

Previous Phase 5 source of truth:
`docs/superpowers/plans/2026-05-26-production-hardening-roadmap.md`.

| Group | Workstream | Status (end of Phase 5) |
|---|---|---|
| **I** | UI/UX production polish + responsive QA | ✅ Effectively complete through pass 15. Passes 1-11 fixed topology/visibility defects, added Playwright QA, tightened mobile shell/upload/settings/inbox/dock/log/webhook/library/supplier-mapping/delivery/connector/webhook/billing flows, and wired live upload routing. Pass 12 (topology + bridge visual calibration): log-compressed `strokeFromWeight()`, staggered Bezier CPs, amber alert badges, r=2.2 pulse, mobile Lane List, responsive accordion for bridge detail, 28px StatusJourney nodes, `1fr/1.05fr/1.15fr` column grid, footer de-duplication, mobile sticky CTA, 2×2 KPI grid on mobile. Pass 13: BridgeTopbar auto-breadcrumb from pathname via `useAutoCrumb()`. Pass 14: BridgePageLoader loading.tsx for all 11 missing routes, InboxView mobile empty state, global `:focus-visible` ring + dark-chrome override, sidebar workspace-switcher accessible button, topbar aria-labels. Pass 15: SpineReview wired to live `GET /api/orders/{id}` via `useQuery`; `buildNodesFromOrder()` maps Order → SpineNodeData[]; `BuyerName` added to `OrderDto` (extracted from `CanonicalJson`); loading gate renders `SpineReviewSkeleton`; error/not-found gate renders centred panel with back-to-inbox button. |
| **J** | Live end-to-end QA + deployment hardening | In progress — carries forward into Horizon 1 (Phase 6). Code-level deployment gaps fixed (see Group J section). |
| **K** | Standards + engine hardening | ✅ Done — Standards matrix + canonical PO model written; cXML 1.2 input parser + output transformer landed with 18 new tests; merged to `main` via `2697115`. |
| **L** | Trust, onboarding + commercial readiness | ✅ Waves 1+2+3 shipped. Expanded scope (dual-persona UX, magic mapping preview, per-industry templates, standards-visibility chrome) continues in Horizon 1. |

### Completed phases
| Phase | What was built |
|---|---|
| Phase 0–3 | Auth, Postgres, Core loop, Sellable MVP |
| Next.js migration | App Router, Clerk, all routes, middleware |
| Group A | Tech debt (bun remove lovable-tagger, controller cleanup) |
| Group B | Marketing pages (landing, pricing, how-it-works) |
| **Group C** ✅ | Stripe billing — all 12 tasks done and pushed to both repos |
| **Group D** ✅ | PO Field Mapping Engine — all 12 tasks done and pushed to both repos |
| **Group E** ✅ | AI mapping suggestions — provider-neutral, OpenAI structured outputs first |
| **Group F** ✅ | PDF ingestion — text-based purchase-order PDFs via PdfPig |
| **Group G** ✅ | ERP connectors — Erply and Directo delivery adapters |
| **Group H** ✅ | Email polling — IMAP attachment ingestion via MailKit |
| **Group K** ✅ | Standards + engine hardening — standards matrix, canonical PO model, cXML 1.2 parser + transformer |
| **Phase 5 roadmap** | Groups I-L planned: UI polish, live QA, standards hardening, commercial trust |

---

## Group C — what was built (May 24 2026)

**Backend (`ProcuLink`):**
- `PlanConstants.cs` + `BillingFeature.cs`
- `IBillingService` interface + `StripeBillingService` implementation
- `Organisation` entity + EF migration (`stripe_customer_id`, `stripe_subscription_id`, `plan`, `orders_this_month`, `order_limit`, `pilot_expires_at`)
- `BillingController` — 5 endpoints + 3 Stripe webhook handlers
- Order + supplier limit enforcement in `OrdersController` and `SuppliersController`
- DI wired in `Program.cs`

**Frontend (`project-proculink`):**
- `BillingSection` component on settings page
- `UploadWorkbench` 429 banner with upgrade CTA

### Group C2 — Billing model reconciliation ✅ (May 25 2026)

**Status: Final model implemented in backend and frontend. Live Stripe webhook/Checkout QA is still required before billing is treated as production-ready.**

The final billing model is now locked:

| Plan | Price | Orders | Suppliers |
|---|---:|---:|---:|
| Pilot | €0 / 14 days | 20 total during trial | 1 |
| Growth | €149/mo | 150/month | 5 |
| Operations | €399/mo | 500/month | 10 |
| Integration | €999/mo | 1,000/month | 20 |
| Enterprise | Custom, from €2,500/mo | Custom | Custom |

Source of truth: `docs/superpowers/specs/2026-05-24-stripe-billing-design.md`.

Important corrections shipped:
- Pilot is internal/free, not Stripe Checkout and not free forever.
- Expired Pilot becomes read-only: users can view previous data and billing, but cannot upload, transform, deliver, or add suppliers.
- Add explicit account statuses: `trialing`, `active`, `trial_expired`, `past_due`, `read_only`, `cancelled`.
- Paid self-serve Checkout only supports Growth, Operations, Integration.
- Enterprise is contact-sales/manual.
- Pricing page, settings billing UI, upload 429 banners, supplier-limit banners, and backend limits must reflect the final model.

Backend (`ProcuLink`):
- Added account status constants and expanded plan constants.
- Extended `Organisation` billing fields + EF migration `AddBillingPlanFieldsToOrganisations`.
- Replaced `BillingStatus` with the final contract: plan/status, order and supplier usage, trial dates, limit flags, processing/add-supplier permissions, Stripe ids.
- Updated `StripeBillingService`, `BillingController`, Checkout, Portal, webhook price-id mapping, upload/transform enforcement, supplier-limit support, and delivery read-only guard.

Frontend (`project-proculink`):
- Updated billing TypeScript contract and mock billing data.
- Rebuilt settings billing UI around Pilot read-only freeze, paid-plan Checkout, and Stripe Portal.
- Updated upload 429 banners for Pilot expired, order limit, and supplier limit.
- Replaced old Starter/Growth/Enterprise pricing page with Pilot/Growth/Operations/Integration/Enterprise.

Verification:
- `dotnet build ProcuLink.slnx --no-restore` → passed.
- `dotnet test ProcuLink.Infrastructure.Tests\ProcuLink.Infrastructure.Tests.csproj --no-restore` → 25 passed.
- `dotnet test ProcuLink.Transform.Tests\ProcuLink.Transform.Tests.csproj --no-restore` → 22 passed.
- `bun run build` in `project-proculink` → passed; existing Sentry/Browserslist/ESLint warnings remain.

---

## Group D — PO Field Mapping Engine ✅ (May 25 2026)

**Backend (`ProcuLink`):**
- `PoMappingConfig` POCOs + `IPoMappingService` interface (`ProcuLink.Core`)
- `SupplierPoMapping` entity with JSONB `config_json` + EF migration (`supplier_po_mappings`)
- `IFieldManipulator` interface + `ManipulatorRegistry` factory
- 8 field manipulators: Replace, Trim, DateFormat, Concat, Fallback, Split, Multiply, Divide (`ProcuLink.Transform`)
- `PoMappingEngine.Apply()` static method
- `PoMappingService` CRUD with camelCase JSONB serialization (`ProcuLink.Infrastructure`)
- 4 API endpoints on `SuppliersController`: GET/PUT/DELETE `{id}/po-mapping`, POST `{id}/po-mapping/test`
- `OrderService` template-aware CSV parsing branch with culture-safe date parse
- 22 unit tests (all passing)

**Frontend (`project-proculink`):**
- `src/lib/api/types.ts` — TypeScript types mirroring backend contracts
- `src/lib/api/mapping.ts` — API client for all 4 mapping endpoints
- `src/components/bridge/PoMappingEditor.tsx` — visual CSV field mapping editor component
- `SupplierDockProfile` — "PO Mapping" tab wired to editor

## Group D2 — Buyer-Side Supplier Delivery Config ✅ HTTP-first path (May 25 2026)

**Status: HTTP/webhook delivery config path implemented and committed. SFTP/FTP intentionally deferred until HTTP workflow is production-proven.**

### What Group D2 builds
Per-supplier delivery configuration for a procurement team sending purchase orders out: HTTP/webhook first, then SFTP/FTP. Protocol selection, auth credentials, output file naming, safe test-fire, retry policy, audit trail, and non-developer friendly UI for configuring how mapped POs are delivered to each supplier.

### What shipped

**Backend (`ProcuLink`):**
- Replaced delivery credential encryption with authenticated `AesGcm`.
- Added `OrderStatusConstants`; transform now sets `ready_to_deliver`, not `delivered`.
- Added delivery config contracts and `IDeliveryConfigService`.
- Added `DeliveryConfigService` with org-scoped CRUD, protocol validation, encrypted credential storage, credential preservation, and redacted reads.
- Added supplier delivery config endpoints: GET/PUT/DELETE `/api/suppliers/{id}/delivery-config`.
- Added real test-fire endpoint: POST `/api/suppliers/{id}/delivery-config/test-fire`; writes `DeliveryAttempt` with `OrderId = null`.
- Added `IDeliveryService` + `DeliveryService` workflow: no-op when no config or `auto_deliver=false`, `delivering` during dispatch, `delivered` only on dispatcher success, `delivery_failed` on dispatch failure.
- Replaced old `DeliverOrderJob` supplier-profile webhook logic with delivery workflow delegation.
- `TransformOrderJob` now enqueues delivery after successful transform.
- Hardened `HttpDeliveryDispatcher` with timeout support and safer failure messages.

**Frontend (`project-proculink`):**
- Added delivery config TypeScript types and API client.
- Added `DeliveryConfigEditor` in the Bridge Layer style.
- Added `Delivery` tab to `SupplierDockProfile`.
- HTTP is enabled first; SFTP/FTP are visible as later protocols.

### Verification
- `dotnet test ProcuLink.Infrastructure.Tests\ProcuLink.Infrastructure.Tests.csproj --no-restore` → 25 passed.
- `dotnet test ProcuLink.Transform.Tests\ProcuLink.Transform.Tests.csproj --no-restore` → 22 passed.
- `dotnet build ProcuLink.slnx --no-restore` → passed.
- `bun run build` in `project-proculink` → passed; existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

### Deferred from D2
- SFTP dispatcher.
- FTP/FTPS dispatcher.
- PEPPOL, ERP connectors, invoices, and broad document types.
- Manual browser/Scalar test-fire against a live API session still recommended before pushing.

---

## Group E — AI mapping suggestions ✅ (May 25 2026)

**Status: Implemented in backend and frontend. Live OpenAI key/provider QA is still recommended before relying on suggestions in production.**

Backend (`ProcuLink`):
- Added `OpenAI` SDK package to `ProcuLink.Infrastructure`.
- Added provider-neutral `IAiMappingService` contract and `OpenAiMappingService`.
- Uses OpenAI structured outputs / JSON schema with `Ai:Provider = "openai"`, `Ai:OpenAI:ApiKey`, and `Ai:OpenAI:MappingModel` (`gpt-5-mini` default).
- No-ops when AI provider/key is absent.
- Runs only after deterministic item mapping lookup leaves a line unresolved.
- Stores suggestions on `purchase_order_lines` via EF migration `AddAiMappingSuggestionsToOrderLines`.
- API exposes suggestions as line metadata: supplier code, confidence, reason, provenance.
- Manual line resolution clears suggestion metadata and persists confirmed mappings when requested.

Frontend (`project-proculink`):
- Added AI suggestion types to the order line contract.
- Resolve UI pre-fills unresolved supplier-code fields when suggestions exist.
- Suggestions are visibly labelled `AI suggested` with confidence, reason, provenance, and controls to use or clear the suggestion.
- Mock orders include AI suggestions for local review/demo.

Verification:
- `dotnet build ProcuLink.slnx --no-restore` → passed.
- `dotnet test ProcuLink.slnx --no-restore` → 49 tests passed.
- `bun run build` in `project-proculink` → passed; existing Sentry/Browserslist/ESLint warnings remain.

---

## Group F — PDF ingestion ✅ (May 25 2026)

**Status: Implemented for text-based purchase-order PDFs. Scanned/image-only PDFs and OCR are intentionally deferred.**

Backend (`ProcuLink`):
- Added `PdfPig` package to `ProcuLink.Transform`.
- Added `PdfOrderParser : IPurchaseOrderParser` with text extraction, header detection, and conservative line parsing.
- Registered the PDF parser in API DI so `OrderParserFactory` can select it.
- Updated upload validation to accept `.pdf` alongside CSV/XLSX.
- Added focused transform tests covering PDF parser selection, parsed header/line data, and header-only PDFs.

Frontend (`project-proculink`):
- Updated `FileUploadZone` to accept `.pdf`/`application/pdf`.
- Updated upload copy and selected-file icon handling so PDFs are first-class upload inputs.

Verification:
- `dotnet build ProcuLink.slnx --no-restore` → passed.
- `dotnet test ProcuLink.slnx --no-restore` → 52 tests passed.
- `bun run build` in `project-proculink` → passed; existing Sentry/Browserslist/ESLint warnings remain.

---

## Group G — ERP connectors ✅ (May 25 2026)

**Status: Implemented as delivery adapters for already-generated artifacts. ERP-native order modeling and supplier-specific ERP payload transforms remain future hardening.**

Backend (`ProcuLink`):
- Added `DeliveryProtocolConstants` with `erp_erply` and `erp_directo`.
- Added provider-neutral `IErpConnector`, `ErpDeliveryRequest`, and `ErpDeliveryResult`.
- Added `ErplyConnector` for REST-style POST delivery with bearer/API-key auth support.
- Added `DirectoConnector` for XML/API form-post delivery.
- Added `ErplyDeliveryDispatcher` and `DirectoDeliveryDispatcher` so existing `DeliveryService` can dispatch ERP destinations through the same audit/status workflow.
- Registered ERP connectors and dispatchers in API DI.
- Expanded delivery config validation to accept `erp_erply` and `erp_directo`.

Frontend (`project-proculink`):
- Extended delivery protocol typing with `erp_erply` and `erp_directo`.
- Enabled Erply ERP and Directo ERP modes in `DeliveryConfigEditor`.
- Added ERP-specific config fields while preserving masked credential behavior.

Verification:
- `dotnet build ProcuLink.slnx --no-restore` → passed.
- `dotnet test ProcuLink.slnx --no-restore` → 56 tests passed.
- `bun run build` in `project-proculink` → passed; existing Sentry/Browserslist/ESLint warnings remain.

---

## Group H — Email polling ✅ (May 26 2026)

**Status: Implemented for Integration+ organisations ingesting CSV/XLSX/PDF order attachments from IMAP mailboxes. Body-only email parsing and richer message-id dedupe are deferred.**

Backend (`ProcuLink`):
- Added `email_config` JSONB on `organisations` via EF migration `AddEmailConfigToOrganisations`.
- Added email settings contracts, `IEmailSettingsService`, and `EmailSettingsService`.
- IMAP passwords are encrypted with the existing `DeliveryEncryptionService`; redacted reads preserve saved credentials.
- Added `GET/PUT /api/settings/email`; enabling requires `BillingFeature.EmailIngestion` and a valid org-scoped default supplier.
- Added `MailKit` to `ProcuLink.Worker`.
- Added `EmailPollingJob`, scheduled in Hangfire every 5 minutes.
- Email polling loads enabled org configs, skips plans without email ingestion, reads unseen messages, imports CSV/XLSX/PDF attachments through `IOrderService.CreateStubAsync`, and enqueues `ParseOrderJob`.

Frontend (`project-proculink`):
- Added email settings TypeScript contracts and API helpers.
- Replaced the settings placeholder with a Bridge Layer IMAP configuration panel: enable toggle, host/port/SSL/folder, username/password, default supplier, saved-password state, last-polled metadata, and Integration-plan gate.

Verification:
- `dotnet build ProcuLink.slnx --no-restore` → passed.
- `dotnet test ProcuLink.slnx --no-restore` → 60 tests passed.
- `bun run build` in `project-proculink` → passed; existing Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin warnings remain.

Deferred from H:
- Live IMAP mailbox QA with real app-password credentials.
- Body-only email parsing.
- Persistent message-id/import dedupe beyond marking processed messages as seen.

---

## Design workflow correction (May 25 2026)

- Lovable is no longer used for this project.
- All UI/UX decisions run through `docs/design-system`, `/frontend-design`, and Claude Design/reference images.
- Design system path: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\docs\design-system`
- First-read design file for agents: `docs/design-system/00-agent-quick-brief.md`
- Locked direction: Direction 4 — The Bridge Layer, supported by Direction 3 — System Identity.
- Keep design files on disk; they do not affect token usage unless Claude/Codex reads them. Agents should read the quick brief first, then only task-specific design files.

---

## Current queue

| Group | What | Status |
|---|---|---|
| **C2** | Final billing model reconciliation | **Implemented — live Stripe QA still recommended** |
| **D2** | Buyer-side supplier delivery config (HTTP-first path) | **Implemented — live manual QA still recommended** |
| **E** | AI mapping suggestions — provider-neutral, OpenAI structured outputs first | **Implemented — live OpenAI QA still recommended** |
| **F** | PDF ingestion (`PdfPig`) | **Implemented — text-based PDFs only; OCR deferred** |
| **G** | ERP connectors (Erply, Directo) | **Implemented — live ERP sandbox QA still recommended** |
| **H** | Email polling (IMAP/MailKit) | **Implemented — live IMAP mailbox QA still recommended** |
| **I** | UI/UX production polish + responsive QA | **In progress — pass 15 complete** |
| **J** | Live end-to-end QA + deployment hardening | In progress — code gaps fixed; live deployed QA remaining |
| **K** | Standards + engine hardening | **✅ Done — cXML 1.2 parser + transformer, standards matrix, canonical PO model (`2697115`)** |
| **L** | Trust, onboarding + commercial readiness | **✅ Done (code) — Waves 1+2+3 all merged to `main`. Only founder configuration remaining (PostHog keys, Clerk redirect, status URL, Loom URL, Cal.com URL, optional SMTP).** |
| **Wave 3** | Invoice + ASN canonical models | **✅ Done — UBL 2.1 invoice parser, invoice/ASN entities, CSV/XML/JSON transforms, Hangfire job, controllers (`3fbff22`)** |
| **Wave 4** | Zapier/Make.com integration layer | **✅ Done — API keys, org slug, integration subscriptions, ingress/trigger controllers, frontend tabs (`3fbff22`)** |

### Group I — UI/UX production polish + responsive QA (in progress)

Use `/frontend-design` and the local design system. Start with
`docs/design-system/00-agent-quick-brief.md`.

Pass 1 completed (May 26 2026):
- `WireTopology` travellers now animate on the same SVG `pathD` as the rendered wire and start hidden until the animation begins, so they cannot appear as standalone dots before page load.
- Topology travellers are hidden under `prefers-reduced-motion`.
- Bridge dashboard header controls wrap on small screens.
- KPI cards move from fixed 5-column layout to responsive 1/2/5-column layout.
- Lower dashboard panels stack below `xl`, and queue/supplier-health rows truncate/wrap safely.
- `bun run build` in `project-proculink` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 2 completed (May 26 2026):
- Installed Playwright for frontend QA and ignored `.qa-screenshots/`.
- Added a non-production local QA auth bypass (`PROCULINK_QA_BYPASS_AUTH=true`) so protected routes can be screenshot-tested locally without weakening production Clerk middleware.
- Fixed `WireTopology` horizontal-wire rendering by using SVG `linearGradient` with `gradientUnits="userSpaceOnUse"`; same-lane wires can be straight but now render with the same gradient/stroke logic as curved wires.
- Added shared-port fan-out so multiple wires from the same buyer/supplier dock do not hide one another.
- Tethered alert counters to their wire and moved the volume legend out of supplier-pill collision space.
- Improved mobile shell navigation, marketing nav compaction, and `SpineReview` mobile behavior with a stable horizontally-scrollable canonical workbench.
- Verified with Playwright screenshots: `/bridge` desktop/mobile and `/inbox/008412` mobile. `bun run build` passed after the topology changes.

Pass 3 completed (May 26 2026):
- Route QA captured upload, settings, and order review screenshots across desktop/mobile.
- `UploadWorkbench` no longer forces a desktop two-column grid on mobile; it stacks route configuration below upload/recent activity.
- Recent uploads now render as readable buyer-to-supplier route cards on mobile while keeping the dense table on tablet/desktop.
- Settings now uses horizontal tab chips on mobile instead of a narrow sidebar; email polling form grids collapse safely.
- Verified with Playwright screenshots: `/upload` desktop/mobile and `/settings` desktop/mobile. `bun run build` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 4 completed (May 26 2026):
- Route QA captured inbox queue, supplier/buyer docks, mappings, rules/templates, operations log, connectors, webhooks, and tablet order-review screenshots.
- Fixed the inbox queue blank-body regression by replacing the broken virtualized table render with visible responsive rows: mobile route cards plus a dense desktop table.
- Removed the now-unused `@tanstack/react-virtual` frontend dependency.
- Fixed buyer and supplier dock mobile cards so names, volume, health, totals, and last-crossing metadata no longer overlap.
- Fixed crossings log and webhook mobile rows so event data stacks instead of clipping horizontally.
- Verified with Playwright screenshots: `/inbox`, `/library/suppliers`, `/library/buyers`, `/operations/log`, and `/operations/webhooks` mobile plus `/inbox` and `/operations/log` desktop. `bun run build` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 5 completed (May 26 2026):
- Route QA captured supplier detail, mapping editor, rules/templates, connectors, settings, and supplier tab interactions.
- Fixed supplier detail mobile header so title, code badge, and KPI metrics no longer overlap.
- Fixed supplier detail overview cards and tab strip responsiveness.
- Fixed Mapping Editor mobile layout by adding buyer-to-supplier mapping cards while preserving the dense desktop table.
- Fixed PO mapping and delivery config tab surfaces so toolbars, field rows, protocol selector, auth fields, and footer actions stack safely on mobile.
- Verified with Playwright screenshots: `/library/suppliers/s1`, `/library/mappings`, supplier `PO Mapping`, and supplier `Delivery` on mobile plus supplier detail/mapping desktop. `bun run build` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 6 completed (May 26 2026):
- Route QA captured settings billing/email tabs, connectors, webhooks, connector/webhook panels, and order review across desktop/mobile.
- Billing and email settings no longer sit in a low-information loading state when the API is unreachable; both now use no-retry queries, bounded API fetch timeouts, skeleton cards, explicit error copy, and retry actions.
- `/operations/connectors` now has responsive mobile connector cards instead of forcing a dense desktop table onto small screens.
- Connector rows/cards and webhook edit/add buttons now open lightweight configuration panels so the UI path is visible while live save/test-fire remains for Group J.
- Verified with Playwright screenshots: settings billing/email API-unavailable states, connector mobile cards, connector panel, webhook panel, connectors desktop, and webhooks desktop. `bun run build` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 7 completed (May 26 2026):
- Route QA captured mappings, rules, templates, mapping import/edit panels, rules list/edit panels, template edit panel, order-review confirm dialog, and order-review inline edit state on mobile/desktop.
- `/library/mappings` import/export/add/edit actions now open lightweight panels instead of being inert buttons. Mobile add button no longer wraps awkwardly.
- `/library/rules` header/filter controls now wrap safely; list view uses mobile cards instead of a clipped desktop table; new/edit rule panels are available.
- `/library/templates` new/edit actions now open a template panel with metadata and template-body editing.
- Dense order-review inline edit and confirmation modal were rechecked on mobile and remain usable inside the horizontal canonical workbench.
- Verified with Playwright screenshots and `bun run build`. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 8 completed (May 26 2026):
- `/upload` now has a real selected-file state for browse/drop, shows plan usage/read-only context in the pipeline panel, and blocks processing when billing says `canProcessOrders=false`.
- Upload errors now flow through shared `ApiHttpError` handling, preserving HTTP status/body so 429 Pilot/order/supplier-limit responses display the correct upgrade copy instead of collapsing into a generic failure.
- `/library/suppliers` now distinguishes actual supplier-limit state from billing-service-unavailable state; the add action no longer presents a misleading "limit reached" label when the API is merely unavailable.
- Supplier dock creation now opens a lightweight inline setup panel when the plan allows adding a supplier, keeping the action visible instead of inert while live persistence remains for later QA/hardening.
- Verified with Playwright screenshots: `/upload` desktop/mobile and `/library/suppliers` desktop/mobile, including the screenshot-driven billing-unavailable label correction. `bun run build` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 9 completed (May 27 2026):
- Connector and webhook panels now show visible draft test results and save notices instead of closing silently. Copy is explicit that browser-side drafts are local QA states and live credential/test-fire verification belongs to Group J.
- Mapping import/export/add/edit, validation-rule toggle/edit, and output-template validate/save flows now surface wrapped local feedback notices instead of inert or silent actions.
- Mapping and rules notices were moved into their own wrapped rows after screenshot review so they do not squeeze filter chips or clip on mobile.
- Verified with Playwright screenshots: connector test/save, webhook test, mapping save, rules save on mobile, and template validation. `bun run build` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 10 completed (May 27 2026):
- `/upload` now routes to the actual uploaded order id returned by the upload API/mock instead of always navigating to `/inbox/008412`.
- `/inbox/[orderId]` review now shows visible local feedback for Save draft, output Copy/Download, and confirmed delivery states so the first-upload-to-delivery path no longer has silent actions.
- The review sticky action bar now has a mobile-specific summary/action layout; grand total, output template, exception state, and buttons no longer squeeze or overlap on small screens.
- Verified with mock-mode Playwright screenshots: file upload → new `/inbox/ord-*` review route, review draft notice, output copy notice, delivered state, and mobile review footer. `bun run build` passed. Existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Pass 11 completed (May 27 2026):
- `UploadWorkbench` now loads supplier docks from `GET /api/suppliers` instead of hardcoded mock UUIDs, with loading/error/empty states and a link to `/library/suppliers` when none exist.
- When `NEXT_PUBLIC_USE_MOCK=false`, successful uploads route to `/orders/{id}` (`OrderDetailPage` — real API, polling while `parsing`/`transforming`) instead of `/inbox/{id}` (`SpineReview` is still static demo data).
- Exported `isApiMockMode` from `api-client.ts` for consistent mock vs live routing.
- Backend `dotnet test ProcuLink.slnx` → 60 passed. Frontend `bun run build` passed. Existing Sentry/Browserslist/ESLint warnings remain.

Pass 12 completed (May 27 2026) — Topology + Bridge visual calibration per design brief:
- `WireTopology`: exported `strokeFromWeight()` (log-compressed, weight 1–6 → ~1.2–5.2px); replaced linear STROKE_W lookup; added staggered Bezier control points (cx1 370±20, cx2 530±18) to prevent co-landing wire bunching; alert badges changed to white fill + amber stroke + amber numeral (no stem line); pulse radius 2.2, strokeWidth 1.2; buyer port dot = blue filled + white core; supplier port dot = hollow + colored stroke; legend rebuilt from weights [1,2,4,6] via `strokeFromWeight()`; responsive wrapper — `WireTopologyLaneList` renders below `md:` (lane rows with 76×38 mini-arc, buyer chip → supplier chip).
- `StatusJourney`: full variant upgraded to 28px nodes, 3px gradient connector, max-w-[720px] centered; optional `crossingRef` prop shows "Stage N of 5 · {ref}" sub-label above the stepper.
- `SpineReview`: 3-column body grid changed to `1fr/1.05fr/1.15fr` with 22px gap; connector SVG stubs deleted from `SpineNodeCard`; footer stripped to grand total + template + exceptions only (header retains Save/Cross); mobile accordion (`AccordionPanel` ×3: Source/Canonical/Output) + `md:hidden` sticky CTA bar with Save + Cross.
- `BridgeDashboard`: KPI grid changed to `grid-cols-2` on mobile (2×2 layout).
- Design-system docs: `tokens.css` wire stroke scale comment added; `05-components.md` §A.2 and §A.6 updated to reflect `strokeFromWeight()` and 28px full variant.
- Frontend commit `35ff057`, pushed to `main`.
- TypeScript check: `tsc --noEmit` → no errors.

Must address:
- Continue live API/deployment QA for the full first-upload-to-delivery happy/error paths against a running backend, including real save/test-fire persistence for connector/webhook/mapping/rule/template forms in Group J.
- App shell, sidebar, topbar, route labels, active states, and mobile navigation.
- Core flow polish: sign-in, first upload, inbox/review, mapping, transform, delivery, settings/billing/email.
- Empty, loading, error, disabled/read-only, and plan-gated states.

Do **not** introduce a new visual direction. Keep Direction 4 — The Bridge Layer,
supported by Direction 3 — System Identity.

### Group J — live end-to-end QA + deployment hardening (in progress)

#### Code-level gaps fixed (May 27 2026)

| Item | Fix | Commit |
|---|---|---|
| **EF migrations never applied in prod** | Added `db.Database.MigrateAsync()` in `Program.cs` before `app.Run()` | `2f725cb` |
| **Worker never deployed** | Added `Dockerfile.worker` for `ProcuLink.Worker` as a separate Railway service | `2f725cb` |
| **No prod appsettings template** | Added `appsettings.Production.json` (all-blank, no secrets) | `2f725cb` |
| **Frontend env vars incomplete** | Expanded `.env.example` with `CLERK_SECRET_KEY`, `SENTRY_*` | `c9ac1bb` |
| **SpineReview header hardcoded** | FROM/TO, file chips, StatusJourney stage, ConfirmDialog, CrossedToast now use live order data | `9240abd` |

#### Railway environment variables required

Set these in **Railway API service** environment:

| Variable | Source |
|---|---|
| `ConnectionStrings__DefaultConnection` | Railway Postgres plugin → `DATABASE_URL` (convert to Npgsql format) |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Clerk__Authority` | Clerk dashboard → API Keys → JWT public key domain |
| `Storage__R2AccountId` | Cloudflare R2 → Account ID |
| `Storage__R2AccessKeyId` | Cloudflare R2 → API token → Access Key ID |
| `Storage__R2SecretAccessKey` | Cloudflare R2 → API token → Secret Access Key |
| `Storage__R2Endpoint` | `https://<accountid>.r2.cloudflarestorage.com` |
| `Storage__R2BucketName` | `proculink` (or prod bucket name) |
| `Stripe__SecretKey` | Stripe dashboard → API Keys → Secret key (live) |
| `Stripe__WebhookSecret` | Stripe dashboard → Webhooks → signing secret for Railway URL |
| `Stripe__GrowthPriceId` | Stripe dashboard → Products → Growth monthly price ID |
| `Stripe__GrowthYearlyPriceId` | Stripe dashboard → Products → Growth yearly price ID |
| `Stripe__OperationsPriceId` | Stripe dashboard → Products → Operations monthly price ID |
| `Stripe__OperationsYearlyPriceId` | Stripe dashboard → Products → Operations yearly price ID |
| `Stripe__IntegrationPriceId` | Stripe dashboard → Products → Integration monthly price ID |
| `Stripe__IntegrationYearlyPriceId` | Stripe dashboard → Products → Integration yearly price ID |
| `Stripe__DistributorPriceId` | Stripe dashboard → Products → Distributor monthly price ID |
| `Stripe__DistributorYearlyPriceId` | Stripe dashboard → Products → Distributor yearly price ID |
| `Ai__OpenAI__ApiKey` | OpenAI platform → API keys |
| `Delivery__EncryptionKey` | Generate: `openssl rand -base64 32` → 32-byte AES-GCM key as base64 |
| `Frontend__Url` | Vercel deployment URL e.g. `https://proculink.vercel.app` |
| `Sentry__Dsn` | Sentry project → Settings → DSN |

Set these in **Railway Worker service** environment (same values):
`ConnectionStrings__DefaultConnection`, `ASPNETCORE_ENVIRONMENT`, `Clerk__Authority`,
`Storage__*`, `Ai__OpenAI__ApiKey`, `Delivery__EncryptionKey`.

Set these in **Vercel** environment (Production + Preview):

| Variable | Value |
|---|---|
| `NEXT_PUBLIC_API_BASE_URL` | Railway API service public URL |
| `NEXT_PUBLIC_USE_MOCK` | `false` |
| `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` | Clerk dashboard → API Keys → Publishable key (live) |
| `CLERK_SECRET_KEY` | Clerk dashboard → API Keys → Secret key (live) |
| `NEXT_PUBLIC_SENTRY_DSN` | Sentry project → Settings → DSN |
| `SENTRY_AUTH_TOKEN` | Sentry → Settings → Auth Tokens |
| `SENTRY_ORG` | Sentry organisation slug |
| `SENTRY_PROJECT` | Sentry project slug |

#### Remaining live QA items (require deployed services)

- [ ] Verify Railway API service starts and `/health` returns 200
- [ ] Verify Railway Worker service starts and Hangfire dashboard accessible
- [ ] Verify Clerk login, protected routes, org resolution, sign-out on Vercel URL
- [ ] Verify Stripe Checkout flow (Growth plan) with test card; verify webhook lands
- [ ] Verify Stripe Portal works for an active subscription
- [ ] Verify upload → parse → `pending_review` status update in live DB
- [ ] Verify resolve → transform → artifact download end-to-end
- [ ] Verify HTTP delivery test-fire against a controlled endpoint (e.g. webhook.site)
- [ ] Verify IMAP polling against a test mailbox/app password (Integration plan)
- [ ] Verify Sentry captures a frontend error and a backend 500 without leaking secrets
- [ ] Verify CORS does not block Vercel origin (check browser console on first load)

Verify deployed Vercel/Railway behavior with real test service configuration:
Clerk, Stripe Checkout/Portal/webhooks, upload/parse/transform/download,
HTTP delivery test-fire, ERP sandbox/stub test-fire, IMAP polling, Sentry/logging,
CORS, database migrations, and production env vars.

### Group K — Standards + engine hardening ✅ (May 28 2026)

**Status: Implemented and merged to `main` via `2697115`. Standards matrix, canonical PO model, and cXML 1.2 input parser + output transformer landed with 18 new tests.**

Backend (`ProcuLink`):
- Added `docs/standards-matrix.md` mapping supported procurement standards (cXML 1.2, UBL 2.1 Order, EDIFACT ORDERS, OpenPEPPOL BIS, ANSI X12 850, internal canonical) to parser/transformer coverage and gaps.
- Added `docs/canonical-po-model.md` documenting the in-memory `ParsedOrder` canonical PO shape (header, parties, lines, totals, references, custom fields) so future format work shares one target.
- Added `CxmlOrderParser : IPurchaseOrderParser` in `ProcuLink.Transform/Parsing/` for cXML 1.2 `OrderRequest` documents with header detection, party/address/line extraction, and a dedicated `CxmlParseException` for malformed envelopes.
- Added `CxmlTransformService : ITransformService` in `ProcuLink.Transform/Output/` producing standards-compliant cXML 1.2 `OrderRequest` output from the canonical PO model.
- Added `OutputFormat.CXml` to the core output-format enum so deliveries can target cXML.
- Registered `CxmlOrderParser` and `CxmlTransformService` in API DI (`ProcuLink.Api/Program.cs:216` and `Program.cs:226`).
- Added 18 new unit tests across the parser and transformer (round-trip, malformed envelope, header/line fidelity, party/address normalization).

Verification:
- `dotnet build ProcuLink.slnx --no-restore` → passed.
- `dotnet test ProcuLink.slnx --no-restore` → 193 tests passed (102 Transform + 91 Infrastructure), 0 failures.
- Remote `origin` no longer carries `feat/group-k-standards`; only `refs/heads/main` exists.

Next (deferred to future hardening passes):
- JSON/API payload output transformer.
- UBL 2.1 / Peppol BIS Order input parser.

### Group L — trust, onboarding + commercial readiness

Add onboarding, demo data, concrete ROI copy, trust/security/legal/support pages,
analytics event plan, and sales/demo assets after UI polish begins.

#### Group L Wave 2 — sample-order backend chip ✅ (2026-05-28)

**Phase 6.1 — fixture + entities + migration + quota guard** (commit `ffe7418`):
- `ProcuLink.Api/Fixtures/sample-order.csv` — 3-line EUR fixture (DEMO-2026-001, Northwind Trading OÜ).
- `PurchaseOrderEntity.IsSample: bool` + `Supplier.IsSample: bool` + `Supplier.Code: string?`.
- EF migration `20260528150709_AddIsSampleFlags` — `is_sample` + `code` on both tables.
- `StripeBillingService.CountOrdersAsync` — `&& !o.IsSample` guard on both Pilot cumulative and paid monthly count branches.

**Phase 6.2 — service + controller + tests** (commit `524b080`):
- `ISampleOrderService` (Core) — `Task<Guid> CreateAndEnqueueAsync(Guid orgId, string? userId, CancellationToken)`.
- `SampleOrderService` (Infrastructure) — idempotent `__sample__` supplier, fixture upload via `IFileStorageService`, `IsSample = true` order stub, `IParseJobEnqueuer.EnqueueAsync`, `sample_order_started` PostHog event.
- `POST /api/onboarding/sample-order` — returns `{ orderId, isSample: true }`.
- `ISampleOrderService` DI-wired in `Program.cs` (`AddScoped`).
- 3 xUnit tests: `CreatesSampleSupplier_IfMissing`, `ReusesExistingSampleSupplier`, `DoesNotIncrementOrdersThisMonth`.

**Key implementation decisions:**
- Fixture linked into `ProcuLink.Infrastructure.csproj` via `LogicalName` so `typeof(SampleOrderService).Assembly.GetManifestResourceStream(...)` resolves in unit tests without an Api project reference.
- Uses `IParseJobEnqueuer` (already in Core) rather than `IBackgroundJobClient` directly — avoids Infrastructure→Api dependency cycle.

#### Phase 4.3 — backend analytics event emitters ✅ (2026-05-28)

`IAnalyticsService` injected into 6 callsites, all emitting idempotent PostHog funnel events (commits `b7fa374`, `0220fd8`):

| Callsite | Event | Guard |
|---|---|---|
| `TenantResolutionMiddleware` | `org_created` (plan, created_via) | Auto-provision path only |
| `SuppliersController.CreateSupplier` | `first_supplier_added` (supplier_id) | `AnyAsync` — no prior org suppliers |
| `ParseOrderJob` | `first_upload_parsed` (order_id, parser) | `AnyAsync` — no prior parsed orders for org |
| `TransformOrderJob` | `first_transform_succeeded` (order_id, output_format) | `AnyAsync` — no prior delivered/ready orders |
| `DeliveryService.PersistAttemptAsync` | `first_delivery_succeeded` (order_id, protocol) | `AnyAsync` — no prior `Delivered` order for org |
| `StripeBillingService` | `billing_upgraded` / `billing_downgraded` / `billing_cancelled` | Called explicitly; no guard needed |

**Note:** `StripeBillingService.EmitBillingUpgradedAsync` / `EmitBillingDowngradedAsync` / `EmitBillingCancelledAsync` are concrete public methods (not on `IBillingService`). Wiring from `BillingController` Stripe webhook handlers is a separate later chip (Wave 3 Phase 7.2).

**New project:** `ProcuLink.Api.Tests` added to `ProcuLink.slnx` — 11 tests across middleware, controller, jobs, and billing service.

**Combined Wave 2 test count after both chips merged:** **211 total** (102 Transform + 11 Api.Tests + 98 Infrastructure), 0 failures.

#### Group L Wave 2 — frontend chips ✅ (2026-05-28)

- **Phase 3 cookie consent banner** — `src/lib/cookie-consent.ts` hook + `CookieConsentBanner.tsx` mounted in root layout. Three states (`unknown` / `functional-only` / `analytics-allowed`) persisted in `localStorage`, synced across tabs via `proculink:cookie-consent` event.
- **Phase 4.4 frontend PostHog SDK** — `src/lib/analytics.ts` + `AnalyticsBoot.tsx` mounted in root layout. `posthog-js` SDK no-ops without `NEXT_PUBLIC_POSTHOG_KEY`; opts out of capturing until consent is `analytics-allowed`. Identifies via Clerk user on sign-in, sets `organisation` group.
- **Phase 4.5 frontend analytics events** — `OnboardingWizard` emits `wizard_opened` / `wizard_step_completed` / `wizard_dismissed`; `UploadWorkbench` emits `first_upload_started` with `file_kind`.
- **Phase 5.1 + 5.2 4-step wizard** — `hasResolvedMapping` flag added to `/api/onboarding/status` (backend) and mirrored in `OnboardingStatus` TS type. `OnboardingWizard.tsx` rewritten with 4 real steps (supplier → upload → resolve mapping → configure delivery) driven by `useQuery` against the onboarding status endpoint.
- **Phase 7.1 /welcome page** — `(marketing)/welcome/page.tsx` Client Component reads `?upgraded={plan}` for post-Checkout state, renders 4-step preview, captures `welcome_viewed` analytics.
- **Phase 9.1 in-app HelpSlideover** — `BridgeTopbar.tsx` gets a Help button, opens `HelpSlideover.tsx` with route-aware contextual link (e.g. `/upload` → `/help/first-upload`) plus "Open help docs" / "Contact support" / "Report a bug" nav.

#### Group L — Wave 3 shipped ✅ (was previously deferred)

All five phases below merged to `main` and tests green. Recap of what each delivered:

| Phase | Delivered |
|---|---|
| **6.3** | Frontend "Try with sample order" button on `/upload` + amber non-quota banner on `SpineReview` |
| **7.2** | Stripe Checkout `success_url` → `/welcome?upgraded={plan}` + Stripe webhook handlers wired to `EmitBilling*` analytics emitters |
| **9.2** | `POST /api/support/contact` backend (`ISupportContactService` + `IEmailSender`) + `ContactForm.tsx` on `/support` |
| **10.3** | `/watch` Loom-slot page (env-gated) + Pilot "Book a 15-min demo" CTAs on `/upload` and Billing settings |
| **10.4** | Dead-code cleanup of unused `OnboardingWizard` + `Dashboard` + orphaned dashboard route |

#### Group L — waiting on founder configuration / external setup

Implementation is complete for each of these but they will not function in production until external services are wired up.

| Area | Action required | Where to set | Effect when missing |
|---|---|---|---|
| **PostHog analytics (frontend + backend)** | Create PostHog Cloud EU project; capture project API key | Vercel env: `NEXT_PUBLIC_POSTHOG_KEY` + `NEXT_PUBLIC_POSTHOG_HOST=https://eu.posthog.com`. Railway API + Worker: `Analytics:PostHog:ApiKey` | Frontend SDK and backend wrapper both no-op silently. Funnel data is not collected. |
| **Stripe `success_url` `Frontend:Url`** | Code already routes Checkout to `{Frontend:Url}/welcome?upgraded={planKey}&session_id={CHECKOUT_SESSION_ID}` (Wave 3 Phase 7.2 shipped). Founder only needs to set the env var. | Vercel + Railway `Frontend:Url` env var | After successful Checkout, users hit a broken URL. |
| **Clerk post-signup redirect to `/welcome`** | In Clerk dashboard for the `golden-alpaca-43` instance (and any production instance), set the post-sign-up redirect URL to `/welcome` | Clerk dashboard → Paths configuration | New sign-ups skip the welcome funnel and land on a default route. |
| **Status page link in marketing footer** | Host an external status board (Instatus, BetterStack, etc.) and set the URL | `project-proculink/.env` → `NEXT_PUBLIC_STATUS_URL` (currently empty) | Footer link is hidden — no visible broken link, but customers cannot self-check uptime. |
| **`/watch` walkthrough video** | Record a 60-90 second Loom and paste the embed URL | `project-proculink/.env` + Vercel → `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL` | `/watch` shows a "video is being recorded" placeholder card. |
| **"Book a 15-min demo" CTA** | Create a Cal.com or Calendly slot and paste the booking URL | `project-proculink/.env` + Vercel → `NEXT_PUBLIC_BOOK_DEMO_URL` | Book-a-demo card on Pilot upload/billing screens is hidden. |
| **Support form SMTP (optional)** | Provide Resend/SMTP credentials so `MailKitEmailSender` actually delivers `support@proculink.eu` emails. Without these, form returns 200 OK but emails go to the console log via `ConsoleEmailSender` (dev fallback). | Railway API: `Smtp:Host`, `Smtp:Port`, `Smtp:Username`, `Smtp:Password`, `Smtp:From`, `Smtp:SupportTo` | Support form submissions are not actually delivered to the inbox. |
| **DPA counter-signature workflow** | Founder receives `legal@proculink.eu` inbox, signs incoming DPAs within 5 business days as committed in `/dpa` | Operational, not code | Customer-facing trust commitment becomes false. |
| **Subprocessor change-notification subscriber list** | Founder maintains the manual subscriber list (currently no SaaS-backed list); emails the list 30 days before any subprocessor change | Operational, not code | Customer trust commitment in `/subprocessors` becomes false. |
| **Cookie banner copy review** | Review the banner copy live on the marketing site (incognito tab) to confirm tone matches brand voice | Browser smoke test | Cosmetic only. |
| **Plan file step checkboxes** | `docs/superpowers/plans/2026-05-28-group-l-trust-onboarding-commercial.md` has been incrementally updated by chips with `[x]` on completed steps; may have drift vs reality | Read-only audit | Future agents reading the plan may re-implement already-shipped work. |

### Group E provider decision (May 25 2026)

Do not implement Group E as Anthropic-only. Use a provider-neutral `IAiMappingService` with OpenAI structured outputs as the first provider because SKU suggestion needs cheap, fast, schema-bound JSON with confidence and provenance.

Required behavior:
- no-op when no AI API key is configured;
- run only after deterministic mapping lookup leaves a line unresolved;
- never auto-apply suggestions;
- every suggestion shows supplier code, confidence, reason, and provenance;
- frontend may prefill unresolved fields, but must visibly label them as `AI suggested`.

Config direction:
- `Ai:Provider = "openai"`
- `Ai:OpenAI:ApiKey`
- `Ai:OpenAI:MappingModel = "gpt-5-mini"`

Claude/Anthropic can be added later behind the same interface for heavier reasoning, but it is not the Group E default.

---

## Active repos
- Backend: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink` (branch: `main`)
- Frontend: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (branch: `main`)
- API dev port: `:5223` · FE dev port: `:8082`
- DB: `Host=localhost;Port=5435;Database=proculink_dev`

## Latest commits / push state

### Backend (`ProcuLink`) — branch `main` (Wave 2 fully merged)

| Commit | What |
|---|---|
| (merge) | merge: Group L Wave 2 — sample order + event emitters + Phase 5.1 hasResolvedMapping |
| `0220fd8` | feat(analytics): emit org_created + first_supplier/upload/transform/delivery + billing events |
| `b7fa374` | feat(analytics): add FakeAnalyticsService test double for emitter tests |
| `524b080` | feat(sample): POST /api/onboarding/sample-order — sample-order service + controller + 3 tests |
| `ffe7418` | feat(sample): add sample-order.csv fixture + IsSample flags + quota skip |
| `fb84587` | feat(onboarding): add hasResolvedMapping flag to /api/onboarding/status |
| `81f6166` | merge: Group L Wave 1 — Phase 1 gdpr.md + Phase 4.1/4.2 backend analytics |
| `e9811e8` | docs: mark Wave 3 + Wave 4 complete in CLAUDE.md + STATUS.md |
| `32e0f41` | docs: update CLAUDE.md + STATUS.md to pass 15, Wave 1/2 verified, Group K done |
| `11f7935` | feat(analytics): PostHog backend client wrapper (no-op when key absent) |
| `8ff3b3f` | docs(analytics): PostHog event taxonomy v1.0 |
| `367c07f` | fix(tests): resolve 48 InMemory JsonDocument test failures |
| `3fbff22` | feat: Wave 3+4 — invoice/ASN models, UBL parser, API keys, integration triggers |

**Test state:** 211/211 pass (102 Transform + 11 Api.Tests + 98 Infrastructure), 0 failures.

### Frontend (`project-proculink`) — branch `main`

| Commit | What |
|---|---|
| `f09390b` | feat(help): /help landing + 7 MDX articles + Fuse.js search |
| `d125413` | build(help): enable .mdx via @next/mdx |
| `1e8997c` | feat: Group I pass 11 — live backends, inbound/changelog/onboarding |
| `a0c64cd` | fix(dev): skip Sentry wrapping in dev mode |
| `5f119d8` | feat: Wave 4 frontend — API Keys tab + Connectors/Webhooks settings |

**Build state:** `bun run build` passes. Existing warnings: Sentry global error handler, `onRequestError`, Browserslist age, Next ESLint plugin.

---

## UI fixes applied (May 24 2026)
- `MarketingNav`: canonical `ProcuLinkMark` (size 30, text 18px) — was wrong ellipse shape and too small
- `BridgeSidebar`: logo now white and sized correctly (28px mark, 17px text, 56px height)
- `BridgeTopbar`: height bumped to 56px to match sidebar logo row
- `WireTopology`: traveller motion is now attached same-path SVG segments, not standalone pulse dots
- `SpineReview`: two-row header (endpoints top, StatusJourney full-width below) — was cramped
- `SpineReview` DocumentAnatomy: zone labels moved left, overflow hidden — was bleeding into center column
- `PricingPage`: hero merged with card section (no blank gap), subtitle uses `<br>`, 3-col fixed grid
