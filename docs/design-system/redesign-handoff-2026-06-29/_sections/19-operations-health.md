## 19. Operations Health — `/operations/health`

- **File:** `src/app/(app)/operations/health/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (wide variant — 1480px max width)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle row)
  - `src/components/bridge/layout/Card.tsx` (empty/error surfaces)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile dead-letter cards)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (the per-row "Try sending again" action)
  - `src/components/bridge/UnifiedStatusBadge.tsx` (status pill in the dead-letter table)
  - `src/components/bridge/BridgeLoader.tsx` → `BridgePageLoader` (route-level `loading.tsx` only)
  - `src/hooks/useQueriesEnabled.ts` (auth/mock data-query gate)
  - API layer: `src/lib/api/operations.ts` (re-exported via `src/lib/api-client.ts`)
- **Capture URL (mock):** `/operations/health` (no ids/query needed — counts and the dead-letter list come from fixed mock functions; the only query param is the in-page `includeFailed` toggle which is client state, not URL state)

### What it is & why it exists
This is the operator's "is the pipeline OK?" dashboard — the failure-side mirror of the inbox. It sits at the tail of the `parse → normalize → validate → review → transform → deliver → learn` workflow and surfaces everything that fell out of the happy path: orders stuck mid-stage, transforms/deliveries that failed, deliveries that exhausted their retries (dead-lettered), supplier rejections, SLA breaches, and a count of open exceptions. The headline element is a worker/engine health banner (a dead Hangfire worker stalls the whole pipeline), and the actionable element is a dead-letter queue where the operator can manually re-attempt delivery of orders that ran out of automatic retries.

### Who uses it & the primary job
**Operator** (the person responsible for keeping POs flowing — often the same procurement coordinator wearing an ops hat). The single most important task: **find an order that couldn't be delivered and requeue it** ("Try sending again") after the underlying cause (supplier endpoint down, timeout, bad config) is believed fixed. Secondary job: confirm at a glance that order processing is running and nothing is silently stuck.

### Layout & structure (current)
Top-to-bottom inside a `PageShell variant="wide"` (1480px max, gutter 16→24→34px, vertical padding 20→28px):

1. **PageHeader** — `h1` "Operations health" (Bricolage Grotesque, 28→30px, weight 600) + muted 13px subtitle "Orders that are stuck, failed, or couldn't be delivered, at a glance."
2. **Worker status banner** — full-width pill-shaped bar (`marginBottom: 14`, `padding: 12px 16px`, `radius-md`). Green soft bg + green border + green dot + "Order processing is running" (weight 700) + "Last checked Ns ago" when `workerHealthy`; flips to danger-soft bg/border + red dot + "Order processing is paused" + a caveat about new uploads waiting when unhealthy.
3. **"Awaiting your review" banner** — a `Link` to `/inbox?status=pending_review`, blue-soft bg (`--brand-blue-soft`), `#D6E3F2` border, 10px radius, `px-4 py-3`, `mb-4`. Big 26px tabular number (`pendingReview ?? 0`) + label "Awaiting your review" + sub "Orders paused for a person to check — not a system problem." Explicitly INFORMATIONAL, styled blue (not red), excluded from `totalProblemOrders`.
4. **Tile grid OR all-clear banner** — if `totalProblemOrders === 0 && openExceptions === 0`, a single green-soft "✓ All clear" banner (`padding 16px 18px`, weight 600). Otherwise a CSS grid `repeat(auto-fill, minmax(168px, 1fr))`, `gap-3` (12px), of 8 count tiles. Each tile is a `Link` (white surface, `--border`, 10px radius, `px-4 py-3`, hover shadow): a 26px count number (faint when 0) + a colored dot + a 12px muted label.
5. **Threshold footnote** — `marginTop 10`, 11.5px faint: "Flagged as stuck after {N} min · auto-refreshes every 45s".
6. **Dead-letter section** (`marginTop 28`) — `h2` "Orders we couldn't deliver" (display font, 18px, weight 600) on the left; a `Include delivery-failed` checkbox label on the right (space-between, wraps on mobile). Optional blue-soft notice bar below the heading after a requeue. Then either an empty `Card` or the data: a desktop `<table>` (`hidden md:block`, white surface, `radius-md`, `overflowX:auto`) and a mobile `<div className="flex flex-col gap-3 md:hidden">` of `MobileListRow` cards.

Density/type/spacing observations: spacing uses a mix of Tailwind scale (`gap-3`, `mb-4`, `px-4 py-3`) and ad-hoc inline pixel values (`marginBottom: 14`, `marginTop: 10/28`, `padding: "12px 16px"`, font sizes `13.5`, `12.5`, `11.5`, `10.5`). Tile numbers are NOT given `tabular-nums` (only the pending-review number and the table Attempts cell are). Table header cells are 10.5px uppercase 700; body cells 13px.

