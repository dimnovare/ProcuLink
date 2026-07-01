## 18. Exceptions — `/operations/exceptions`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/exceptions/page.tsx`
- **Key components:**
  - `src/components/bridge/ExceptionDetail.tsx` — the expanded per-row what/why/how/status panel (Group V6)
  - `src/components/bridge/layout/PageShell.tsx` — `variant="wide"` page wrapper (1480px)
  - `src/components/bridge/layout/PageHeader.tsx` — title + subtitle + actions row
  - `src/components/bridge/layout/MobileListRow.tsx` — mobile card wrapper
  - `src/components/bridge/DSPrimitives.tsx` — `Button` (variants `blue`/`secondary`)
  - `src/components/bridge/UnifiedStatusBadge.tsx` — canonical order-status pill, used inside `ExceptionDetail`
  - `ExceptionCard` + `SeverityBadge` + `Section` — local components defined inside the two files above
- **Capture URL (mock):** `/operations/exceptions` (mock returns 5 exceptions across states; the default "All" tab shows all 5)

### What it is & why it exists
This is the all-orders exception queue — a single list of every order that is blocked or needs a human decision before it can move forward in the `parse → normalize → validate → transform → deliver → learn` pipeline. Each row is a discrete fault (unresolved supplier code, missing delivery config, supplier HTTP rejection, parse-time assumption, duplicate PO) tied to an owning order. A coordinator opens this page to triage the day's blockers: see what is wrong in plain English, understand why, jump to the owning order to fix the cause, or dismiss noise. The honest model baked into the code is that the backend Reconcile pass is the source of truth — an order-linked exception cannot be manually "resolved" from this list (it would re-open on the next pass), so for those the real action is "Open order".

### Who uses it & the primary job
**Operator / procurement coordinator.** Primary job: triage blocked orders and get to the fix fast — expand a row to read what/why/how-to-fix, then click **Open order** to go fix the cause in the order-detail screen. Secondary jobs: **Ignore** genuine noise, filter by lifecycle state (Open/Resolved/Ignored), and refresh.

### Layout & structure (current)
Top-to-bottom, inside `PageShell variant="wide"` (max-width 1480px, gutter ramp 16→24→34px, vertical 20→28px):

