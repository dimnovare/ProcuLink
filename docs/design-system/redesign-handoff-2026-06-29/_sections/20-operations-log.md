## 20. Delivery Log (Audit Trail) — `/operations/log`

- **File:** `src/app/(app)/operations/log/page.tsx` (one-liner; renders `<CrossingsLog />`)
- **Key components:**
  - `src/components/bridge/CrossingsLog.tsx` (the entire screen — table, filters, search, export, expandable rows)
  - `src/components/bridge/layout/PageShell.tsx` (wide variant, max-width 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle + actions slot)
  - `src/components/bridge/EmptyState.tsx` (filtered-empty state; pulls `MarkSystem`)
  - `src/components/bridge/MarkSystem.tsx` (brand mark in the empty state)
- **Capture URL (mock):** `/operations/log` (no ids/query — single static route; mock mode renders the in-component `MOCK_LOG` fixture, 8 entries)

### What it is & why it exists
This is the append-only **audit trail** for the whole bridge: every parse, edit, validation, and delivery the system records appears here as a timestamped, actor-attributed event. It sits at the end of the `parse → normalize → validate → review → transform → deliver → learn` loop as the system of record — the place a procurement coordinator goes to answer "did this PO actually reach the supplier, when, and who/what touched it?" It is read-mostly: the only outbound actions are export (CSV) and navigating to the underlying order to act there (e.g. resend a failed delivery). It deliberately does **not** retry deliveries in place (the code comment notes the old "Retry delivery" control was a dead control; it now honestly says "Open to resend" and routes to the order).

### Who uses it & the primary job
**Operator / procurement coordinator** (with some integration-expert overlap). Primary job: **confirm a delivery's outcome and trace its history** — find a PO, see whether it delivered/failed/retried, expand the failed event to read the error reason, then jump to the order to resend or fix. Secondary job: export the filtered log to CSV for a record/handover.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (centered, max 1480px, gutter `px-4 → sm:px-6 → lg:px-[34px]`, vertical `py-5 → sm:py-7`):

1. **PageHeader** — `h1` "Delivery log" (Bricolage Grotesque, weight 600, 28/30px, `-0.02em`); subtitle row with a small key icon (Lucide, `--ink-faint`) + text "Append-only audit log · every parse, edit, validation and delivery recorded" (13px, `--ink-muted`). Right-aligned **actions** slot: a single `.btn.btn-secondary` **Export log** (download icon + label), disabled when nothing matches.
2. **Filter / search bar** — flex row, `marginBottom: 14`, `gap: 12`. Left: a row of 7 **filter chips** (`.fchip`, 30px tall, 12px/600) — "All events / Delivered / Failed / Edited / Validated / Parsed / Created". Active chip = `.fchip.active` (green-soft bg `#E9F1EA`, green-deep text, green-tinted border). Right: a **PO search input** — a 200px-wide / 30px-tall pill (`--surface` bg, `--border` border, `--radius` 6px) with a search icon + borderless `<input placeholder="Filter by PO…">` (12.5px).
3. **Body — date-grouped cards**. The filtered events are grouped by calendar date into a `Map`. For each group:
   - A **date eyebrow** label (mono, 10.5px, `--ink-faint`): "Today · 29 June 2026", "Yesterday · 28 June 2026", or a long weekday date. `marginBottom: 8`.
   - A **`.card`** (`--surface`, 1px `--border`, `--radius-lg`, `--shadow-card`, but `padding:0 overflow:hidden`) containing the event **rows**, each separated by a 1px `--border` bottom (last row none).
4. **Each row (desktop)** is a full-width `<button>` laid out as fixed columns at `padding: 11px 16px`:
   `[ time mono 64px ] [ 26px event-icon circle ] [ event label 92px ] [ PO mono 150px ] [ buyer → (arrow) → supplier — grow ] [ actor 110px right ] [ chevron 15px ]`.
   The event label + icon + colors are driven by `canonicalEvent` (not the raw event type): Created (slate/`--surface-2`), Parsed (blue), Validated (green), Edited (violet/`--ai`), Delivered (green), Failed (red). Buyer is blue (`--brand-blue-deep`), supplier is green (`--brand-green-deep`), joined by a small arrow.
