## 17. Standards reference — `/library/standards`

- **File:** `src/app/(app)/library/standards/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (wide variant — 1480px container)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle + actions slot)
  - `src/components/bridge/EmptyState.tsx` (compact no-results state; renders `MarkSystem`)
  - `src/lib/standards/catalog.ts` (the typed `FIELD_STANDARDS` data + `CanonicalFieldStandards` type — the page's only data source)
- **Capture URL (mock):** `/library/standards`

### What it is & why it exists
This is the conservative "offer ⇔ works" source of truth: a single cross-format reference table showing how each canonical PO field (PO number, order date, buyer, currency, line number, quantity, unit price, etc.) maps to its element/segment path across cXML 1.2, UBL 2.1, EDIFACT, X12, and Peppol BIS. It is not part of an order's live workflow — it sits in the Library as always-on standards documentation. ProcuLink's product rule is that standards visibility is never gated behind a mode, so this screen makes the canonical-model join key auditable to anyone who needs to trust a transform.

### Who uses it & the primary job
Primary persona is the integration expert / 30-year procurement veteran verifying that ProcuLink's field mapping matches the standard a specific supplier expects (e.g. "does PO number really land at cXML `OrderRequestHeader/@orderID` and EDIFACT `BGM 1004`?"). The single most important task: look up a canonical field and read its reference path in the target format, optionally filtering by typing a field name or a standards path.

### Layout & structure (current)
Top-to-bottom, inside a `PageShell variant="wide"` (max-width `var(--container-wide)` = 1480px; gutter ramp `px-4 → sm:px-6 → lg:px-[34px]`, vertical `py-5 → sm:py-7`, page canvas `var(--bg)` #F6F7FA):

1. **PageHeader row** — title "Standards reference" (Bricolage Grotesque, 28→30px, weight 600, `-0.02em`, `var(--ink)` #0B1A2F) over the subtitle "How every order field maps across formats — always visible, never hidden" (13px, `var(--ink-muted)` #5E6779). The header's `actions` slot (right-aligned on `sm`, wraps below the title on mobile) holds the **search input**.
2. **Search input** (in the header actions) — a `<label>` pill: `h-10 w-full` on mobile, `sm:h-8 sm:w-[240px]` on desktop; `var(--surface)` background, `1px var(--border)` border, `var(--radius)` (6px), `0 11px` padding. Inline 15×15 magnifier SVG (stroke `var(--ink-faint)` #98A0AE) + a transparent `<input>` (12.5px, `var(--ink)`), placeholder "Search fields or paths…", `aria-label="Search fields or paths"`.
3. **Single white card** — a plain `<div>` (NOT the `Card` primitive — the code comment explains the table needs zero internal padding for edge-to-edge layout): `var(--surface)` #FFFFFF, `1px var(--border)` #E5E8EE, `var(--radius-md)` (8px), `box-shadow var(--shadow-card)` (`0 1px 2px rgba(11,26,47,0.04)`), `overflow-hidden`.
   - Inside: `overflow-x-auto` wrapper around a `<table>` (`w-full min-w-[760px] border-collapse`).
   - **thead**: one header row. First cell "Canonical field" is `sticky left-0 z-10`, `min-width:180`, with `background var(--surface)`; the other 5 are the reference columns in this exact order: **cXML 1.2 · UBL 2.1 · EDIFACT · X12 · Peppol BIS** (note: cXML-first, defined locally in `REF_COLUMNS`, which intentionally differs from the catalog's `STANDARD_REF_COLUMNS` order that puts UBL first). All header cells: 10.5px, weight 600, uppercase, `tracking-[0.05em]`, color `var(--ink-faint)`, `px-3 py-[9px]`, `1px var(--border)` bottom border, `white-space:nowrap`.
   - **tbody**: one row per canonical field. First cell is sticky (`sticky left-0 z-10`, `var(--surface)` bg) and stacks two lines: the human label (12.5px, weight 600, `var(--ink)`, e.g. "PO number") above the C# canonical field name in mono (10.5px, `var(--font-mono)`, `var(--ink-faint)`, e.g. "PoNumber"). The 5 reference cells: 11px, `var(--font-mono)`, `var(--ink-muted)`, `px-3 py-[11px]`, `white-space:nowrap`, value or "—" if absent. Each row has a bottom `1px var(--border)` divider except the last; full-row hover tints the reference cells (`group-hover:bg-[var(--surface-2)]` #F1F3F7 — note the sticky label cell does NOT get the hover tint, so hover is visually asymmetric).
4. **Request-a-format footer** — `mt-[14px]`, flex-wrap: faint prompt "Need a standard we don't list?" (11.5px, `var(--ink-faint)`) + a ghost link "Request a format" (`<a href="/support">`, 11.5px weight 600, `var(--ink-muted)`, h-8, inline mail-envelope SVG, `hover:bg-[var(--surface-2)]`).

Density/type observations: the page mixes many bespoke fractional sizes (10.5 / 11 / 11.5 / 12.5 / 13px) and bespoke paddings (`py-[9px]`, `py-[11px]`, `mt-[14px]`) rather than a 4/8 scale. Heavy reliance on inline `style={{...}}` with raw CSS-var strings instead of Tailwind utilities/`Card`.

### Data shown
A single entity: **`CanonicalFieldStandards`** rows from the static constant `FIELD_STANDARDS` in `src/lib/standards/catalog.ts`. No API, no hook, no network — the data is a hand-transcribed typed constant (sourced from `ProcuLink/docs/standards-matrix.md`). There are **11 rows** total: header-scope fields `PoNumber, OrderDate, BuyerName, Currency, Lines` and line-scope fields `LineNumber, BuyerItemCode, Description, Quantity, Unit, UnitPrice`. Displayed fields per row: `label`, `canonicalField`, and the 5 reference strings `cxml`, `ubl`, `edifact`, `x12`, `peppolBis`. The richer per-standard support matrix in the same file (`STANDARDS` array, with `parse`/`transform`/`conformance`/`transport`/`referenceUrl`/support-level badges) is **NOT** rendered on this page at all — only the field-mapping table is. The `scope` ("header" vs "line") field exists in the data but is **not** surfaced (no grouping/sectioning on screen).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Search input (`#q`, header actions) | Type a query | Client-side filter: lowercased substring match against `label`, `canonicalField`, and all 5 reference values; table re-renders to matching rows live (no submit). Stays on page. |
| Table column headers | None | Static `<th>` — no sort, no `aria-sort`, not clickable. |
| Table rows / cells | Hover only | `group-hover` tints the 5 reference cells (`--surface-2`); rows are not clickable, no row action, no detail/expand. |
| "Request a format" link | Click | Navigates (full `<a href>`) to `/support`. |

