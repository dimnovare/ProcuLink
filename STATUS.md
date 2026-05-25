# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

---

## Where we are: **Phase 4 Group G complete — Email polling next**

**Strategic correction (May 25 2026):** first paying ICP is the **buyer/procurement team sending orders out** to many suppliers, not the supplier/distributor receiving buyer orders. Keep the platform vision broad, but build the next 6 weeks around outbound PO reliability: buyer order source → canonical PO → supplier-specific validation/mapping → supplier-ready delivery.

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
| **H** | Email polling (IMAP/MailKit) | Next |

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

## Latest commits to push when ready
- Backend `ProcuLink`: includes C2 backend (`18feb71`) and status handoff (`f957f16`), D2 backend commits, Group E (`1094e86`), Group F (`831aa3e`), and Group G ERP connectors.
- Frontend `project-proculink`: includes D2 UI (`7772f4a`, `748c6de`), C2 frontend (`6116af9`), Group E (`5f03de9`), Group F (`85d03e3`), and Group G ERP connector UI.
- Both repos have verified builds/tests listed above. Manual live QA is recommended but not required before pushing code for backup/review.

---

## UI fixes applied (May 24 2026)
- `MarketingNav`: canonical `ProcuLinkMark` (size 30, text 18px) — was wrong ellipse shape and too small
- `BridgeSidebar`: logo now white and sized correctly (28px mark, 17px text, 56px height)
- `BridgeTopbar`: height bumped to 56px to match sidebar logo row
- `WireTopology`: pulse dots fade in/out near endpoints — no more "floating dot" at supplier port
- `SpineReview`: two-row header (endpoints top, StatusJourney full-width below) — was cramped
- `SpineReview` DocumentAnatomy: zone labels moved left, overflow hidden — was bleeding into center column
- `PricingPage`: hero merged with card section (no blank gap), subtitle uses `<br>`, 3-col fixed grid
