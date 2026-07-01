## 02. Inbox — Order Work Queue — `/inbox`

- **File:** `src/app/(app)/inbox/page.tsx` (thin wrapper; metadata `title: "Inbox — ProcuLink"`, renders `<InboxView />`)
- **Key components:**
  - `src/components/bridge/InboxView.tsx` (the whole page — 1581 lines, `"use client"`, TanStack Table)
  - `src/components/bridge/layout/PageShell.tsx` (`variant="wide"`, max-width `var(--container-wide)` ≈ 1480px, grey `var(--bg)` canvas)
  - `src/components/bridge/layout/PageHeader.tsx` (title + sub + actions slot)
  - `src/components/bridge/StatusJourney.tsx` (compact 5-node pipeline track + `CrossingStatus` type)
  - `src/components/bridge/FileChip.tsx` (coloured source-format tag)
  - `src/components/bridge/inboxSend.ts` (pure helpers: `isRedeliverable`, `shouldShowBulkBar`, `formatBulkSendResult`)
  - `src/components/bridge/BridgeLoader.tsx` (`BridgePageLoader` for `loading.tsx`)
  - Hooks: `src/hooks/useOrderDirection.ts`, `src/hooks/useSampleOrder.ts`, `src/hooks/useQueriesEnabled.ts`
  - Data: `src/lib/api-client.ts` (`getOrders`, `getOrdersSummary`, `redeliverOrder`), types in `src/types/procurement.ts`
- **Capture URL (mock):** `/inbox` (the list renders 50 generated mock rows; a row click navigates to `/inbox/demo-001` — `demo-001` is the first seeded mock detail id)

### What it is & why it exists
The Inbox is the operator's work-queue: every order that has entered ProcuLink (uploaded, emailed, or API-ingested) appears here as one row, showing where each order sits in the `Parse → Normalize → Validate → Transform → Deliver` pipeline. It is the triage surface for the *review* and *deliver* stages — the procurement coordinator opens it to answer "what needs me right now?" (the header literally reads "N need review · N failed"), to filter to the orders that are blocked or ready, and to click into a single order's review screen. It also offers a bulk "Send selected" path to re-deliver orders that are ready or whose delivery failed.

### Who uses it & the primary job
Primary persona: the **procurement coordinator / operator** running daily PO flow. The single most important task: **scan the queue, spot the orders that need attention (needs-review / failed), and open one** — i.e. each row's job is to communicate status + counterparties + value at a glance and route to `/inbox/[orderId]` on click. The secondary job is **bulk re-delivering** ready/failed orders without opening each one.

### Layout & structure (current)
Top-to-bottom inside a wide `PageShell` (grey canvas, content centred at ~1480px, gutter `16 → 24 → 34px`, vertical `20 → 28px`):

1. **PageHeader row** — `h1` "Inbox" (Bricolage Grotesque, 28→30px, weight 600, `var(--ink)` navy). Subtitle line (13px, `var(--ink-muted)`): `"{reviewCount} need review · {failedCount} failed"`, with `· N selected` appended in blue when rows are selected. Right-aligned actions: a **Sync** button (white outline, 32px, ↻ glyph that spins while `isFetching`, label flips to "Syncing…") and the **↑ Upload order** primary button (solid blue `#1E66C9`, white text, 32px, hover → `#0F4FA8`, routes to `/upload`).
2. **Bulk action bar** (conditional) — full-width navy (`#0B1A2F`) strip, radius 8, `mb-3`. Left: "{N} selected" headline + Clear/Dismiss text button. Right: a `role="status"` result line (green `#7FD18A` ✓ / red `#F2A6A6` ⚠) + a "Send selected" / "Sending…" text button. Shown when `selectedCount > 0` OR a `bulkResult` is still on display.
3. **Toolbar** (on grey canvas, `pb-3`) — left: a horizontal-scroll row of 5 **filter chips** (All orders / Needs review / Ready to send / Delivered / Failed), each 28px tall, with a mono count badge; active chip = navy fill, white text. Right: a **search box** (32px, white, `🔍` emoji + input, placeholder "Search PO, buyer, supplier…", capped 160–240px on `sm+`) and a **Columns** menu button (`lg+` only, `▦` glyph, toggles a dropdown).
4. **Queue table card** — a single floating white card (`#FFFFFF`, 1px `#E5E8EE` border, radius 12), `flex-1`, internally scrollable. Inside:
   - **Mobile (`< lg`):** a stack of route **cards** (`p-3` gap `2.5`), not the table.
   - **Desktop (`lg+`):** a fixed-layout `<table>` (`minWidth: 1180px`, `tableLayout: fixed`, `borderCollapse`, 12.5px) with a **sticky header** (`thead`, `position: sticky; top: 0; z-index: 4`, white bg, uppercase 10.5px/700 `var(--ink-faint)` labels, sort arrows). Rows are 56px tall with 1px `#F0F2F6` separators.
