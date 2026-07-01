## 09. Drafts — `/drafts`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/drafts/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (page canvas + centered wide container)
  - `src/components/bridge/layout/PageHeader.tsx` (title row + actions slot)
  - `src/components/bridge/layout/Card.tsx` (empty-state surface wrapper)
  - `src/components/bridge/EmptyState.tsx` (bare Mark + Bricolage title + sub + secondary action)
  - `src/components/bridge/MarkSystem.tsx` / `src/components/bridge/DSPrimitives.tsx` (`MarkSystem`, `ProcuLinkMark`, `Button`)
  - `src/app/globals.css` — `.pill` / `.pill-review` / `.pill-failed` design classes used by the draft rows
- **Capture URL (mock):** `/drafts` (mock mode renders 2 demo rows `d1`, `d2`; real/non-mock mode renders the empty state)

### What it is & why it exists
Drafts is the holding pen for orders a coordinator started but did not finish — an order they saved while still resolving it (mapping SKUs, clearing exceptions, or picking a supplier). It sits between **review** and **transform** in the parse → normalize → validate → review → transform → deliver → learn workflow: an order parked mid-review so it does not get lost. A procurement coordinator opens it to resume an order they couldn't complete in one sitting and jump back into its inbox/review screen.

> **Important reality check for the redesign:** there is **no draft-persistence backend yet**. The page is hardwired to a 2-row `DEMO_DRAFTS` constant that only shows when `NEXT_PUBLIC_USE_MOCK=true`. Real users (mock off) always see the empty state — `const DRAFTS = isApiMockMode ? DEMO_DRAFTS : []`. The rows are demo scaffolding, not live data, and the row click routes to `/inbox/${d.id}` (e.g. `/inbox/d1`), which will 404 against the real API. Treat the list layout as a design target to be wired later, not a working feature.

### Who uses it & the primary job
**Persona:** procurement coordinator (the buyer-side operator who uploads POs and resolves them). **Primary job:** *resume an in-progress order* — find the saved draft and reopen it to finish mapping / clearing exceptions / choosing a supplier before sending. Secondary job: start a new order (`New` button → `/upload`).

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width `--container-wide` = 1480px; gutter ramps 16 → 24 → 34px, vertical padding 20 → 28px; canvas background `--bg`):