### Data shown
**Entity 1 — `OpsHealth`** (mock `mockGetOpsHealth`, real `GET /api/ops/health`). Fields rendered:
- Banner: `workerHealthy` (bool), `secondsSinceWorkerHeartbeat` (number|null → "Ns/Nm/Nh ago"), `lastWorkerHeartbeatUtc` (string|null), `activeWorkers` (present but **not displayed**).
- Pending-review banner: `pendingReview` (optional → 0 if undefined).
- Tiles (8, each a numeric field + label + inbox filter href):
  - `parsingStuck` → "Stuck reading the file" → `/inbox`
  - `deliveringStuck` → "Stuck delivering" → `/inbox?status=delivering`
  - `transformFailed` → "Transform failed" → `/inbox?status=failed`
  - `deliveryFailed` → "Delivery failed" → `/inbox?status=failed`
  - `deliveryDeadLetter` → "Out of retries" → `/inbox?status=failed`
  - `rejectedBySupplier` → "Rejected by supplier" → `/inbox?status=failed`
  - `slaBreached` → "Overdue" → `/inbox`
  - `openExceptions` → "Open exceptions" → `/operations/exceptions`
  - (`failed` exists in the type and in the red-tone logic but is NOT a tile.)
- `totalProblemOrders` (drives all-clear), `stuckThresholdMinutes` (footnote).

**Entity 2 — `DeadLetterOrder[]`** (mock `mockGetDeadLetterOrders(includeFailed)`, real `GET /api/ops/dead-letter?includeFailed=true`). Table columns: **Order** (`poNumber` or `orderId.slice(0,8)`, links to `/inbox/{orderId}`), **Supplier** (`supplierName ?? "—"`), **Status** (`UnifiedStatusBadge`, with `rejected_by_supplier`→`rejected` normalization), **Attempts** (`deliveryAttempts`, right-aligned tabular), **Last error** (`lastError` + ` (lastResponseCode)`, red, truncated with title tooltip, `maxWidth 280`), **Last attempt** (`relativeTime(lastAttemptAt)`), **Action** ("Try sending again" button).

Mock fixtures: tile counts show `deliveryFailed:1, deliveryDeadLetter:1, openExceptions:2, pendingReview:3, totalProblemOrders:2, stuckThresholdMinutes:30, workerHealthy:true`. Dead-letter rows: `mock-dl-1` (PO-2026-0142, Acme Components, delivery_dead_letter, 3 attempts, "HTTP 503: supplier endpoint unavailable" 503); with `includeFailed` also `mock-dl-2` (PO-2026-0151, BoltWorks BV, delivery_failed, 1 attempt, "Connection timed out", null code).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Worker status banner | — (static, not a link) | Display only |
| "Awaiting your review" banner | `Link` | Navigates to `/inbox?status=pending_review` |
| Tile: "Stuck reading the file" | `Link` | `/inbox` |
| Tile: "Stuck delivering" | `Link` | `/inbox?status=delivering` |
| Tile: "Transform failed" | `Link` | `/inbox?status=failed` |
| Tile: "Delivery failed" | `Link` | `/inbox?status=failed` |
| Tile: "Out of retries" | `Link` | `/inbox?status=failed` |
| Tile: "Rejected by supplier" | `Link` | `/inbox?status=failed` |
| Tile: "Overdue" | `Link` | `/inbox` |
| Tile: "Open exceptions" | `Link` | `/operations/exceptions` |
| "Include delivery-failed" checkbox | `setIncludeFailed(bool)` → re-runs `deadLetterQ` with new `includeFailed` key | Widens/narrows dead-letter list (adds/removes `delivery_failed` rows) |
| Order # link (table & mobile) | `Link` | `/inbox/{orderId}` (order detail) |
| "Try sending again" button (table row, `variant="blue"` size sm) | `requeue.mutate(o)` → `POST /api/ops/orders/{id}/requeue-delivery` | Sets inline notice, invalidates `ops-health` + `ops-dead-letter` queries; order returns to "sending". Per-row disabled+"Sending…" while pending |
| "Try sending again" button (mobile card, size md, full-width) | same as above | same |
| Auto-refresh (no control) | `refetchInterval: 45_000` on both queries | Silent background refresh every 45s |

### What opens / what closes

