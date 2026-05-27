# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

---

## Where we are: **Phase 5 in progress — Group I UI/UX polish pass 9 complete**

**Strategic correction (May 25 2026):** first paying ICP is the **buyer/procurement team sending orders out** to many suppliers, not the supplier/distributor receiving buyer orders. Keep the platform vision broad, but build the next 6 weeks around outbound PO reliability: buyer order source → canonical PO → supplier-specific validation/mapping → supplier-ready delivery.

**Production direction (May 26 2026):** ProcuLink is no longer being treated as a throwaway MVP. The next work should make the product feel trustworthy and usable end-to-end: UI/UX polish, mobile responsiveness, live QA of billing/delivery/email, and then engine hardening for broader input/output standards.

### Phase 5 grouped roadmap

Source of truth for the next grouped plan:
`docs/superpowers/plans/2026-05-26-production-hardening-roadmap.md`.

| Group | Workstream | Status | Why |
|---|---|---|---|
| **I** | UI/UX production polish + responsive QA | **In progress — pass 9 complete** | The product must feel reliable before more engine depth is layered on top. Passes 1-9 fixed the known Wire Topology traveller/visibility defects, added Playwright QA, tightened mobile shell/order review behavior, cleaned upload/settings responsive defects, fixed inbox/dock/log/webhook mobile layout issues, made supplier detail/mapping/delivery forms usable on mobile, added resilient settings plus connector/webhook configuration states, completed library interaction panels for mappings/rules/templates, tightened upload/supplier plan-gated states, and added visible local feedback for draft test/save flows. More first-upload-to-delivery live QA remains. |
| **J** | Live end-to-end QA + deployment hardening | Planned after I | Verify Clerk, Stripe, upload, mapping, transform, delivery, ERP test-fire, and IMAP polling against real deployed services. |
| **K** | Standards + engine hardening | Planned after I/J scoping | Expand toward explicit standards coverage: cXML, UBL/Peppol BIS Order, common EDI order formats, supplier CSV/XLSX templates, API/webhook payload templates, and later invoices/other documents. |
| **L** | Trust, onboarding + commercial readiness | Planned; can overlap after I starts | Add onboarding, product copy clarity, trust/security pages, support/legal basics, demo data, analytics, and case-study hooks. |

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
| **I** | UI/UX production polish + responsive QA | **In progress — pass 9 complete** |
| **J** | Live end-to-end QA + deployment hardening | Planned after I |
| **K** | Standards + engine hardening | Planned after I/J scoping |
| **L** | Trust, onboarding + commercial readiness | Planned; can overlap after I starts |

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

Must address:
- Continue desktop/tablet/mobile QA for the full first-upload-to-delivery happy/error paths against a running API, including real save/test-fire persistence for connector/webhook/mapping/rule/template forms in Group J.
- App shell, sidebar, topbar, route labels, active states, and mobile navigation.
- Core flow polish: sign-in, first upload, inbox/review, mapping, transform, delivery, settings/billing/email.
- Empty, loading, error, disabled/read-only, and plan-gated states.

Do **not** introduce a new visual direction. Keep Direction 4 — The Bridge Layer,
supported by Direction 3 — System Identity.

### Group J — live end-to-end QA + deployment hardening

Verify deployed Vercel/Railway behavior with real test service configuration:
Clerk, Stripe Checkout/Portal/webhooks, upload/parse/transform/download,
HTTP delivery test-fire, ERP sandbox/stub test-fire, IMAP polling, Sentry/logging,
CORS, database migrations, and production env vars.

### Group K — standards + engine hardening

Do not start broad implementation until a standards matrix is written. The matrix
must define support level, fixtures, validation depth, plan gate, and owner for:
cXML, UBL/Peppol BIS Order, supplier CSV/XLSX templates, JSON/API payload
templates, EDI order formats, and OCR/scanned PDF support.

### Group L — trust, onboarding + commercial readiness

Add onboarding, demo data, concrete ROI copy, trust/security/legal/support pages,
analytics event plan, and sales/demo assets after UI polish begins.

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
- Backend `ProcuLink`: includes C2 backend (`18feb71`) and status handoff (`f957f16`), D2 backend commits, Group E (`1094e86`), Group F (`831aa3e`), Group G ERP connectors, and Group H email polling.
- Frontend `project-proculink`: includes D2 UI (`7772f4a`, `748c6de`), C2 frontend (`6116af9`), Group E (`5f03de9`), Group F (`85d03e3`), Group G ERP connector UI, and Group H settings UI.
- Phase 5 roadmap is now documented. Current implementation group is **Group I**; pass 9 has completed visible local feedback for connector/webhook/mapping/rule/template draft flows, and first-upload-to-delivery live QA should continue before Group J.
- Both repos have verified builds/tests listed above. Manual live QA is recommended but not required before pushing code for backup/review.

---

## UI fixes applied (May 24 2026)
- `MarketingNav`: canonical `ProcuLinkMark` (size 30, text 18px) — was wrong ellipse shape and too small
- `BridgeSidebar`: logo now white and sized correctly (28px mark, 17px text, 56px height)
- `BridgeTopbar`: height bumped to 56px to match sidebar logo row
- `WireTopology`: traveller motion is now attached same-path SVG segments, not standalone pulse dots
- `SpineReview`: two-row header (endpoints top, StatusJourney full-width below) — was cramped
- `SpineReview` DocumentAnatomy: zone labels moved left, overflow hidden — was bleeding into center column
- `PricingPage`: hero merged with card section (no blank gap), subtitle uses `<br>`, 3-col fixed grid