5. **Footer row** (on grey canvas) — left: `"{totalCount} orders"` (11px faint, `· N selected` in blue). Right: pagination — **← Prev**, mono `Page {currentPage} of {totalPages}`, **Next →** (28px buttons, disabled states greyed to `#CBD0DA`).

Density/type/spacing observations: spacing and sizing are highly *ad-hoc* — almost everything is inline `style` with literal px (`height: 32`, `padding: "9px 10px"`, `11px 10px`, font sizes `10.5 / 11 / 12 / 12.5 / 13 / 14px`, radii `6 / 8 / 10 / 12`). Colours are hard-coded hex constants (`BLUE = #1E66C9`, `NAVY = #0B1A2F`, plus `#5E6779`, `#E5E8EE`, `#F0F2F6`, `#CBD0DA`, `#FBE3E3` etc.) only loosely mapped to tokens. The status pill (`StatusDotPill`) *does* use the `.pill / .pill-*` token classes from globals.css.

### Data shown
Entity: **Order** (rendered as `OrderRow`, mapped from `OrderSummary`). Per row:

| Column (id) | Field shown | Source |
|---|---|---|
| `select` | checkbox (only enabled for `ready_to_deliver` / `delivery_failed` raw statuses) | `OrderRow.rawStatus` via `isRedeliverable` |
| `po` ("Order") | `poNumber` (mono semibold navy) + `"{lines} lines · {issues} to review"` sub | `OrderSummary.poNumber / lineCount / unresolvedCount` |
| `lane` ("Buyer → Supplier" / "Customer → You") | `buyer` (blue) → `supplier` (green); falls back to `labels.unknownBuyer` | `OrderSummary.buyerName / supplierName`; header from `useOrderDirection().labels.railHeader` |
| `fmt` ("Source") | `FileChip` (PDF/cXML/XLSX/EDI/EMAIL/API/CSV) | `OrderSummary.sourceFormat` → mapped to upper-case label |
| `value` ("Value") | `valueLabel` e.g. `€ 24,180.50` (mono semibold) | `OrderSummary.totalValue` + `currency` |
| `status` ("Pipeline") | `StatusJourney` compact 5-node track | `mapStatus(status)` → `STATUS_PRESENTATION[...].stage` |
| `statusPill` ("Status") | `StatusDotPill` (New/Extracting/Needs review/Normalized/Delivered/Ready to send/Failed) | `mapStatus(status)` |
| `ageMin` ("Updated") | `"{age} ago"` e.g. "2m ago", "1h ago" | derived from `OrderSummary.createdAt` |
| `chevron` | `›` affordance | static |

Data source:
- **List:** `apiClient.getOrders({ page, pageSize: 25, status, search })` → `GET /api/orders?page=&pageSize=&status=&search=` returning `{ items: OrderSummary[], totalCount, page, pageSize }`. Mock fn: `mockGetOrders` (filters/paginates `mockOrders` client-side).
- **Counts:** `apiClient.getOrdersSummary()` → `GET /api/orders/summary` returning `{ byStatus: Partial<Record<OrderStatus,number>>, total }`. Mock fn: `mockGetOrdersSummary`. Drives header summary + every chip badge in BOTH mock and live.
- **Mock mode:** `MOCK_ORDERS = generateOrders(50)` — 12 hand-seeded rows (ids `demo-001`, `nrd9981`, `sh44120`, `850201`, `wmt341`, `008411`, …) + 38 procedural (`gen-000012`…).
- **Bulk send:** `apiClient.redeliverOrder(id)` → `POST /api/orders/{id}/redeliver` (server-gated to `RedeliverableFrom = {delivery_failed, ready_to_deliver}`).