1. **PageHeader** — `h1` "Exceptions" (Bricolage Grotesque, 28/30px, weight 600), subtitle "Every order that needs a human decision before it can be sent.  {N} shown", and a right-aligned **Sync** button (`variant="secondary" size="sm"`, label toggles to "↻ Syncing…" while fetching).
2. **Instructional note** — a single 12px faint line ("Expand a row to see what's wrong, why, how to fix it, and its real delivery status…") pulled up with `-mt-3`.
3. **State filter tabs** — a wrapping flex row of 4 pill-buttons (All / Open / Resolved / Ignored), each 28px tall, 6px radius. Active tab = solid `--ink` (navy) background with white text; inactive = white surface, `--border`, muted text.
4. **Content card** — a raw `div` (NOT the Card primitive — comment explains the Card's 18px padding breaks the flush-edge table) replicating card chrome: `--surface` bg, `1px --border`, `--radius-md` (8px), `--shadow-card`. It is `flex-1 min-h-0 overflow-auto`. Inside it renders ONE of: loading skeleton / error / empty / list.
   - **Desktop table** (`md:` and up): `<table>` `minWidth: 980`, `tableLayout: fixed`, font 12.5px. `colgroup` widths: expand `40`, Severity `96`, Stage `110`, Code `180`, Message `auto`, Raised `96`, actions `176`. Sticky `<thead>` (`position: sticky; top:0; z-index:4`) with 7 columns: "" (chevron), Severity, Stage, Code, Message, Raised, "" (actions, right-aligned). Header cells: 10.5px, weight 700, uppercase, 0.06em tracking, `--ink-faint`.
   - **Mobile cards** (`md:hidden`): vertical stack of `ExceptionCard` (gap 8px, padding 12px).
5. **Footer** — sticky-bottom flex row: "{N} exception(s)" count (11px faint) + client-side pager ("← Prev" / "Page X of Y" mono / "Next →") shown only when `totalPages > 1`. PAGE_SIZE = 25, paginated client-side over the full loaded array.

Density/type/spacing observations: row vertical padding is inconsistent (`9px` on chevron/action cells vs `11px` on data cells in the SAME row); the page uses a mix of pixel values (28px tabs, 24px chevron button, 32px detail buttons) rather than a strict 4/8 scale; numbers (Raised relative time, page count) are mostly NOT tabular except the pager which uses `font-mono`.

### Data shown
**Entity:** `OrderException` (type in `src/types/procurement.ts`). Fields displayed per row:

| Column | Field | Notes |
|---|---|---|
| (expand) | — | chevron toggle |
| Severity | `exc.severity` | `info` / `warning` / `error` / `critical` → `SeverityBadge` |
| Stage | `exc.stage` | parse / validate / transform / deliver (or "—") |
| Code | `exc.code` | machine code in `font-mono` (e.g. `UNRESOLVED_SUPPLIER_CODE`) |
| Message | `exc.message` | human sentence; truncated with ellipsis, click toggles expand |
| Raised | `exc.createdAt` | `relativeTime()` → "12m ago" / "3h ago" / "1d ago" |
| (actions) | `exc.state` | open → Resolve/Open order + Ignore; else shows the state label |

Expanded `ExceptionDetail` additionally lazy-fetches the owning order (`apiClient.getOrderById(exc.orderId)`) and shows: `order.status` (via `UnifiedStatusBadge`), an honest delivery-status line ("Sent — acceptance unconfirmed" for delivered/sent), `order.errorMessage`, and links built from `order.supplierId` (→ `/library/suppliers/{id}`).

**Data source:** `getExceptions(state)` → `GET /api/exceptions?state=open|resolved|ignored` (returns the WHOLE list, no server paging). Mutations: `resolveException(id)` → `PATCH /api/exceptions/{id}/resolve`; `ignoreException(id)` → `PATCH /api/exceptions/{id}/ignore`. Mock: `mockGetExceptions` / `mockResolveException` / `mockIgnoreException` in `src/lib/api/operations.ts`. Mock dataset = 5 exceptions (`exc-001`…`exc-005`) on orders `ord-001`/`ord-002`/`ord-003`, spanning info/warning/error/critical and open/resolved/ignored.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Sync** button (header) | `refetch()` | Re-runs the exceptions query; label → "↻ Syncing…" while `isFetching` |
| **All / Open / Resolved / Ignored** tabs | `selectTab(i)` | Sets `activeState`, resets page to 1, collapses any expanded row; re-keys query (`["exceptions", state]`) |
| **Chevron button** (per row, desktop) | `toggleExpanded(id)` | Expands/collapses the in-row `ExceptionDetail`; `aria-expanded`, rotates 90° |
| **Message text button** (per row) | `toggleExpanded(id)` | Same as chevron — clicking the message opens detail (hover underline) |
| **Resolve** button (`variant="blue"`) | `resolveMut.mutate(id)` | Only rendered when `canResolveFromList(exc)` is true (i.e. `!exc.orderId`); invalidates `["exceptions"]` on success. For order-linked rows this is replaced by ↓ |
| **Open order** button (`variant="blue"`) | `router.push('/inbox/{orderId}')` | Navigates to the owning order. Disabled when no `orderId` (wrapped in a `<span title=…>` carrying the why-tooltip) |
| **Ignore** button (`variant="secondary"`) | `ignoreMut.mutate(id)` | Sets state to ignored; invalidates `["exceptions"]` |
| **← Prev / Next →** (footer) | `setPage(±1)` | Client-side page over loaded array; disabled at ends |
| **Open order to fix →** (detail, step 3) | `next/link` to `/inbox/{orderId}` | Same destination as the row "Open order" |
| **Check conformance** (detail, step 3) | `next/link` to `/inbox/{orderId}?tab=conformance` | Order detail, conformance tab |
| **supplier's Validation rules tab** link (detail, step 2) | `next/link` to `/library/suppliers/{supplierId}` | Only shown when the fetched order has a `supplierId` |
| **Mobile card chevron / message** | `onToggle()` | Expand/collapse card detail |
| **Mobile Resolve / Open order / Ignore** | same mutations / `onOpen` | Mirror the desktop row actions |

### What opens / what closes

**No modals, drawers, sheets, or dialogs. The only transient surfaces are inline expand/collapse panels and native browser title tooltips. The page navigates in place (router.push / next/link).**

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Row detail (desktop) | Inline expandable table row | Chevron button OR the Message text button (`toggleExpanded`) | `ExceptionDetail`: step 1 "What's wrong" (message + code · stage), step 2 "Why" (`stageReason` + optional supplier-rules link), step 3 "How to fix" (Open order / Check conformance), step 4 "Status" (UnifiedStatusBadge + honest delivery line + errorMessage) | Clicking the chevron/message again; switching tabs (`selectTab` sets `expandedId=null`). Only one row open at a time (`expandedId` is a single id). No Esc handler |
| Card detail (mobile) | Inline expandable card region | Card chevron/message (`onToggle`) | Same `ExceptionDetail` | Tap chevron/message again; tab switch |
| "Open order" disabled tooltip | Native `title=` on wrapping `<span>` | Hovering the disabled Open-order button when `!exc.orderId` | `NO_ORDER_TITLE`: "This exception isn't tied to an order, so there's no order to open." | Mouse leave (browser-native) |
| "Open order" enabled tooltip | Native `title=` on the button | Hovering Open-order when an order exists | "Open the order to fix the cause. This exception clears itself on the next pipeline pass once the cause is gone." | Mouse leave |
| "Resolve" tooltip | Native `title=` on the button | Hovering Resolve | "Mark this exception resolved. Only available when it isn't tied to an order — order-linked exceptions clear automatically once you fix the cause." | Mouse leave |

There are **no confirmation dialogs** on Ignore or Resolve — they fire immediately and there is no undo. (`mark_chapter`-worthy note for the redesign: Ignore is the closest thing to a destructive action and has no confirm.)

### States
- **Empty:** Handled well. When `exceptions.length === 0`: a green ✓ (32px), "No exceptions — all clear" (20px, weight 600, Bricolage), and helper copy "Nothing is blocked right now. Exceptions appear here when an order needs a decision before it can be sent to a supplier." No next-action button (acceptable — the desirable state is empty).
- **Loading:** Handled — skeleton, not a bare spinner. 6 placeholder rows (`divide-y`), each with a 16px chip + flex bar + 20px bar, `animate-pulse`. `showLoading` also covers the query-not-enabled case so the page never flashes an error before Clerk is ready. The expanded detail's status block has its own pulse skeleton while the order lazy-loads.
- **Error:** Handled — a centered red ⚠ in a 46px `--danger-soft` circle, "Couldn't load exceptions", reassurance copy ("Your orders are safe — this is usually transient."), and a **↻ Retry** button (`refetch`). `retry: 1` on the query.
- **Success/feedback:** Minimal. Mutations invalidate `["exceptions"]` so the list re-fetches and the row drops out of the Open tab, but there is **no toast / inline confirmation**. While a mutation is pending, that row's action buttons are `disabled` (`pendingId === exc.id`) — the only feedback is the disabled (greyed) state; there is no spinner on the button itself.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Full desktop `<table>` inside the 1480px `PageShell`. Sticky header, fixed columns, Message column flexes. Tabs and footer in one row each.
- **Tablet 768:** Still the desktop table (`md:` breakpoint = 768px). Table has `minWidth: 980` with `overflow-x-auto`, so at exactly 768 the table **horizontally scrolls** — a likely cliff (header/actions can be off-screen until you scroll right).
- **Mobile 390:** `md:hidden` swaps to stacked `ExceptionCard`s. Each card: severity badge + stage + relative time on top row, chevron+message, mono code, expand detail, then a row of action buttons (which are `h-[44px]` on mobile via `Button` size logic). Tabs wrap (`flex-wrap`). Footer pager wraps. This is genuinely stacked, not shrunk — good.

### Current UX issues
- **No status-badge unification across the two badge systems.** Severity uses the page-local `SeverityBadge` (rounded-4px rectangle, 10.5px, custom `#F4D5D5`/`#8E1F1F` for critical that the code itself admits has "no exact token match"), while the expanded detail uses the pill-shaped `UnifiedStatusBadge`. Two shapes, two colour sources, two radii for state on one screen — violates the single status-badge system rule.
- **Spacing drift within a single row.** Action/chevron cells use `9px` vertical padding while data cells use `11px`; chevron button is 24px, tab buttons 28px, detail CTAs 32px — no strict 4/8 rhythm.
- **Numbers are not consistently tabular.** "Raised" relative times, the "{N} shown" header count, and "{N} exceptions" footer count use the body font; only the pager is `font-mono`. Counts/timestamps can jitter and don't align.
- **No tabular/sortable affordance.** The table header has no sort controls and no `aria-sort`; rows are fixed-sorted newest-first server-side with no way to sort by severity or stage — the most useful triage axis (severity) isn't sortable.
- **Filter tabs are dead-styled as toggles, not as the canonical Pill/segmented control.** They're bespoke buttons with a navy active fill that doesn't match the app's other tab/segment patterns; counts per state are not shown on the tabs (you can't see "Open 3 / Resolved 1" without switching).
- **Destructive-ish action has no confirm and no undo.** **Ignore** mutates immediately with no toast, no "Undo", and no confirmation — a misclick silently hides a real blocker.
- **No feedback on success.** After Resolve/Ignore the only signal is the row disappearing from the Open filter; there's no toast and no in-place "Ignored ✓". Pending state is just `disabled`, not a visible spinner.
- **Tooltips rely on native `title=`.** The why-explanations for disabled/blue buttons are browser tooltips (slow, untouchable on mobile, inconsistent styling) rather than a styled popover.
- **Tablet table scroll cliff** (`minWidth: 980` from 768px) can hide the right-edge action column behind horizontal scroll.
- **Two primary-coloured buttons compete.** Both "Resolve" and "Open order" use `variant="blue"` (which is actually brand-green per DSPrimitives) AND the detail's "Open order to fix →" uses `--brand-blue`. Green vs blue for the same conceptual action across the row and the panel reads inconsistent; no single dominant primary per screen.
- **"Code" column shows raw machine codes** (`UNRESOLVED_SUPPLIER_CODE`, `SUPPLIER_HTTP_422`) at column width 180 — useful to experts but leads with jargon; the human message is the secondary, ellipsis-truncated column.