**No modal/drawer/dialog/popover/dropdown/sheet overlays — the page navigates in place and gives feedback via an inline notice bar + a native title tooltip.** This is the page's defining UX gap for an ops surface: the most consequential action (requeue) fires immediately on click with **no confirmation dialog and no detail view**.

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Requeue success/error notice | Inline panel (blue-soft bar above the table) | Successful or failed "Try sending again" mutation (`onSuccess`/`onError` → `setNotice`) | Plain sentence, e.g. "Trying to send PO-2026-0142 again. It will move back to 'sending'." or the error message | **Never explicitly closes** — `notice` is set but never reset; only replaced by the next mutation or a full remount/navigation. No X/Esc. |
| Last-error tooltip | Native HTML `title` tooltip | Hovering the truncated "Last error" cell (`<span title={o.lastError}>`) | The full last-error string (the cell itself is `text-overflow: ellipsis` clipped) | Mouse-out (browser default) |
| Worker-state change | Inline (the banner itself swaps style/text) | `workerHealthy` toggling between fetches | Green "running"/red "paused" copy | Next fetch flips it back |

### States
- **Empty:**
  - *All clear* (no problem orders + no open exceptions): a single green-soft "✓ All clear — no orders in a problem state and no open exceptions." banner replaces the tile grid. The worker banner, pending-review banner, threshold footnote, and dead-letter section still render.
  - *Dead-letter empty:* a `Card edge="none"` with muted 13.5px text "No orders awaiting operator review." + a contextual hint to tick "Include delivery-failed" when that toggle is off. Good empty copy, but no illustration/next-action affordance beyond the hint.
- **Loading:** Two layers. (a) Route-level `loading.tsx` renders `BridgePageLoader label="Loading system health…"` — the canonical animated blue→green wire mark (reduced-motion safe). This only shows during navigation/Suspense. (b) **In-component**, while `!queryEnabled || healthQ.isLoading`, it renders the header + a **bare muted text line "Loading pipeline health…"** — NOT a skeleton. The dead-letter query has no separate loading skeleton; the table simply renders from `deadLetterQ.data ?? []` (empty card until data arrives).
- **Error:** If `healthQ.isError` or data is undefined, the whole body is replaced by a `Card` with red 14px text "Could not load operations health. The API may be unavailable — retry shortly." — **reason given but no retry button** (relies on the 45s auto-refetch / `retry: 1`). The dead-letter query has **no error state of its own** — if it fails it just falls back to an empty list (the page would silently show "No orders awaiting operator review" even on an API error).
- **Success/feedback:** Inline blue notice bar after a requeue (names the PO). Per-row button shows "Sending…" + disabled while that row's mutation is pending (gated on `requeue.variables?.orderId === o.orderId` so only the clicked row spins). Queries invalidate on success so counts/list refresh.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered at 1480px max. Tile grid auto-fills `minmax(168px, 1fr)` (≈7–8 tiles per row at 1440). Desktop dead-letter `<table>` visible (`md:block`), mobile cards hidden.
- **Tablet 768:** At the `md` breakpoint (768px) the table is still shown; tiles wrap to fewer columns. PageHeader/section header rows stay horizontal (`sm:flex-row`). This is the transition point — the table can get cramped just above 768 (7 columns including a 280px error cell) and the page relies on `overflowX:auto` to avoid breaking.
- **Mobile 390:** Below `md`, the `<table>` is hidden and the `flex flex-col gap-3 md:hidden` list of `MobileListRow` cards renders instead — each card stacks PO# + status badge on one row, then "supplier · N attempts · time" line, then a red error line, then a full-width "Try sending again" button (size md = 44px tap height). Buttons enforce 44px min height on mobile via `BUTTON_SIZE`. Tiles collapse toward 1–2 per row (168px min). PageHeader stacks (`flex-col`). The explicit comment in code notes the deliberate Tailwind-class (not inline-style) `display` to avoid the inline-style-beats-media-query double-render bug. No known hard cliff.

