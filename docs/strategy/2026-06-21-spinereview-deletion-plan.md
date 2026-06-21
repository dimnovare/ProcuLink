# Task #114 — SpineReview deletion plan (2026-06-21, read-only scoping)

> **✅ PR-1 SHIPPED 2026-06-21 (`3520ed4`, pushed to FE `main` → Vercel).** Ported the invoice
> badge + `delivery_dead_letter` banner + the three failure-state gates (`failed`→ParseFailedPanel
> with the order-audit query, `transform_failed`/`delivery_failed`→FailedPanel) into `OrderWorkshop`,
> repointed `/inbox/[orderId]` to mount `OrderWorkshop` directly, and deleted `SpineReview.tsx`
> (−2,587), `EdgeRails.tsx`, the `SpineReviewSkeleton` export, and `src/lib/flags.ts`. `bun run build`
> clean; vitest 517/517; grep-clean of `SpineReview`/`isOrderWorkshopEnabled`/`lib/flags`/
> `SpineReviewSkeleton`. Diff reviewed (rules-of-hooks held — gates are early returns after all hooks;
> the port strictly ADDS the failure/invoice rendering prod's workshop lacked, so it cannot regress
> those paths). **Remaining: PR-2** (the triage-cluster orphan island + `SpineConnectors` + 2 tests)
> and the PLAYWRIGHT_LIVE e2e rewrite (`live-po-loop`/`error-recovery`/`order-detail-happy-path` —
> not in CI). Live-verify the failure/invoice render paths on prod when a real failed/invoice order
> is available.

> The legacy `SpineReview` (2,587 lines) still mounts the order-review route and internally forks to the v3 `OrderWorkshop`. Prod runs the workshop via a **Vercel env override** (`NEXT_PUBLIC_ORDER_WORKSHOP_V2`); the in-repo flag default is OFF. This plan removes SpineReview so the route mounts `OrderWorkshop` directly. **~5,000 lines removed across 2 PRs.** Do in a focused session — the deletion is mechanically safe (typed import graph) but the **gate port (below) has NO compile-time guard** and is the real risk.

## ⚠ CRITICAL — port these to OrderWorkshop BEFORE deleting (they live in SpineReview *before* the fork; the workshop lacks them → a LATENT prod gap today, since prod already runs the workshop)
1. **Invoice badge** — SpineReview.tsx:326-338,2041 renders the amber "Looks like an invoice" banner for `documentType==="invoice"`. OrderWorkshop header (line 355) has none. (Server still force-holds invoices in `pending_review`, so it can't be wrongly sent — but the *visible explanation* is gone.)
2. **Failure-state gates** — SpineReview.tsx:1988-1997: `failed`→`<ParseFailedPanel>`, `transform_failed`→`<FailedPanel stage="transform">`, `delivery_failed`→`<FailedPanel stage="delivery">`, plus the inline `delivery_dead_letter` banner (header ~2066). **OrderWorkshop has NO failure rendering** — a failed order falls through to the normal mapper (only `useSendFlow`'s notice strip shows). Port these (reuse the KEPT `FailedPanels.tsx`) into OrderWorkshop before its main return.
3. (minor) Load-error copy parity: SpineReview "Check your connection and try again." vs OrderWorkshop "Something went wrong loading this order."

## Mount path (confirmed)
`src/app/(app)/inbox/[orderId]/page.tsx:14` → `<SpineReview orderId={orderId}/>`. SpineReview.tsx:2004-2006 → `if (isOrderWorkshopEnabled(searchParams)) return <OrderWorkshop orderId={orderId}/>`. OrderWorkshop is reachable ONLY via this fork today.

## Safe to delete (referenced only by SpineReview or its triage-only cluster)
`SpineReview.tsx`, `EdgeRails.tsx`, `SpineConnectors.tsx`(+test), `review/stageModel.ts`(+test), `review/{FixQueueTriage, ContextStage, SendReadinessCard, CalibrationInsightCard, useBreakpointBand, ManualCodeRow, AiSuggestionContent, Kbd, HeaderInlineEditField}`, and SpineReview-private: `DocumentAnatomy, MobileSpineAccordion, TabletSpineLayout, ParsedChip, ConfChip, InvoiceBadge, TotalsSummary, SpineNodeCard, ZoneMarker, buildNodesFromOrder`. Delete the `SpineReviewSkeleton` export from `Skeletons.tsx` (keep the file). Delete `src/lib/flags.ts` (the workshop flag is the only consumer; now unconditional).

## DO NOT delete (shared / still live)
`StatusJourney` (Inbox, Dashboard), `FileChip`, `StandardsFieldPopover` (PoMappingEditor), `OrderPassport`/`ConformancePanel`/`SupplierResponsePanel` (workshop OrderDetailsDrawer), **`FailedPanels.tsx`** (move into OrderWorkshop), `review/orderDisplay.ts`/`buildFixQueue.ts`/`ConfirmDialog.tsx`/`confirmPolicy.ts`/`hooks/*`/`OutputPreview.tsx` (workshop OutputZone)/`CatalogHintCard.tsx`+`catalogHint.ts` (SupplierDockProfile)/`calibrationDisplay.ts` (own honesty test).

Pre-existing dead (NOT #114, flag separately): `workshop/{ReceivedZone, OutputZone, MappingPanel}.tsx` are imported only by their own tests — OrderWorkshop deliberately doesn't compose them.

## PR split (keep each diff reviewable/reversible)
- **PR-1 (small):** port the §gates into OrderWorkshop → repoint `page.tsx` to `<OrderWorkshop>` → delete `SpineReview.tsx` + `EdgeRails.tsx` + `SpineReviewSkeleton` export + `flags.ts`/fork. ~−2,650/+60.
- **PR-2 (orphan island):** delete the 11 triage-cluster files + `SpineConnectors` + 2 tests. ~−2,000/−2,500.

## Tests
- vitest: delete `SpineConnectors.test.ts` + `stageModel.test.ts` with their sources; KEEP `buildFixQueue/calibrationDisplay/catalogHint/workshop/*` tests.
- e2e (PLAYWRIGHT_LIVE-gated, not CI — rewrite, don't block): `live-po-loop.spec.ts` is heavily coupled to `?view=triage`/`fix-queue-triage`/`context-stage`/`stage-breadcrumb` testids → rewrite to the OrderWorkshop surface. `error-recovery.spec.ts`: update the "check your connection" string. Grep `live-po-failure-states.spec.ts` (depends on the §2 failure panels existing in the workshop).

## Verification checklist
`bun run build` clean (the typed import graph catches dangling refs) · vitest green after removing deleted-source tests · live render a real order (no `?workshop=`) → workshop renders, PO/issues/mapper/Send work · **render an invoice-classified order → "Looks like an invoice" survived** · **render failed/transform_failed/delivery_failed orders → failure panels render (highest-risk regression)** · grep repo for residual `SpineReview`/`view=triage`/`fix-queue-triage`.