5. **Expanded panel** (one row open at a time) renders below the clicked row: `background: --surface-2`, indented `padding: 4px 16px 16px 106px` on desktop (aligns under the content columns). Inside is a nested **`.card`** (`--surface`, `padding 12px 14px`) holding — in order, whichever exist — a **key/value detail grid** (auto-fill `minmax(150px,1fr)`, eyebrow label + mono value), or a free-text `detail` paragraph fallback, an **error banner** (amber if recoverable, red if not), a **field-diff table** (mono, zebra rows, blue field → red from → arrow → green to), and an **action row**: View order (secondary), Open to resend (secondary, failed only), Export entry (ghost).

Type/density observations: extensive **inline `style={{}}`** (almost nothing uses Tailwind here except PageShell/PageHeader). Row heights are not enforced by a single token — desktop rows are `11px 16px` padding but variable height; the icon circle is 26px so effective row height ≈ 48px. Numbers (time, PO) use `.mono` but the codebase mono stack is not declared `font-variant-numeric: tabular-nums` here. Font sizes drift: 11.5, 12, 12.5, 13 all appear within one row.

### Data shown
**Entity:** audit-log events (`LogEntry` internally; `AuditLogEntry` from the API).

| Field (display) | Source field |
|---|---|
| Time (HH:mm:ss) | `ts` ← formatted from `AuditLogEntry.ts` (ISO) |
| Event (Created/Parsed/Validated/Edited/Delivered/Failed) | `canonicalEvent` ← mapped from `AuditLogEntry.action` via `ACTION_TO_EVENT` → `EVENT_TO_CANONICAL` |
| PO number | `po` ← `poNumber ?? orderId ?? "—"` |
| Buyer | `buyer` ← `buyerName ?? "—"` |
| Supplier | `supplier` ← `supplierName ?? "—"` |
| Format (in CSV export + mock details) | `fmt` ← `format ?? "—"` |
| Actor name | `actor.name` ← `actorName`; type ∈ user/system/ai |
| Message | `message` |
| Expanded: detail grid, error banner, field diff | mock-only (`details`, `error`, `recoverable`, `detail`, `diff`) — **not present on API entries** |

**Data source:** `getAuditLog(page=1, pageSize=50)` in `src/lib/api-client.ts` → `GET /api/audit?page=1&pageSize=50` (returns `AuditLogPage { events, total, page, pageSize }`). Fetched via TanStack `useQuery(["audit"])`, `enabled: !isApiMockMode`. In mock mode the component ignores the API and renders the local `MOCK_LOG` constant (8 hand-built entries: PO-DEMO-001, WMT-2026-0341, 850-99201). Note the API's own `getAuditLog` mock (2 entries) is bypassed because the component branches on `isApiMockMode` first.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Export log** button (header, secondary) | `handleExport()` | Builds a CSV of the **currently filtered** rows (8 columns) and triggers a browser download `delivery-log-YYYY-MM-DD.csv`. Disabled (`opacity .5`) when `filtered.length === 0`. |
| **Filter chips** ×7 (All / Delivered / Failed / Edited / Validated / Parsed / Created) | `setFilter(key)` | Client-side filter on `canonicalEvent`; active chip turns green. No URL change. |
| **PO search** input | `setSearch(e.target.value)` | Client-side substring filter across PO / buyer / supplier (case-insensitive). |
| **Event row** (button) | `setOpenId(open ? null : c.id)` | Toggles the expanded detail panel for that row (accordion — only the inner mobile button sets `aria-expanded`; the desktop button does **not**). |
| **Chevron** (within row) | (rotates 180° via CSS on open) | Visual affordance only — clicking it clicks the parent row button. |
| **View order** (expanded, secondary) | `router.push('/inbox/{crossingId}')` | Navigates to the order's inbox detail page. |
| **Open to resend** (expanded, secondary, **failed events only**) | `router.push('/inbox/{crossingId}')` | Navigates to the order (resend lives there) — does **not** retry in place. |
| **Export entry** (expanded, ghost) | inline CSV builder | Downloads a single-row CSV `delivery-{po}-{time}.csv`. |
| **Retry** button (error state only) | `refetch()` | Re-runs the audit query after a load failure. |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Row detail** | Inline expand/accordion panel (in-flow, not an overlay) | Clicking any event row button | Nested card: key/value **detail grid**, OR free-text detail; optional **error banner** (amber/red); optional **field-diff table**; **action row** (View order / Open to resend / Export entry) | Clicking the same row again (`openId → null`), or clicking a **different** row (replaces it — only one open at a time). No Esc / backdrop / X. |
| **CSV download** | Browser file download (transient, no UI) | Export log / Export entry buttons | A generated CSV blob via a temporary `<a>` | n/a (resolves immediately; element removed in code) |