1. **PageHeader** — title **“Drafts”** (Bricolage Grotesque, 600, 28→30px, `--ink`) with subtitle **“Orders you've saved to finish later”** (13px, `--ink-muted`). Right-aligned **actions slot** holds one button: **`New`** (blue/green `variant="blue"` Button, size `md`, with an inline 24-viewBox plus-icon SVG). On mobile the header stacks (`flex-col`) and the action wraps below.
2. **Body — conditional on `DRAFTS.length`:**
   - **Empty (real users / mock off):** a single `Card` with `flex items-center justify-center min-h-[360px]` wrapping `EmptyState` — bare ProcuLink Mark (52px, hover opacity 0.7→1, 400ms), Bricolage title **“Drafts live here”**, muted sub explaining what a draft is, and a navy secondary action button **“Go to Inbox”** (→ `/inbox`).
   - **Populated (mock):** a vertical stack (`flex flex-col gap-2.5` = 10px gaps) of **draft rows**. Each row is a hand-rolled clickable `div` (NOT the shared `MobileListRow` — the code notes `MobileListRow` doesn't accept a `style` prop, so it re-implements role/tabIndex/keyboard/press behaviour). Row styling: `--surface` background, `1px --border`, **3px amber left border (`--amber`)**, `--radius-md` (8px), padding `14px 16px`, `--shadow-card`, `min-height: --tap-min` (44px), hover shadow swap to `0 4px 14px rgba(11,26,47,0.08)`.
     - Row internal layout: `flex-col gap-3` on mobile → `sm:flex-row sm:items-center sm:gap-4` on desktop.
       - **Left (identity, `flex-1 min-w-0`):** PO number (12px, mono, 600, `--ink`) then a line `buyer → supplier` — buyer in `--ink` 500, the `→` arrow in `--ink-faint`, supplier in `--brand-green-deep` 500, both names `truncate`.
       - **Right (meta cluster, `flex items-center gap-2 flex-wrap`):** a **stage pill** (`.pill .pill-review`, amber, with dot), an optional **exceptions pill** (`.pill .pill-failed`, red, with dot — only when `issues > 0`), and a saved-at timestamp (11px, `--ink-faint`, `min-width 56px`, right-aligned, rendered as `{savedAt} ago`).

**Density/type/spacing observations:** mostly aligned to the system, but the rows are styled almost entirely via inline `style={{}}` objects with hardcoded pixel values (`14px 16px`, `boxShadow`, `minHeight`) rather than the canonical `Card` / `MobileListRow` primitives. The two pill systems differ from the rest of the app's `UnifiedStatusBadge` (these use raw `.pill` CSS classes). Font sizes mix integers and decimals (12px, 11px, 12.5px in Button).

### Data shown
Single entity: a **Draft** (in-progress purchase order). Source: the local `DEMO_DRAFTS` constant in the page file — **not an API** (no draft endpoint exists). Fields per row:

| Field | Mock value example | Render |
|---|---|---|
| `id` | `d1`, `d2` | row key; used in `/inbox/${id}` nav target |
| `po` | `PO-2026-008422`, `AR-2026-1110` | mono PO number, top of identity block |
| `buyer` | `Example Buyer Co.` | left of `→`, `--ink` 500 |
| `supplier` | `Example Supplier Co.` | right of `→`, `--brand-green-deep` 500 |
| `savedAt` | `3m`, `2h` | `{savedAt} ago`, faint timestamp |
| `stage` | `Needs review`, `Ready` | amber `.pill-review` stage pill (note: always `pill-review` styling even when stage text is “Ready”) |
| `issues` | `2`, `0` | red `.pill-failed` exceptions pill, only if `> 0`; pluralized “exception/exceptions” |

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| `New` button (header) | `onClick` | `router.push("/upload")` — start a new order |
| Draft row (whole row, `role="button"`) | `onClick` | `router.push("/inbox/${d.id}")` — opens the draft's inbox/review (mock ids 404 on real API) |
| Draft row | `onKeyDown` Enter / Space | same nav as click (`preventDefault` + push) |
| Draft row | hover (`onMouseEnter`/`Leave`) | swaps `boxShadow` to a deeper card shadow and back |
| `Go to Inbox` button (empty state only) | `onClick` | `router.push("/inbox")` |
| ProcuLink Mark (empty state) | hover | opacity 0.7 → 1 (decorative only, not a control) |

There are **no per-row action menus, no delete/discard control, no filters, no search, no sort, no tabs, and no bulk-select** anywhere on this page.

### What opens / what closes
**No overlays — navigates in place.** This page opens **zero** modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts. Every interactive control is a direct `router.push` navigation to another route. The FOCUS HINT's “row actions / delete confirm” surfaces **do not exist** — rows are single click-through nav targets, and because there is no draft-persistence backend there is no delete affordance and therefore no delete-confirm dialog to capture. (This is a gap to design, not an existing surface.)

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| — | — | — | None — all actions navigate to a new route | — |

### States
- **Empty:** Fully handled and well-designed. A `Card` (min-height 360px, centered) holds `EmptyState`: bare 52px Mark, **“Drafts live here”**, an explanatory sub (“Save an order while you are still resolving it — mapping SKUs, clearing exceptions, picking a supplier — and it waits here until you are ready to send it.”), and a navy **“Go to Inbox”** button. This is what every real (non-mock) user sees.
- **Loading:** **Not handled — there is no `loading.tsx` in the drafts folder** and no skeleton/spinner in the page. Because the data is a synchronous local constant there is no async load today, so nothing renders a loading state. When wired to a real endpoint this will be a bare gap.
- **Error:** **Not handled.** No error boundary, no fetch, no retry. With no API call there is nothing to fail today, but the page has no error surface for when drafts are wired up.
- **Success/feedback:** None beyond row hover shadow + native focus ring. No toasts, no inline confirmations, no “saved” feedback (consistent with there being no save/delete actions on this screen).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** content capped at 1480px and centered; gutter 34px. Header is title-left / `New`-right. Draft rows are single-line: identity flexes left, meta cluster (pills + timestamp) pinned right.
- **Tablet 768:** still uses the `sm:` desktop row layout (the row's `sm:flex-row` kicks in at 640px), so rows remain horizontal; gutter 24px.
- **Mobile 390:** header stacks (title over the `New` button which wraps below). Each draft row collapses to `flex-col gap-3`: identity block on top, the meta cluster (pills + timestamp) wraps underneath, left-aligned and `flex-wrap`. Button is forced to 44px tall (`h-[44px]`) for tap targets. No drag/mapper canvas here, so nothing breaks on small screens.
- **Cliffs:** none functionally — but the empty-state `Card`'s fixed `min-h-[360px]` is tall on a 390px viewport, and the row uses inline pixel padding that won't reflow as gracefully as the canonical primitives. The stage pill text “Ready” renders with amber `pill-review` styling regardless of value (a content/colour mismatch, not a breakpoint issue).

### Current UX issues
- **The whole feature is fake data.** Real users only ever see the empty state; the demo rows route to `/inbox/d1` etc. which 404 against the live API. The list UI implies a capability that doesn't exist (violates “offer ⇔ works”). Either ship draft persistence or keep the honest empty state and don't show a populated design as if it works.
- **No row actions at all, despite drafts being the place you'd most want them.** There is no resume/open button, no **discard/delete draft**, no rename, no overflow menu. The FOCUS HINT expected a delete confirm; none exists. A drafts list with no way to clear a stale draft will rot.
- **Status pill system is off-spec.** Rows use raw `.pill .pill-review` / `.pill-failed` CSS classes, not the app-wide `UnifiedStatusBadge`. Worse, the **“Ready” stage is rendered in amber “review” styling** — colour does not match meaning (DESIGN BAR #4: one status system, colour must match semantics). “Ready” should be green.
- **Not using the canonical row primitive.** The row re-implements `MobileListRow` by hand with a large inline `style` object (hardcoded `14px 16px`, custom box-shadow, manual hover handlers). This drifts from the one-list-density rule (DESIGN BAR #5) and duplicates accessibility logic that the shared component already owns.
- **Numbers are not consistently tabular.** PO numbers are mono (good) but the `savedAt` timestamp and `issues` count are not guaranteed tabular figures; the timestamp's `min-width:56px` hack is a symptom of non-tabular jitter (DESIGN BAR #3).
- **No loading or error states** (DESIGN BAR #6) — fine while data is a constant, but the moment this is wired to an endpoint there is nothing. Empty state is the only handled state.
- **Stage as free text** (`"Needs review"`, `"Ready"`) is fragile — not enum-backed, so the pill can't reliably map status → colour/icon.
- **Hover feedback is shadow-only** via JS mouse handlers; no CSS `:hover`, no pressed state beyond `active:bg-surface-2`. JS-driven shadow swaps don't respect reduced-motion intent as cleanly as a CSS transition would.
- **Single primary action is ambiguous.** `New` (header) and `Go to Inbox` (empty state) both compete as “the next thing to do”; on a populated list the dominant action should be resuming a draft, but rows have no explicit primary affordance.

### Redesign recommendations (for Claude Design)
Ranked most-impactful first. Keep navy `#0B1A2F` + violet brand; green=success, amber=warning, red=blocking.

1. **Decide the feature's truth first.** If draft persistence isn't shipping, make the empty state the *only* state and remove the demo list, or clearly label it “preview.” If it is shipping, wire to a real `GET /api/drafts` (or equivalent) and design real loading/error/empty around it. Don't ship a list that 404s.
2. **Add per-row actions with a separated, confirmed destroy.** Give each row a clear primary **“Resume”** (green, the dominant action) and an overflow `⋯` menu (dropdown) with **Rename** and a visually separated **Discard draft** that opens a small confirm dialog (“Discard PO-2026-008422? This can't be undone.” — Cancel / red Discard). This is the missing “delete confirm” surface and the page's most important add. (DESIGN BAR: destructive separated + confirm-before-destroy.)
3. **Replace the hand-rolled row with the canonical `MobileListRow` + `UnifiedStatusBadge`.** One row height, one padding, one hover/focus treatment, gray-200 gridlines, sticky header if it becomes a table. Drop the inline `style` object. (DESIGN BAR #5, #8.)
4. **Fix the status semantics: “Ready” must be green, “Needs review” amber, blocked/exceptions red** — one badge shape/size/padding, always with an icon or word, never colour alone. Make stage an enum, not free text. (DESIGN BAR #4.)
5. **Tabular figures everywhere** — PO#, exception count, and the saved-at timestamp use `font-variant-numeric: tabular-nums` so columns don't jitter; drop the `min-width:56px` band-aid. (DESIGN BAR #3.)
6. **Lead with the human field name.** Keep `buyer → supplier` (good — it's the bridge metaphor and human-readable), and surface the *real* exception summary (“2 unmapped SKUs”) rather than a bare count so the coordinator knows *why* it's parked.
7. **Add real loading (skeleton rows, not a spinner) and error (reason + Retry)** states once wired to an endpoint; reuse the same skeleton the inbox uses. Add a `loading.tsx`. (DESIGN BAR #6.)
8. **One dominant primary per state:** on the empty state keep a single green primary (e.g. “Upload an order” → `/upload`) and demote “Go to Inbox” to ghost; on the populated list let the per-row **Resume** be the dominant affordance and keep header **New** as a secondary/outline. (DESIGN BAR #7.)
9. **Add filter/sort affordances** (by stage, by saved-at, by supplier) with `aria-sort` once there's enough data to warrant it — drafts naturally accumulate. (DESIGN BAR #5.)
10. **Accessibility polish:** ensure the row's `role="button"` keeps the focus-visible ring, give the `New` plus-icon an `aria-label`-backed accessible name (currently text-labelled “New”, which is fine, but the SVG should stay `aria-hidden`), and keep every control ≥44px. (DESIGN BAR #9.)
