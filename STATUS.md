# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

---

## Where we are: **Phase 4 Group D2 — Supplier Delivery Config**

### Completed phases
| Phase | What was built |
|---|---|
| Phase 0–3 | Auth, Postgres, Core loop, Sellable MVP |
| Next.js migration | App Router, Clerk, all routes, middleware |
| Group A | Tech debt (bun remove lovable-tagger, controller cleanup) |
| Group B | Marketing pages (landing, pricing, how-it-works) |
| **Group C** ✅ | Stripe billing — all 12 tasks done and pushed to both repos |
| **Group D** ✅ | PO Field Mapping Engine — all 12 tasks done and pushed to both repos |

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

## Group D2 — Supplier Delivery Config

**Status: Not started**

### What Group D2 builds
Per-supplier delivery configuration: HTTP webhooks, SFTP, FTP — protocol selection, auth credentials, test-fire. Non-developer friendly UI for configuring how mapped POs are delivered to each supplier.

---

## After Group D

| Group | What | Status |
|---|---|---|
| **D2** | Supplier delivery config (HTTP/SFTP/FTP, auth, test-fire) | **Next** |
| **E** | AI mapping suggestions via Claude API | Not started |
| **F** | PDF ingestion (`PdfPig`) | Not started |
| **G** | ERP connectors (Erply, Directo) | Not started |
| **H** | Email polling (IMAP/MailKit) | Not started |

---

## Active repos
- Backend: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink` (branch: `main`)
- Frontend: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (branch: `main`)
- API dev port: `:5223` · FE dev port: `:8082`
- DB: `Host=localhost;Port=5435;Database=proculink_dev`

---

## UI fixes applied (May 24 2026)
- `MarketingNav`: canonical `ProcuLinkMark` (size 30, text 18px) — was wrong ellipse shape and too small
- `BridgeSidebar`: logo now white and sized correctly (28px mark, 17px text, 56px height)
- `BridgeTopbar`: height bumped to 56px to match sidebar logo row
- `WireTopology`: pulse dots fade in/out near endpoints — no more "floating dot" at supplier port
- `SpineReview`: two-row header (endpoints top, StatusJourney full-width below) — was cramped
- `SpineReview` DocumentAnatomy: zone labels moved left, overflow hidden — was bleeding into center column
- `PricingPage`: hero merged with card section (no blank gap), subtitle uses `<br>`, 3-col fixed grid
