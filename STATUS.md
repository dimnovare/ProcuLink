# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

---

## Where we are: **Phase 4 Group D — PO Field Mapping Engine**

### Completed phases
| Phase | What was built |
|---|---|
| Phase 0–3 | Auth, Postgres, Core loop, Sellable MVP |
| Next.js migration | App Router, Clerk, all routes, middleware |
| Group A | Tech debt (bun remove lovable-tagger, controller cleanup) |
| Group B | Marketing pages (landing, pricing, how-it-works) |
| **Group C** ✅ | Stripe billing — all 12 tasks done and pushed to both repos |

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

## Group D — PO Field Mapping Engine

**Status: Design spec done. Implementation plan NOT yet created.**

### Design doc
`docs/superpowers/specs/2026-05-24-bulk-mapping-import-export-design.md`

### What Group D builds
Replaces hardcoded column aliases in `CsvOrderParser` with per-supplier **mapping templates** stored as JSONB. Each template maps supplier CSV columns → canonical fields and applies manipulators (Replace, Trim, DateFormat, Concat, Fallback, Split, Multiply, Divide).

### Architecture decisions (from brainstorm)
- **Template-based**: `SupplierPoMapping` entity with `config_json` JSONB on `supplier_profiles`
- **Engine in Transform layer**: `PoMappingEngine` in `ProcuLink.Transform` — applies template to raw CSV rows
- **8 field manipulators**: Replace, Trim, DateFormat, Concat, Fallback, Split, Multiply, Divide
- **Bulk import/export**: CSV format for template portability between suppliers
- **Frontend**: Visual mapping editor on supplier detail page

### Next action
Run `/superpowers:write-plan` with spec at:
`docs/superpowers/specs/2026-05-24-bulk-mapping-import-export-design.md`

---

## After Group D

| Group | What | Status |
|---|---|---|
| **D2** | Supplier delivery config (HTTP/SFTP/FTP, auth, test-fire) | Not started |
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
