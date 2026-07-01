## 11. Advance Shipping Notices (Inbound ASNs) — `/inbound/asns`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/inbound/asns/page.tsx`
- **Key components:**
  - `src/app/(app)/inbound/asns/page.tsx` (the whole page incl. a locally-defined `StatusBadge`, `SkeletonRow`, `SkeletonCard`)
  - `src/app/(app)/inbound/asns/loading.tsx` → `BridgePageLoader` (`src/components/bridge/BridgeLoader.tsx`)
  - `src/components/bridge/layout/PageShell.tsx` (page canvas + max-width container)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle row)
  - `src/components/bridge/layout/Card.tsx` (the table/empty/error/loading surface)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile stacked card)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (only the error-state Retry button)
  - Data: `getAsns`, `AsnDto`, `isApiMockMode` from `src/lib/api-client.ts`
- **Capture URL (mock):** `/inbound/asns` — mock mode (`isApiMockMode`) is on, so `getAsns()` returns `_mockAsns` (two rows: `ASN-2026-001` / FastParts Inc / received, `ASN-2026-002` / GlobalComponents / pending) after a 400 ms delay. No mock ids are needed for any detail route because there is no detail route.

### What it is & why it exists
This is the read-only inbound list of **Advance Shipping Notices** — the EDIFACT DESADV / ASN documents a supplier sends to confirm an upcoming delivery. In ProcuLink's `parse → normalize → validate → review → transform → deliver → learn` loop, ASNs sit on the **inbound** side (documents arriving from suppliers), parallel to inbound invoices. The defining fact of this page today is that **ASN ingestion is not built** — DESADV parsing needs a commercial EDI licence the founder declined, and the backend `DesadvController POST /api/asns/upload` returns `501`. So the page deliberately ships **no upload control** and leads with an honest amber "Coming soon" notice; it only lists ASNs that may have been created by other means.

### Who uses it & the primary job
**Procurement coordinator** (the buyer-side operator). The intended primary job is "see which supplier shipments are inbound and confirmed." In its current state the real job is reduced to **awareness**: confirm whether any ASNs exist and read the honest "not available yet" message. There is no action the user can take on this page.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width `--container-wide` = 1480px; gutter ramp 16→24→34px, vertical padding 20→28px; canvas `var(--bg)`):