### Redesign recommendations (for Claude Design)
1. **Unify the status/severity badge.** Make `SeverityBadge` adopt the one canonical badge shape/size/padding (pill, icon-or-word, never colour alone) shared with `UnifiedStatusBadge`; map critical→danger token (drop the orphan `#F4D5D5`/`#8E1F1F`), error→danger, warning→amber, info→info/blue, with a leading Lucide icon (AlertTriangle / AlertCircle / Info). Keep navy/violet brand intact; green=resolved, red=blocking, amber=warning.
2. **Add per-tab counts and make tabs the canonical segmented control.** Show "Open 3 · Resolved 1 · Ignored 1" inline so triage scope is visible without clicking. Active state via the design-system segment style, not a bespoke navy fill.
3. **Make the table the canonical dense table.** One row height, one cell padding (kill the 9 vs 11px split → single 8/12px), low-contrast `gray-200` gridlines, real `aria-sort` + sortable Severity and Raised columns (severity is the key triage axis). Tabular-figures (`font-variant-numeric: tabular-nums`) on Raised, all counts, and the pager.
4. **Lead each row with the human message; demote the code.** Put the plain-English `message` first (it's the operator's signal) with the machine `code` as a small mono sub-label or an on-demand "show code" affordance — consistent with the "lead with the human field name" rule.
5. **One dominant primary action.** Pick a single primary (green ≥44px) for the row's main action — for order-linked rows that's "Open order"; demote "Ignore" to outline/ghost and visually separate it as the dismissive action. Resolve a single colour story across row and detail (stop mixing brand-green `variant="blue"` with `--brand-blue` link buttons).
6. **Confirm + undo for Ignore.** Add a lightweight confirm (or, better, an optimistic action with a 5s "Ignored — Undo" toast). Add success toasts for Resolve/Ignore and a button-level spinner during the pending mutation instead of bare `disabled`.
7. **Replace native `title=` tooltips with styled popovers** (shadcn Tooltip) for the disabled/blue button explanations, with focus-visible + touch support; keep the honest copy.
8. **Fix the tablet cliff.** Below ~1024px, switch to the stacked card layout (or a 2-column condensed table) so the action column is never hidden behind horizontal scroll; ensure the primary action stays visible.
9. **Elevate the expanded detail consistency.** The 4-step (What/Why/How/Status) panel is excellent and honest (delivered ≠ accepted) — keep it, but give it the canonical card inset, one radius, one border colour, and tabular figures on any numeric status; make the step badges and CTAs use the unified button/badge tokens.
10. **Keep the empty + error states; add a next-action to error.** Empty is good. On the error state, keep Retry but also surface a quiet link to ops health (`/operations/health`) so a persistent failure points somewhere actionable.