### Current UX issues
- **Requeue has no confirm and no detail (DESIGN BAR: confirm-before-destroy / one primary action).** "Try sending again" re-fires a real outbound delivery to a supplier instantly on a single click — a consequential, externally-visible action — with no confirmation step and no way to inspect the failure first. For an ops tool this is the biggest risk.
- **Notice bar never dismisses (DESIGN BAR: modals/transient surfaces need a clear close).** `notice` is set but never cleared; it lingers until the next action or navigation. No X, no auto-timeout, no Esc.
- **No real per-surface loading skeleton (DESIGN BAR #6).** The in-component loading is a bare "Loading pipeline health…" text line, contradicting the "skeleton, not bare spinner/text" rule. The dead-letter table has no loading state at all.
- **Dead-letter error is swallowed (DESIGN BAR #6 + "never show healthy when something is failing").** A failed `getDeadLetterOrders` falls back to `[]`, rendering the reassuring "No orders awaiting operator review" empty state — actively misleading on an API failure.
- **Two competing badge/pill systems.** Tiles use ad-hoc colored dots + numbers (their own `tone()` function with red/amber/neutral), the status column uses `UnifiedStatusBadge`, and the banners use yet another inline pill style. No single status-pill system across the page (DESIGN BAR #4).
- **Inconsistent tabular figures (DESIGN BAR #3).** Tile counts (26px) are not `tabular-nums`; only the pending-review number and the Attempts cell are. Counts and timestamps in the table will jitter/misalign.
- **Spacing/type drift (DESIGN BAR #1 & #2).** Mixed Tailwind scale and raw px (`marginBottom: 14`, `marginTop: 10/28`, `padding: "12px 16px"`), and many off-scale font sizes (13.5/12.5/11.5/10.5). Hierarchy partly carried by color (muted grays) rather than size+weight.
- **Hardcoded hex borders bypass tokens (DESIGN BAR #8).** Banner borders use literal `#BFE3BF`, `#F0B4B4`, `#D6E3F2`, `#D6E3F2` instead of semantic tokens; the empty/error Cards use `--shadow-card` while the banners/tiles use ad-hoc `hover:shadow-md` — no single elevation tier.
- **Focus/hit-area gaps (DESIGN BAR #9).** Tiles and the two banners are `Link`s with hover-shadow only — no visible focus-visible ring defined here, and tile hit areas aren't guaranteed ≥44px. The "Include delivery-failed" checkbox is a raw native `<input type="checkbox">` (unstyled, small target, no custom focus ring).
- **`activeWorkers` fetched but never shown**, and "Order processing is paused" relies on a heuristic heartbeat — risks the "never show healthy when something is failing" rule if heartbeat lags but jobs are truly dead.
- **HTTP code shown as raw `(503)` next to the error string** — fine for an operator but not lead-with-the-human-cause; no acceptance distinction (HTTP 200 ≠ supplier acceptance is correctly handled upstream via `rejected_by_supplier`, but the dead-letter table mixes transport failures and rejections without grouping).

### Redesign recommendations (for Claude Design)
1. **Add a requeue confirmation + detail step (highest impact).** Clicking "Try sending again" should open a small confirm dialog/drawer (navy header, scrim, Esc/X to close) showing the order, supplier, attempt count, full last error, and last response code, with one dominant green primary "Send again" and a ghost "Cancel". This satisfies confirm-before-destroy and gives the operator the failure context that's currently buried in a `title` tooltip.
2. **Promote the dead-letter queue to the page's primary action zone with ONE primary action style.** Make "Try sending again" the single green primary (≥44px, dominant); keep tiles/links as navigation only. Today everything is equally weighted blue/links.
3. **Make the notice a real toast** (dismissible, auto-timeout, success=green / error=red, aria-live) anchored consistently, replacing the persistent inline blue bar.
4. **Unify the status system (DESIGN BAR #4).** One pill shape/size/padding with icon+word for: worker state, tile severity, and dead-letter status. Reuse `UnifiedStatusBadge` semantics (green/amber/red/neutral) for tiles instead of bespoke dot+number tones.
5. **Add proper loading + error states to every surface (DESIGN BAR #6).** Replace "Loading pipeline health…" with tile + table skeletons; give the dead-letter query its own error card with a Retry button instead of silently rendering empty; add a Retry button to the health error card.
6. **Enforce one spacing rhythm and one type scale (DESIGN BAR #1 & #2).** Convert all raw px margins/paddings to the 4/8 scale; collapse 13.5/12.5/11.5/10.5 into the canonical sizes; carry hierarchy via size+weight, keep navy `--ink`/violet for emphasis, drop gray-on-gray.
7. **Tabular figures everywhere (DESIGN BAR #3).** Apply `font-variant-numeric: tabular-nums` to all tile counts, attempts, and timestamps so numbers stop jittering across the 45s auto-refresh.
8. **Tighten the table to one density (DESIGN BAR #5).** Single row height, sticky header, low-contrast `gray-200` gridlines, sortable affordance with `aria-sort` on Attempts/Last attempt, and a dedicated severity/lead-cause column so transport failures vs supplier rejections are visually grouped (keep red for blocking).
9. **Standardize elevation, radius, borders (DESIGN BAR #8).** One card radius/border/shadow tier; replace hardcoded hex borders with the green/red/blue soft+strong token pairs; one popover/dialog shadow tier for the new confirm dialog.
10. **Accessibility polish (DESIGN BAR #9 & forms).** Replace the native checkbox with a labeled, ≥44px, focus-ringed control with helper text; add focus-visible rings to tiles and banners; add `aria-label`s; keep reduced-motion behavior for the pulsing worker dot and `UnifiedStatusBadge` pulse.
11. **Show `activeWorkers` and a precise last-heartbeat timestamp in the worker banner**, and never render "running" green when the heartbeat exceeds the stuck threshold — make the banner the literal source of truth for engine health (don't show healthy when something is failing).
12. **Mobile (DESIGN BAR #10).** Keep the stacked card list (already good); ensure tiles render 2-up, the worker/pending banners stay full-width and legible, and the requeue confirm dialog becomes a bottom sheet on mobile.
