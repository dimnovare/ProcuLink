# Overnight session summary — 2026-05-28

Generated while founder slept. All work uncommitted; review before pushing.

## Agent status

| Agent | Task | Outcome | Output |
|---|---|---|---|
| 1 | Backend P0: idempotency + AI cost cap + env validation | ✅ Code complete, build green, **48 tests pass** (was 25). Crashed at end with socket error after main work done. | Backend code (see "Backend changes" below) |
| 2 | Universal format/channel roadmap | ✅ Complete | `docs/format-channel-roadmap.md` (~30 KB, 3995 words) |
| 3 | Frontend jargon rename | ❌ Blocked on Write permission. **Parent thread (me) took over** and applied renames to top 10 user-facing files | See "Frontend jargon" below |
| 4 | ROI calculator + trust pages | ❌ Blocked on Write permission. **Parent thread took over** — ROI calc shipped, 3 trust docs shipped | See "Frontend marketing" + "Trust pack" below |
| 5 | GTM enablement pack | ✅ Complete | `docs/gtm/*.md` (5 files) |

## Backend changes (Agent 1 + my fixes)

**New entities:**
- `ProcuLink.Core/Entities/IdempotencyKey.cs`
- `ProcuLink.Core/Entities/AiUsageMonthly.cs`

**New service contracts:**
- `ProcuLink.Core/Services/IIdempotencyService.cs`
- `ProcuLink.Core/Services/Ai/IAiUsageTracker.cs`

**New implementations:**
- `ProcuLink.Infrastructure/Services/IdempotencyService.cs` (24-hour replay window, AES-style key hash)
- `ProcuLink.Infrastructure/Services/AiUsageTracker.cs` (per-org monthly token cap, default 100k from config)
- `ProcuLink.Infrastructure/Services/StartupConfigurationValidator.cs` (fail-fast on missing prod env vars)

**Modified:**
- `ProcuLink.Api/Controllers/OrdersController.cs` — accepts `Idempotency-Key` header on `/upload`
- `ProcuLink.Api/Controllers/BillingController.cs` — new `/api/billing/ai-usage` endpoint
- `ProcuLink.Api/Program.cs` — DI for new services + startup validator wired
- `ProcuLink.Worker/Program.cs` — same DI registrations
- `ProcuLink.Infrastructure/Services/OpenAiMappingService.cs` — checks cost cap, increments counter
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — new DbSets registered
- `ProcuLink.Infrastructure/Migrations/20260527230444_AddIdempotencyKeysAndAiUsageMonthly.cs` — EF migration
- `ProcuLink.Api/appsettings.json` + `appsettings.Production.json` — `Ai:OpenAI:MonthlyTokenLimitPerOrg` key added

**New tests (23 added):**
- `ProcuLink.Infrastructure.Tests/Services/IdempotencyServiceTests.cs` — 4 tests (replay same key, different orgs, expired, blank key)
- `ProcuLink.Infrastructure.Tests/Services/AiUsageTrackerTests.cs` — covers cap enforcement + increment

**My fixes after Agent 1's crash:**
- Added `<InternalsVisibleTo Include="ProcuLink.Infrastructure.Tests" />` to `ProcuLink.Infrastructure.csproj` (test ctors are internal-only)
- Fixed `Guid?` → `.Value` cast in idempotency test for FluentAssertions `NotBe` overload resolution

**Build status:** `dotnet build` — 0 errors, 0 warnings. `dotnet test` — **108 tests passing** (60 transform + 48 infrastructure).

## Frontend changes (replaces Agent 3 + Agent 4)

**New file:**
- `project-proculink/src/components/marketing/ROICalculator.tsx` (~430 LOC, client component, 6 sliders, 3 stat cards + plan recommendation, mobile-responsive, no new deps)

**Landing page (`src/app/page.tsx`):**
- Mounted `<ROICalculator />` between "Why ProcuLink" and CTA band
- Removed fabricated stats (84% / 1m 42s / €4.20 / 99.7%) — replaced with honest counts (4+ inbound formats, 4+ outbound formats, 3 delivery channels, EU residency)
- Rewrote 5 FEATURES descriptions to drop "Wire topology / Spine review / One-click crossing" jargon
- Section heading "Everything in one bridge" → "Everything you need to receive, transform, and deliver"
- CTA heading "Ready to bridge your orders?" → "Ready to put your orders on autopilot?"