**No modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts.** The page navigates in place (router.push to `/inbox/{id}`) and uses a single inline accordion for detail. This is the page's defining trait for the redesign: all "depth" is one in-flow expander, and all "actions" leave the page rather than acting here.

### States
- **Empty (filter returns nothing):** Renders a `.card` wrapping `<EmptyState compact title="No matching events" sub="Nothing recorded for this filter yet." />` (brand Mark, Bricolage title, muted sub). There is **no distinct first-run / zero-data empty** — if the API returns `events: []`, the same "No matching events" filter-empty copy shows, which is wrong messaging for a genuinely empty log (no "next action" CTA).
- **Loading:** Only in non-mock mode. Renders a `.card` with **3 `SkeletonRow`s** (shimmer bars via `.skel`, widths 64/26/82/150/220/110). There is **no** route-level `loading.tsx` (the folder has none) — the skeleton is component-internal, so a hard navigation shows nothing until the client component mounts.
- **Error:** Non-mock only. A centered `.card` (`padding 48px 24px`) with a `⚠` glyph (28px, `--danger`), the message "Could not load the delivery log. Check your connection and try again." and a **Retry** secondary button calling `refetch()`. Reason is generic (no status code surfaced).
- **Success/feedback:** None for export (file just downloads silently — no toast/confirmation). No feedback when a filter/search returns results. Navigation is the only "success."

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Identical layout (content capped at 1480px and centered, so 1920 just adds side margin). Fixed-column flat rows; filter chips + search on one line.
- **Tablet 768:** Still uses the **desktop** row layout (the mobile switch is at ≤640px via `useIsMobile`). Filter+search row stays horizontal. Fine, but the search input is a fixed 200px and chips can crowd.
- **Mobile 390 (≤640px):** `useIsMobile()` flips to a **stacked card row**: 4 lines per event — (1) icon + label + time + chevron, (2) PO mono (blue), (3) buyer → arrow → supplier (wraps), (4) actor. Filter+search column-stacks (`flexDirection: column`, items stretch); the chip row becomes horizontal-scroll (`.fchip-row` overflow-x auto, scrollbar hidden), chips grow to 36px, search input to 40px full-width, Export button to full-width 40px. Expanded panel uses full width (`padding 4px 14px 14px`) and the detail grid drops to `minmax(120px,1fr)`; diff rows wrap. Action buttons grow to 36px.
- **Known cliffs:** (a) the **640px** breakpoint means 641–768px renders dense desktop rows on a narrow tablet (PO 150px + 110px actor + grow buyer/supplier can clip with ellipsis). (b) Mobile detection is JS (`matchMedia`), so SSR/first paint always renders desktop then snaps — a layout flash on small screens.

### Current UX issues
- **No status-badge system.** Event state is conveyed by a 26px tinted icon circle + a colored text label only — it is **not** the app's pill badge (`.pill-*` exists in globals.css but isn't used here). It violates the one-badge rule: different shape, no consistent padding, and color does carry meaning but the failed/delivered states don't read as the same family as the rest of the app.
- **"Failed" can hide partial truth.** A row labelled "Failed" with a recoverable error shows an amber banner ("… · auto-retry scheduled") but the **row chip stays red** — there is no "Retrying" state in the canonical event set even though the mock data models retries. HTTP 200 is shown as "Delivered" with "HTTP 200" in the grid, conflating transport success with supplier acceptance (the banner copy in mock e6 even mixes "endpoint timeout (30s)" with "HTTP 504" and an "SFTP timeout" status — inconsistent).
- **Numbers don't use tabular figures.** Times, PO numbers, counts and confidence %s use `.mono` but not `font-variant-numeric: tabular-nums`; with proportional digits, the fixed 64px time column and PO column can still jitter.
- **Spacing drift.** Magic px everywhere (11/12/14/16/18/26/106), font sizes 11.5/12/12.5/13 within a single row — not a strict 4/8 scale, and type hierarchy is partly carried by color (blue/green/violet) rather than size+weight.
- **Type hierarchy via color, contrast risk.** Actor text is `.faint` 11.5px; `--ink-faint` against `--surface` is likely below 4.5:1. The detail "eyebrow" labels are 9px — too small to read comfortably.
- **Accordion accessibility gaps.** The **desktop** row button has no `aria-expanded` (only the mobile button does); the chevron is decorative with no label; the open/close has no Esc handling and no focus management. Rows are buttons but have no visible hover/pressed state beyond the open-row background tint.
- **Filter chips are not a tablist / have no aria-pressed.** Seven look-alike chips with no selected-state semantics for AT; "All events" + "Created"/"Parsed" etc. blur the line between filtering by lifecycle stage vs. delivery outcome (the page is titled "Delivery log" but filters include parse/create events).
- **Export is silent.** No toast/confirmation; a coordinator can't tell if the CSV captured the filtered subset vs everything.
- **Empty/zero-data conflation.** A genuinely empty audit log shows "No matching events / Nothing recorded for this filter yet" with no onboarding/next action.
- **No pagination/load-more.** The query asks for 50 rows; if a tenant has thousands of events there's no way to page (`AuditLogPage.total` is returned but unused) — and no date-range filter, only PO text + event type.
- **Mobile-first-paint flash** from JS breakpoint detection; `≤640px` cliff leaves 641–767px on dense desktop rows.