Important display nuance: the backend `ready` status renders as **"Normalized"** (pre-transform) while `ready_to_deliver` renders as **"Ready to send"** — deliberately split so the row badge can't contradict the "Ready to send" chip. The red "Failed" pill **collapses five** backend statuses (`failed`, `transform_failed`, `delivery_failed`, `delivery_dead_letter`, `rejected_by_supplier` = `FAILED_BUCKET`) into one display state.

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Sync button (header) | `queryClient.invalidateQueries(["orders"])` | refetches list; ↻ spins, label → "Syncing…", disabled while `isFetching` |
| ↑ Upload order button (header) | `router.push("/upload")` | navigates to upload page |
| Filter chip × 5 (All / Needs review / Ready to send / Delivered / Failed) | `handleChip(idx)` — mock: client column filter; live: sets `?status=` | re-filters list, resets to page 1, clears selection; active chip = navy fill |
| Search input | `handleSearch(value)` — mock: instant client filter; live: 350ms debounce → server `search=` | filters by PO / buyer / supplier; resets page 1, clears selection |
| Columns button (`lg+`) | `setColumnsMenuOpen(o => !o)` | opens the column-visibility dropdown (see overlays) |
| Column header (sortable: Order, Source, Value, Pipeline, Updated) | `header.column.getToggleSortingHandler()` — click or Enter/Space | toggles asc/desc; `aria-sort` announced; arrow ↑/↓/⇅ |
| Row checkbox | `row.getToggleSelectedHandler()` | selects row for bulk send; disabled (35% opacity, not-allowed) unless `isRedeliverable(rawStatus)`; `stopPropagation` so it doesn't open the row |
| Header "select all" checkbox | `table.getToggleAllPageRowsSelectedHandler()` | selects all *sendable* page rows; disabled (40% opacity) + explanatory title when none sendable |
| Table row (body) | `router.push('/inbox/{id}')` (whole `<tr>` onClick) | opens the order review screen |
| Mobile route card | `router.push('/inbox/{id}')` (`<button>`) | opens the order review screen |
| j / ArrowDown, k / ArrowUp | keyboard row highlight (desktop) | moves the active-row highlight, scrolls into view |
| Enter (with active row) | `router.push('/inbox/{activeRow.id}')` | opens the highlighted order |
| Bulk bar "Send selected" | `handleSendSelected()` → parallel `redeliverOrder(id)` | re-delivers selected; reports per-PO failures; keeps only failed rows selected |
| Bulk bar Clear / Dismiss | `setRowSelection({}); setBulkResult(null)` | clears selection / dismisses result bar |
| ← Prev / Next → (footer) | `setPage(...)` | paginates; disabled at ends |
| Empty-state "↑ Upload an order" | `router.push("/upload")` | navigates to upload |
| Empty-state "Try a practice order" | `sample.runSample()` (`useSampleOrder("/inbox")`) | `POST /api/onboarding/sample-order`, seeds a sample, routes to it; shows "Starting practice order…" + error line on failure |
| Error-state "↻ Retry" | `refetch()` | retries the orders query |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Columns visibility menu | dropdown / popover (`role="menu"`, absolute, white card, radius 8, shadow `0 8px 24px rgba(11,26,47,0.12)`, z-20) | the **Columns** toolbar button (`lg+` only) | "SHOW COLUMNS" heading + a `role="menuitemcheckbox"` toggle per hideable column (Source / Value / Pipeline / Updated) with a read-only checkbox + label | outside `mousedown` (doc listener), **Escape** key, or toggling the button again. (No backdrop scrim.) |
| Bulk action bar | inline panel (not an overlay — pushed into flow, navy strip below header) | selecting ≥1 row (`selectedCount > 0`) OR a `bulkResult` present | "{N} selected" headline, Clear/Dismiss, a `role="status" aria-live="polite"` result line, "Send selected"/"Sending…" button | "Clear" (clears selection) or "Dismiss" (clears result); auto-clears selection on a full success |
| Tooltips | native `title=` attributes | hovering disabled select checkboxes / select-all header | "Only orders that are Ready to send or have a failed delivery can be sent" etc. | pointer leave (browser-native) |

There are **no modal dialogs, drawers, or sheets** on this page — every "open" either is an inline panel (bulk bar), a single anchored dropdown (Columns), or a route navigation. The keyboard handler explicitly bails when `[role="dialog"]` / `[aria-modal]` exists, confirming none are expected here. Row clicks **navigate in place** to `/inbox/[orderId]` rather than opening a detail drawer.