1. **Page header** (`PageHeader`) — `h1` "Advance Shipping Notices" (Bricolage Grotesque display, 28→30px, weight 600, letter-spacing -0.02em). Subtitle is a live count: `"{n} notices"` (e.g. "2 notices"), or `"Loading…"` while fetching in non-mock mode. No `actions` slot is passed, so the header is title-only. Bottom margin 20→24px.
2. **"Coming soon" notice** — a full-width amber callout (`mb-4`, radius 8px, `1px` amber-soft border + `3px` amber left border, amber-soft background, amber-deep text, 12.5px). Contains a 6px amber dot + bold "Coming soon." + "ASN / EDIFACT DESADV ingestion isn't available yet. We'll let you know when you can upload advance shipping notices." This renders in **every** state (loading/error/empty/populated) because it sits above the conditional block.
3. **Body** — one of four mutually-exclusive branches:
   - **Loading** (only when `isLoading && !isApiMockMode`): a `Card dense` (hidden < sm) wrapping a 5-column table whose header row is real but body is 3 `SkeletonRow`s (animated pulse bars at widths 120/140/80/60/80px); plus 3 `SkeletonCard`s shown only on mobile (`sm:hidden`).
   - **Error** (`isError && !isApiMockMode`): a `Card` with a centred column (p-10) — "Failed to load advance shipping notices" (14px, 600) + a green primary `Button` "Retry" that calls `queryClient.invalidateQueries(["asns"])`.
   - **Empty** (`asns.length === 0`): a `Card` with centred column — "No advance shipping notices yet" (15px, 600) + muted explainer "ASNs are sent by suppliers to confirm upcoming deliveries. Inbound ASN / EDIFACT DESADV ingestion is coming soon — there's nothing to upload here yet." No CTA (deliberately, since upload isn't built).
   - **Populated**: two parallel renders — a **mobile** stacked list (`flex flex-col gap-3 sm:hidden`) of `MobileListRow`s, and a **desktop** `Card dense` (hidden < sm) wrapping a 5-column table.

**Desktop table** (`overflow-hidden`, 12.5px base font): header row has a `2px` bottom border, columns "ASN #", "Supplier", "Ship date", "Packages", "Status" rendered as 10.5px uppercase faint labels (letter-spacing 0.06em), each `th` padded `px-4 py-2.5`. Body rows: `px-4 py-3` cells, `1px` `--surface-2` bottom divider (none on the last row). Cells: ASN # (mono, 600, ink), Supplier (ink-muted), Ship date (ink-muted), Packages (medium, ink — bare number), Status (the local `StatusBadge`).

**Mobile card** (`MobileListRow`, padding 14, radius `--radius-md`, card shadow, `min-height: --tap-min`): a left `3px` green accent strip; top row = ASN # (13px mono 600) over supplier name (12px muted) on the left, `StatusBadge` on the right; bottom row = "Ship: {date}" + "{n} pkgs" (medium ink).

### Data shown
Single entity: **ASN** (`AsnDto`). Source = `getAsns()` (`GET /api/asns`; in mock mode returns `_mockAsns`).

| Field (DTO) | Column / display | Notes |
|---|---|---|
| `asnNumber: string \| null` | "ASN #" | mono, 600; `"—"` when null |
| `supplierName: string \| null` | "Supplier" | `"—"` when null. (`supplierId` is in the DTO but **never displayed**.) |
| `shipDate: string \| null` | "Ship date" | raw string, no date formatting; `"—"` when null |
| `packageCount: number` | "Packages" | bare integer on desktop; "{n} pkg(s)" with pluralisation on mobile |
| `status: string` | "Status" | drives `StatusBadge`; only `"received"` is special-cased, everything else renders as "Pending" |
| `id` | (none) | React key only — not shown, not linked |
| `createdAt` | (none) | present in DTO/mock, never displayed |

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| "Retry" button (error state only) | `onClick` → `queryClient.invalidateQueries({ queryKey: ["asns"] })` | Refetches `getAsns`; re-renders into loading then list/empty/error |
| Table rows | none | Rows are **not** clickable, not links, no row-actions, no menu, no hover affordance beyond the static divider |
| Mobile `MobileListRow` | none | `onClick` is not passed, so the row is non-interactive (no `role=button`, no tab stop) |
| Header / toolbar | none | No filters, no search, no sort, no column controls, no upload, no pagination |

There are **no** sortable headers (no `aria-sort`), no filters, no search, no bulk actions.

### What opens / what closes
**No overlays — navigates in place.** This page opens **zero** modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts. There is no row detail route, no row menu, no upload dialog (intentionally suppressed because `POST /api/asns/upload` returns 501). The only state change is the in-place refetch triggered by the error-state "Retry" button. Nothing here needs an X/Esc/backdrop because nothing transient is ever rendered.

### States
- **Empty:** Handled. Centred `Card` — "No advance shipping notices yet" + an honest explainer. Deliberately **no** next-action CTA because upload isn't built; the "Coming soon" amber notice above carries the forward expectation.
- **Loading:** Handled two ways. (1) Route-level `loading.tsx` → `BridgePageLoader` (the animated buyer→supplier wire mark, "Loading ASNs…", reduced-motion-safe) shown during navigation/suspense. (2) In-page skeleton (table skeleton on desktop, card skeletons on mobile) when `isLoading && !isApiMockMode`. Note: in **mock mode the skeleton is skipped** (`isLoading && !isApiMockMode` is false), so mock renders straight to the populated table after the 400 ms delay.
- **Error:** Handled. Centred `Card` with reason ("Failed to load advance shipping notices") + green "Retry" button. Reason is generic — it does not surface the HTTP status or message.
- **Success/feedback:** None. No toast on retry; the list simply re-renders. No optimistic or confirmation feedback (there are no mutations on this page).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Container caps at 1480px and centres; the 5-column table fills the `Card`. Columns are auto-width (no fixed widths) so wide screens leave a lot of right-side whitespace after the last "Status" column. Identical layout at both widths (no extra columns appear at HD).
- **Tablet 768:** Still desktop table (the `sm:` breakpoint = 640px, so ≥768 shows the table, not cards). Table is full-width; columns can feel sparse.
- **Mobile 390:** Table is hidden (`hidden sm:block`); the stacked `MobileListRow` cards render instead (green-accent strip, ASN#/supplier/status header, ship-date/packages footer). Skeleton also swaps table→cards. This is a proper stacked-not-shrunk mobile treatment. No known breakpoint cliff.

### Current UX issues
- **Page is a dead end with a contradiction:** it offers an inbound "ASN" list but the only thing it can ever say is "Coming soon / nothing to upload." A populated table can still appear (mock, or ASNs created by other means) **above** a notice that says ingestion isn't available — the populated state + "Coming soon" banner read as contradictory. (Design bar: never show one thing while claiming another.)
- **Bespoke status badge breaks the ONE badge system (bar #4).** `StatusBadge` is defined inline here (10.5px, `px-2 py-0.5`, 5px dot) instead of the canonical `UnifiedStatusBadge`/`Pill`. It also has a **truthiness bug**: only `status === "received"` → green "Received"; **every other value** (including any failure/rejected/error status) silently renders amber "Pending". A failed ASN would display as a benign "Pending" — exactly the "never show healthy when something failed" anti-pattern.
- **No tabular figures (bar #3).** "Packages" count and `shipDate` use the default proportional font; numbers will jitter column-to-column and dates won't align. ASN # is mono (good) but the numeric column isn't `font-variant-numeric: tabular-nums`.
- **Dates are raw strings (bar #3 / clarity).** `shipDate` prints verbatim (`"2026-05-15"`) with no locale formatting or relative hinting.
- **Table density drift (bars #1, #5).** Header uses a `2px` border + `py-2.5`; body rows use `1px var(--surface-2)` dividers + `py-3`. No sticky header, no zebra/hover, no `aria-sort`, no sortable affordance — inconsistent with the canonical table density used elsewhere.
- **No row affordance.** Rows look tabular but do nothing — no detail, no link, no hover. A coordinator can't drill into an ASN's packages/lines.
- **Error message is thin (bar #6).** "Failed to load" gives no status code or cause; Retry is the only recourse.
- **Inline-styled, locally-defined components (consistency).** `StatusBadge`, `SkeletonRow`, `SkeletonCard` and the amber notice are all hand-rolled with inline `style` + hard-coded hex fallbacks (`#FFF4D6`, `#D4900A`, `#7A5700`) rather than shared primitives/tokens — drift risk if the design system changes.
- **Empty/coming-soon overlap.** The empty `Card` explainer repeats the same "coming soon / nothing to upload" message already in the amber banner — redundant copy stacked vertically.

### Redesign recommendations (for Claude Design)
1. **Resolve the "coming soon" contradiction first.** Decide one of: (a) keep it as an honest **placeholder page** — drop the table entirely, show one centred "Inbound ASNs are coming soon" state (icon, one sentence, optional "Notify me"), and don't render rows that can't be acted on; or (b) if ASNs can genuinely arrive, demote the banner to an inline info strip and make the list real. Don't show a populated table beneath a "not available yet" banner.
2. **Replace the local `StatusBadge` with the canonical `UnifiedStatusBadge`/`Pill` (bar #4) and fix the truthiness bug.** Map explicit statuses (received=green, pending=amber, failed/rejected=red, unknown=neutral) with icon+word; never let an unknown/failed status fall through to a benign "Pending".
3. **Apply tabular figures (bar #3)** to Packages, Ship date (and ASN # already mono): `font-variant-numeric: tabular-nums`. Right-align the Packages column so counts line up.
4. **Format `shipDate`** to a consistent locale date (e.g. `15 May 2026`) and consider a relative hint ("in 3 days") since ship dates are the actionable signal on an ASN.
5. **Adopt the canonical table density (bar #5):** single row height, `gray-200` gridlines, sticky header, hover row, and — if rows become actionable — `aria-sort` + sortable headers (by ship date / supplier / status).
6. **Make rows lead with the human field and become drill-in targets** if a detail view is ever built: ASN # + supplier first, status pill right-aligned, packages as a secondary metric — and route the row (and `MobileListRow` via `onClick`) to an ASN detail showing packages/lines.
7. **Strengthen the error state (bar #6):** include the failure reason/status alongside Retry, keep Retry as the single ≥44px green primary.
8. **De-duplicate empty vs banner copy:** if keeping both, let the banner state the capability status once and the empty card focus on "what to do next" (or remove the empty card when the banner already covers it).
9. **Tokenise the amber notice and skeletons** — replace inline hex fallbacks with design-system tokens and reuse a shared `Callout`/`Skeleton` primitive so this page tracks the system automatically.
10. **Keep the navy/violet brand, green=success/output, amber=warning, red=blocking** throughout; the green accent strip on mobile rows and the green Retry primary are consistent with the system and should stay.