### Redesign recommendations (for Claude Design)
1. **Adopt the one canonical status badge** for the event/outcome column (`.pill-*` family): same shape/size/padding, green=Delivered, red=Failed (blocking), amber=Retrying/Recoverable, blue=Parsed/In-progress, neutral=Created, violet=Edited — each with icon **and** word, never color alone. Add a real **"Retrying"** state so recoverable failures aren't shown as flat red.
2. **Never conflate HTTP 200 with acceptance.** In the delivered detail grid, separate "Transport: HTTP 200" from "Supplier acceptance: ACK received / pending / rejected." Make the row label reflect business acceptance, not transport. Fix the mock inconsistencies (timeout vs 504 vs SFTP) so copy is internally consistent.
3. **Tabular figures everywhere** — add `font-variant-numeric: tabular-nums` to the mono/number cells (time, PO, counts, %s, sizes) so columns stop jittering and money/counts align.
4. **One table density + sticky header + sortable affordance.** Give rows a single fixed height (≈44–48px), one cell padding, low-contrast `gray-200` gridlines, a real sticky column header row (Time / Event / PO / Buyer → Supplier / Actor) with `aria-sort`, and zebra or hover from one token. Keep the navy/violet brand for buyer/supplier accents but stop using 4 font sizes per row.
5. **Make detail an accessible disclosure.** Both desktop and mobile buttons get `aria-expanded`/`aria-controls`; the chevron gets `aria-hidden`; respect reduced-motion on the rotate; support Esc to collapse and keep focus on the trigger. One radius/border/shadow tier for the nested detail card (it currently nests a `.card` inside a `.card`).
6. **Add real states.** Distinguish **genuinely-empty audit log** (first-run: "No events yet — they'll appear here as you upload, validate and deliver" + a link to Upload) from **filter-empty**. Add a route-level `loading.tsx` skeleton so navigation doesn't show a blank frame. Surface the error status code + a "Retry" that's clearly primary.
7. **One primary action, ≥44px.** "Export log" is the page's primary export action — keep it secondary if export is rare, but ensure every interactive control (chips, search, rows, action buttons) is ≥44px tap target with visible hover/pressed and a focus-visible ring (the global `:focus-visible` exists; verify it shows on these inline-styled buttons).
8. **Group the filters by intent.** Visually separate **delivery outcomes** (Delivered / Failed / Retrying) from **lifecycle events** (Created / Parsed / Validated / Edited), or give the page a primary "Deliveries" view and a secondary "All activity" toggle — the title says "Delivery log" but the default shows all lifecycle events.
9. **Add date-range + pagination/load-more** (the API already returns `total`); a procurement coordinator auditing "did last month's POs deliver" needs a date filter, not only PO text search.
10. **Fix the 640px tablet cliff + first-paint flash.** Move the desktop→stacked switch to a CSS container/media query (or render stacked ≤768px) so 641–767px isn't dense desktop, and avoid the JS-only `matchMedia` snap. On mobile keep the stacked-card row (already good) but raise the 9px eyebrow labels and `.faint` actor text above the contrast floor.
11. **Confirm exports.** Show a toast ("Exported 24 events to CSV") so the coordinator knows the file reflects the current filter, and label the Export button to indicate it exports the *filtered* set.