### What opens / what closes

**No overlays — navigates in place.** This page opens no modal, drawer, sheet, dialog, popover, dropdown, tooltip, or toast. The only "transient" surface is the inline no-results `EmptyState` rendered conditionally inside the card (not an overlay). The "Request a format" control is a plain anchor navigation to `/support`, not an overlay. (Historically the catalog comments reference a `StandardsFieldPopover` that consumes the same data, but no such component file exists in this repo, and this page never renders one.)

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| No-results message | Inline panel (not an overlay) | Search query matching zero rows | `EmptyState compact` — brand Mark + "No fields match" + `Nothing for "{q}".` | Clearing/changing the search so ≥1 row matches |

### States
- **Empty:** There is no true "no data" state — `FIELD_STANDARDS` is a static non-empty constant, so the table always has 11 rows. The only empty-like state is **no search match**, which renders `<EmptyState compact title="No fields match" sub={'Nothing for "{q}".'} />` *below* the (now header-only) table inside the card. Note: the `<thead>` still renders above the empty state, so on a no-match you see column headers and then the centered empty mark — slightly awkward.
- **Loading:** None. No `loading.tsx` exists in the route folder and there is no async data, so there is no skeleton or spinner — the page is fully static and renders immediately.
- **Error:** Not handled / not applicable — no fetch can fail. If `FIELD_STANDARDS` were ever empty the table body would simply be empty with headers showing (no guard for that case).
- **Success/feedback:** None — read-only reference screen. The only feedback is live row filtering and hover tint.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered in the 1480px wide container; table is well within width so no horizontal scroll; search input is the compact `sm:w-[240px] sm:h-8` pill aligned right in the header.
- **Tablet 768:** Same desktop header layout (search still right-aligned at the `sm` breakpoint, which is 640px). The table's `min-w-[760px]` is right at the edge of usable width minus gutters, so the `overflow-x-auto` may begin to scroll horizontally; the first "Canonical field" column stays sticky-left while the 5 reference columns scroll.
- **Mobile 390:** PageHeader stacks (title/subtitle, then actions below). Search input becomes full-width `h-10`. The table forces horizontal scroll (`min-w-[760px]` >> 390px viewport); the sticky first column keeps the field anchored while you scroll the standards columns sideways. This is the intended "scroll the codes, keep the field" behavior, but it is still a wide horizontal scroller on a phone (not a stacked card list).

No hard breakpoint cliff, but the mobile experience is a side-scrolling spreadsheet rather than a stacked, mobile-native layout.