### States
- **Empty (genuinely empty, no filter):** in-card centred state — `⊘` glyph, **"Your inbox is clear"** (20px/600 Bricolage on desktop, 14px on mobile), direction-aware copy ("New orders land here automatically as buyers/customers send them, or upload one yourself."), a blue **"↑ Upload an order"** button, and a secondary **"Try a practice order"** button (sample-order CTA). Rendered both in the desktop table body (`colSpan`) and the mobile card stack.
- **Empty (filtered/searched, 0 results):** distinct state — `⊘` glyph, **"No matching orders"**, "No orders match the current filter or search…", and a **"Clear filters"** button (`handleClearFilters`). No practice-order CTA in this branch.
- **Loading:** two layers. (1) Route-level `loading.tsx` → `BridgePageLoader` (the animated blue→green "wire" mark over `#F6F7FA`, reduced-motion frozen). (2) In-component **skeleton** for the first page load (`isInitialLoading = !mock && isLoading && !ordersPage`): desktop = 9 skeleton rows (one pulse bar per visible column, header stays mounted); mobile = 6 card-shaped skeletons. Subsequent page/filter fetches keep prior rows visible (`placeholderData: prev`) — no spinner flash.
- **Error:** full-screen replacement inside `PageShell` — a `#FBE3E3` circle with `⚠`, **"Couldn't load the queue"**, reassuring body ("your data is safe… Try again in a moment."), and a **"↻ Retry"** button (`refetch`). Triggered only in live mode on `isError`.
- **Success/feedback:** Sync button spinner + "Syncing…"; bulk bar `aria-live` result line ("N orders sent" green / per-PO failure list red); selection count echoed in header + footer; active-row blue inset ring on keyboard nav.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full desktop table inside the wide (~1480px) shell; sticky header; Columns menu visible (`lg+`); all 9 columns shown; toolbar chips + search + Columns on one row.
- **Tablet 768:** still **below the `lg` table breakpoint** — renders the **mobile route-card stack**, NOT the table (table is `hidden … lg:block`). Columns menu hidden. Toolbar collapses: chips on a horizontal-scroll row, search drops to its own full-width row (`sm:flex-row` brings them side-by-side at `sm`, but the table itself only appears at `lg`). This is a notable cliff: a 768–1023px viewport with plenty of width still shows phone-style cards instead of the table.
- **Mobile 390:** route cards — each card stacks PO# + age/lines/value sub, a status pill top-right, a FileChip + red "N to review" tag row, and a buyer→supplier rail that stacks vertically (`↓` connector) when a buyer exists. Primary upload action reachable via the header. Bulk-select checkboxes are **not present** on mobile cards (selection/bulk-send is desktop-only).

### Current UX issues
- **Glyph/emoji iconography instead of a single icon system.** The brief mandates Lucide icons, but this page uses literal characters: `↻` (sync/retry), `↑` (upload), `🔍` (search emoji), `▦` (columns), `⊘` (empty), `⚠` (error), `›` (chevron), `→`/`↓` (rail), `↑↓⇅` (sort). Inconsistent weight, baseline, and a11y; the `🔍` emoji especially clashes with the navy/violet brand.
- **Spacing/type/radius drift everywhere.** Sizing is inline literals off a non-4/8 scale (`12.5px`, `10.5px`, `11px`, `padding: "9px 10px"`, radii `6/8/10/12`, heights `28/32`). Violates the "one spacing rhythm / one type scale" bar. Should be tokenised.
- **Two parallel status systems on every row.** The "Pipeline" column (`StatusJourney`) and the "Status" pill encode the *same* state twice, eating ~308px of width and adding cognitive load. The brief wants ONE status-badge system.
- **Tabular figures only partially applied.** PO# and Value use `font-mono` (good), but counts ("14 lines · 3 to review"), the "Updated" age ("2m ago"), and chip badges are not consistently tabular — columns can jitter.
- **Hierarchy carried by colour, low-contrast greys.** Sub-text uses `#5E6779` and `var(--ink-faint)` on white; the buyer/supplier rail and "Updated" rely on colour for meaning. Several greys risk falling under 4.5:1. The brief: carry hierarchy via size+weight, not colour.
- **Tablet cliff (768–1023px).** A wide tablet shows phone cards, not the table — wasted horizontal space and an inconsistent experience versus 1024px+.
- **Click targets below 44px.** Select checkboxes are 13×13px; sort headers, chips (28px), Columns/pagination buttons (28–32px) are under the 44px minimum. Hover exists but pressed/focus-visible states are inconsistent (focus ring not explicitly styled on the inline-styled controls).
- **Row-level zebra/hover is ad-hoc.** Hover/selected/active/review-tint/failed-tint backgrounds are five hand-rolled hex values applied via JS `onMouseEnter/Leave` rather than CSS; gridlines use two different greys (`#E5E8EE` header vs `#F0F2F6` rows).
- **Bulk bar is a navy slab disconnected from the table.** It animates in by mounting (no transition), sits above the toolbar, and its actions are *text links* (no button affordance / size). The result line and "Send selected" share a cramped right cluster.
- **"Pipeline" + "Failed" semantics can mislead.** The collapsed "Failed" pill mixes redeliverable and non-redeliverable failures; only the checkbox gating hints at the difference — a user can't tell from the row why a "Failed" order isn't selectable without reading a tooltip.
- **Empty/error copy duplicated** across desktop and mobile branches (drift risk), and the error state replaces the whole screen (loses the header/toolbar context the loading state preserves).