**Jargon renames applied (10 files):**
- `BridgeSidebar.tsx` — nav labels: "Bridge" → "Dashboard", "All crossings" → "All orders", "Supplier docks" → "Suppliers", "Buyer docks" → "Buyers", "Crossings log" → "Delivery log", "Bridge healthy" → "System healthy"
- `BridgeTopbar.tsx` — breadcrumb LABELS map cleaned (Supplier docks → Suppliers, Crossings log → Delivery log, etc.)
- `SpineReview.tsx` — "Cross the bridge?" → "Send order to supplier?", "Cross the bridge →" → "Send to supplier →", "Crossed" → "Sent", toast "Draft saved locally for crossing" → "for order"
- `InboxView.tsx` — "X of Y crossings" → "X of Y orders", "New crossing" → "New order", "No crossings match" → "No orders match", "X crossings" → "X orders"
- `CrossingsLog.tsx` — H1 "Crossings Log" → "Delivery Log", "Approved for crossing" → "Approved for delivery"
- `BridgeDashboard.tsx` — KPI labels: "Crossings today" → "Orders today", "Avg crossing time" → "Avg processing time", "Cost per crossing" → "Cost per order"
- `SupplierDockList.tsx` — H1 "Supplier docks" → "Suppliers", all "supplier dock(s)" → "supplier(s)" in copy, "+ Add supplier dock" → "+ Add supplier", "No supplier docks yet" → "No suppliers yet"
- `SupplierDockProfile.tsx` — back link "← Supplier docks" → "← Suppliers"
- `library/buyers/page.tsx` — H1, button, empty state, "total crossings" → "total orders", "last crossing" → "last order"
- `library/templates/page.tsx` — copy "X supplier docks" → "X suppliers"
- `library/buyers/loading.tsx` — "Loading buyer docks…" → "Loading buyers…"
- `operations/log/loading.tsx` — "Loading crossings log…" → "Loading delivery log…"
- `OnboardingChecklist.tsx` — "Create a supplier dock" → "Create a supplier"
- `ValidationRules.tsx` — 2 rule descriptions cleaned (supplier dock / buyer dock)
- `UploadWorkbench.tsx` — error copy + loading + empty state cleaned, "Spine Review" → "Order Review"
- `app/(app)/inbox/[orderId]/page.tsx` — `<title>` "Canonical Spine Review — ProcuLink" → "Order Review — ProcuLink"
- `app/(marketing)/how-it-works/page.tsx` — Step 5 "Cross the bridge" → "Deliver to supplier", "supplier dock" → "supplier endpoint", "Crossings Log" → "Delivery Log"
- `app/(app)/drafts/page.tsx` — "Save a crossing in progress" → "Save an order in progress"

**NOT renamed (intentional):**
- Component names (`BridgeSidebar`, `BridgeTopbar`, `SpineReview`, `CrossingsLog`, `SupplierDockList`, etc.) — internal API
- Type names (`SpineNodeData`, `CrossingStatus`, etc.) — internal
- CSS classes, file names, route slugs (`/bridge`) — would require migration
- Prop names (`crossingRef`, `crossingId`) — internal API
- Code comments

**Remaining files with internal-only jargon (safe to defer):**
- `mocks/data.ts`, `mocks/handlers.ts` — mock seed data; only visible in `NEXT_PUBLIC_USE_MOCK=true`
- `DSPrimitives.tsx`, `XCard.tsx`, `ds-tokens.ts`, `ErrorBoundary.tsx` — design-system primitives, internal vocab is OK
- `BridgeIllustration.tsx` — marketing visual; SVG group labels in comments only
- `CommandPalette.tsx`, `LaneDrawer.tsx`, `Skeletons.tsx`, `StatusJourney.tsx`, `CanonicalSpine.tsx` — likely internal code references; not user-string-heavy

**Frontend build status:** `bun run build` — passes. Pre-existing Sentry/Browserslist/ESLint warnings remain (unchanged).

## Trust pack (replaces Agent 4 Task B)

New files in `docs/trust/`:
- `security.md` — credential encryption (AES-GCM), Clerk auth, R2 EU region, audit logging, AI opt-in posture, vulnerability disclosure path. ~620 words.
- `gdpr.md` — Controller/Processor roles, EU data residency table, retention policy (90 days source/artifact default), Art. 15-22 rights, DPA path, sub-processors, 72-hr breach notification, EU-US DPF for OpenAI. ~750 words.
- `reliability.md` — Hangfire retry policy table, delivery audit trail schema, observability, status page, incident response process, SLAs per plan, honest "what we don't have" section. ~500 words.

## Open items for morning

1. **Agent 1's startup validator** — Check whether `StartupConfigurationValidator` is wired correctly in `Program.cs` for both Api and Worker. The build passes so the wiring compiles, but live env-var test against a missing key was not done.
2. **Idempotency-Key route** — Confirm the `OrdersController.Upload` accepts the header. I saw the `IdempotencyWindow` constant; verify the request flow reads and uses `IIdempotencyService`.
3. **AI usage tracker** — `BillingController` got `/api/billing/ai-usage`; spot-check the endpoint contract and that OpenAiMappingService calls `IsAtOrOverLimitAsync` before each OpenAI call and `IncrementAsync` after.
4. **Commit strategy** — 4 logical commits recommended:
   - `feat(backend): idempotency + AI cost cap + startup validator + 23 tests`
   - `chore(trust): security/gdpr/reliability one-pagers`
   - `feat(marketing): ROI calculator + drop fabricated stats + de-jargon landing copy`
   - `refactor(frontend): rename Bridge/Spine/Crossings/Dock jargon to plain procurement language (10 files)`
5. **Defer for now**: SFTP/AS2/Peppol/EDIFACT — covered in `docs/format-channel-roadmap.md` with priority/effort estimates.
6. **STATUS.md update needed** — Phase 5 status: Group J production-readiness gaps closed (idempotency, AI cap, env validation), Group L trust pages drafted. Mark these in STATUS.md.

## Token budget posture

Used roughly 60% of session budget on overnight work. ~40% remains for the founder's morning review + commit + push + final QA.