### Current UX issues
- **Not on a 4/8 spacing rhythm (Bar 1).** Bespoke odd values everywhere: `py-[9px]`, `py-[11px]`, `mt-[14px]`, `px-[10px]`, header `gap-x-3 gap-y-2`. Drift from the strict 4/8 scale.
- **Fractional type scale, not one scale (Bar 2).** 10.5 / 11 / 11.5 / 12.5 / 13 / 28 / 30px coexist. Hierarchy is partly carried by color (`--ink` vs `--ink-muted` vs `--ink-faint`) and the faint reference cells (`--ink-muted` #5E6779 mono on white is fine; `--ink-faint` #98A0AE used for the canonical sub-label and headers is borderline for small text contrast).
- **No tabular figures (Bar 3).** Reference paths contain numbers (`BGM 1004`, `PO101`, `C507/2380`, `cXML 1.2`); they're mono so alignment is OK, but the design system's tabular-figure rule isn't explicitly applied and version labels ("cXML 1.2", "UBL 2.1") sit in headers without consistent figure treatment.
- **Headers look sortable but aren't (Bar 5).** No `aria-sort`, no sort affordance, no hover on headers — yet it's a data table where users expect to sort/group (e.g. by header vs line scope). The `scope` field exists in data but is invisible.
- **Asymmetric hover (Bar 5).** Row hover tints only the 5 scrolling reference cells; the sticky label cell keeps `--surface`, so a hovered row reads as two-toned.
- **No-match state shows headers above an empty mark (Bar 6).** The empty state renders below a still-visible `<thead>`, which looks unfinished. The empty copy is fine but the composition is awkward.
- **The screen under-delivers on its own promise.** The far richer `STANDARDS` support matrix (parse/transform support levels, conformance notes, transport, spec links) is in the same catalog file but never shown. A coordinator wanting to know "is X12 output actually supported?" gets only field paths, not the honest per-format support/badges — a missed offer⇔works opportunity on the very page meant to be that source of truth.
- **No deep-linkable field rows / no copy affordance.** Veterans frequently want to copy a path (e.g. `cac:Item/cac:BuyersItemIdentification/cbc:ID`); there's no copy button and no row anchor.
- **Mobile is a side-scroll spreadsheet, not stacked (Bar 10).** No card/stacked variant; phone users horizontally scroll a 760px table.
- **Heavy inline-style + `--var` strings instead of `Card` + utilities (Bar 8).** The card is a hand-rolled div replicating `Card` styling; consistency depends on manual token copying rather than the shared primitive.
- **Search input height inconsistent with the rest of the app's 44px target (Bar 9).** `sm:h-8` (32px) is below the 44px interactive minimum on desktop; only mobile gets `h-10` (40px), still under 44px.

### Redesign recommendations (for Claude Design)
Ranked most-impactful first. Keep navy #0B1A2F + violet Bridge brand; green=success, amber=warn, red=block; Lucide icons; shadcn/Tailwind.

1. **Merge in the format-support matrix as the page's primary frame (offer⇔works).** Add a top section (or a "Formats" tab) rendering the `STANDARDS` array with ONE status-badge system (Bar 4): per format show parse/transform support as green "Supported" / amber "Partial" / neutral "Planned" / "—" pills with icon+word, plus the one-line `conformance` note and a spec link. This turns the page into the real source of truth instead of only a field-path table. Never render "supported" green where the data says partial/planned.
2. **Make the field table a real, accessible data table (Bars 3, 5).** One row height, one cell padding on a 4/8 grid, gray-200 gridlines, sticky header, sortable columns with `aria-sort`, and group rows by `scope` (Header fields / Line fields) with a subtle section header — surface the `scope` data that already exists. Apply tabular figures to all paths/versions.
3. **Add a per-row copy affordance + field detail.** A copy-icon button on hover for each reference cell (copy the exact path) and/or a click-to-expand row revealing all five mappings stacked with labels using the HUMAN field name first (already done: "PO number" over `PoNumber`). This is the one place an overlay (a lightweight popover or side sheet showing a single field across all formats) would genuinely help — give it a clear close (X / Esc / scrim) and animate from the trigger.
4. **Fix the no-match empty state (Bar 6).** Hide the `<thead>` when zero rows match, center the `EmptyState`, and add a "Clear search" action; consider a "Request a format" CTA inline in that empty state since a missing field is the natural moment to ask.
5. **Standardize spacing & type (Bars 1, 2).** Collapse the fractional sizes onto the system type scale (label 500 / body 400 / heading 600) and snap all padding/gaps to 4/8. Replace `--ink-faint` small text with a token that clears 4.5:1 on white.
6. **Mobile = stacked cards (Bar 10).** Below `sm`, render each canonical field as a card: human label + canonical name as the card title, then a labeled list of the 5 format paths — no horizontal spreadsheet scroll.
7. **Use the `Card` primitive (or a documented table-card variant) (Bar 8).** Replace the hand-rolled div so radius/border/shadow come from one source; if zero-padding is needed, add a `padding="none"` prop to `Card` rather than forking it inline.
8. **Bump interactive sizes (Bar 9).** Search input to ≥44px tall (or document the dense-toolbar exception consistently across the app), with visible focus-visible ring and hover; add an `aria-label`'d clear (×) button inside the input when non-empty.
9. **Add breadcrumb/active-nav context.** Show "Library › Standards" so depth is obvious and back is predictable (Bar: nav/breadcrumbs for depth).