### Redesign recommendations (for Claude Design)
1. **Unify the status system into ONE badge (highest impact).** Collapse the "Pipeline" column and the "Status" pill into a single token-driven status badge (one shape/size/padding, green=delivered/output, amber=needs-review, red=failed-blocking, blue=in-progress, neutral=new) with an icon + word. Keep a small optional 5-dot progress affordance *inside* a hover/popover, not as a permanent second column — reclaims ~300px and removes the duplicate encoding. Preserve the honest `ready` ("Normalized") vs `ready_to_deliver` ("Ready to send") split.
2. **Tokenise spacing, type, radius, and colour; drop inline literals.** Move to a strict 4/8 scale, one type scale (heading 600 / label 500 / body 400), one card radius + one border colour (`gray-200`) + one shadow tier, and the existing `--brand-*` / `--ink-*` tokens. This single sweep fixes most "unfinished" reads.
3. **Replace all glyphs/emoji with Lucide.** `RefreshCw` (sync), `ArrowUpFromLine`/`Upload`, `Search`, `Columns3`, `Inbox`/`CheckCircle2` (empty), `AlertTriangle` (error), `ChevronRight`, `ArrowRight` (rail), `ChevronsUpDown`/`ArrowUp`/`ArrowDown` (sort). Add `aria-label`s on icon-only buttons.
4. **Make the table responsive down to ~1024px and define a deliberate tablet layout.** Either let the table appear at `md` with horizontal scroll + a sticky first column, or design a richer two-line tablet card. Eliminate the 768–1023px phone-card cliff.
5. **Apply tabular figures to every number** (PO#, qty/line counts, value, age, chip badge counts, "Page X of Y") so columns stop jittering and money aligns.
6. **One row density + CSS-driven states.** Single 48–56px row height, one cell padding, gray-200 gridlines, a single hover and a single selected style in CSS (not JS), low-contrast review/failed left-edge accent bar instead of near-invisible 8%-alpha tints. Add `aria-sort` (already present) + a visible sortable affordance on hover.
7. **Promote one primary action, demote the rest.** Keep "Upload order" as the dominant green/blue ≥44px primary; make Sync, Columns, chips, pagination consistent outline/ghost secondaries with real button sizing (≥44px touch, visible hover + pressed + focus-visible ring).
8. **Rework the bulk bar as a sticky bottom action bar** that animates from the bottom, with real buttons ("Send selected" primary, "Clear" ghost), a clear count, and the result/feedback inline — separated from destructive ambiguity. Keep the per-PO failure detail.
9. **Clarify failed semantics in the row.** Split "Failed" into a redeliverable vs non-redeliverable visual cue (e.g. an inline "Retry" affordance only on `delivery_failed` rows, a muted "Needs fix" on parse/transform failures) so selectability is self-evident without a tooltip — and never imply "200 = accepted".
10. **Single source for empty/error/loading copy** shared between desktop and mobile; keep the header + toolbar mounted in the error state (as the loading state already does) and show a skeleton — never a bare spinner. Preserve the "Try a practice order" CTA only in the genuine-empty branch.
