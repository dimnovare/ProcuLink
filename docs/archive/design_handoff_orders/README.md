# Handoff: Orders + Order Detail

> Implementation handoff for the ProcuLink **Orders list** and **Order detail** screens.

---

## Overview

This handoff covers two screens in the ProcuLink Workbench:

1. **Orders list** (`/orders`) — table of all purchase orders that have been uploaded, with status, line counts, and per-row unresolved indicators.
2. **Order detail** (`/orders/[id]`) — single-order workbench. Surfaces missing supplier-item-code lines in a "Resolve" form at the top, full line items table below, and order metadata in a right-rail sidebar.

The design follows the **ProcuLink design system v1.0 — "The Bridge Layer"** that already lives in this repo under `design-system/`. No new tokens are introduced; the screens use the existing palette, type scale, and component vocabulary.

---

## About the design files

The files in this bundle are **design references created in HTML / React (via Babel-standalone)** — prototypes that demonstrate the intended look, behavior, and information density. They are **not production code to copy directly.**

The task is to **recreate these designs in the ProcuLink codebase's existing environment** (Next.js 15 App Router + Tailwind + shadcn/ui + TanStack, per `design-system/README.md`). Use the codebase's established patterns:

- `components/ui/*` (shadcn primitives) for `Button`, `Input`, `DropdownMenu`, `Checkbox`, etc.
- `design-system/tokens/tailwind.config.ts` for color / spacing / radius classes.
- The signature components already in `design-system/components/` (`StatusJourney`, `XCard`, etc.) where you choose to wire them — though this redesign intentionally uses the **plainest** primitives because the brief is "simple yet professional."
- TanStack Query for data, TanStack Table is optional for the list (a plain `<table>` is fine — only 8–50 rows expected at a time).

---

## Fidelity

**High-fidelity.** Final colors, typography, spacing, border radii, and hover states are all locked. Reproduce pixel-for-pixel using the existing token system. Where the prototype hard-codes a hex value, prefer the equivalent token from `tokens.css` / `tailwind.config.ts`.

---

## Design philosophy for these two screens

Two earlier passes were rejected for being **too crowded and too colourful**. The accepted direction is governed by these rules — apply them as you implement:

1. **Color is information, not decoration.** A row should look identical to every other row unless action is required. The only places color appears in a default-state row are: the status-pill dot (~7px), the file-type chip foreground letter, and the optional unresolved-count flag.
2. **One accent per element.** A row does NOT get a colored left bar AND a colored chip AND a colored status pill. Pick one. (We picked the status pill.)
3. **Type and hierarchy carry the design.** Bricolage Grotesque appears **twice per screen at most** — the page title and the order total. Everything else is Inter; everything code-like is JetBrains Mono.
4. **Navy chrome is allowed to be dark.** The sidebar + topbar are saturated navy. That's the brand. But the work area below the topbar is white / cool-grey, with thin (1px) borders and almost no shadows.
5. **No decorative gradients, no EdgeRails, no stage stepper inside the order page.** Earlier prototypes added these — they don't belong here.
6. **Color budget per screen ≤ 3 accent colors** in addition to ink/border greys.

---

## Screens

### 1. Orders list

**Route:** `/orders`
**Purpose:** Operator scans the queue, identifies orders that need attention (review/failed), opens one.

#### Layout

Full-viewport flex row:

- **Sidebar** — `224px` fixed, navy chrome (see [App shell](#app-shell)).
- **Main column** — fills remaining width.
  - **Topbar** — `54px`, navy, with `1.5px` soft-gradient `LinkSpine` underneath.
  - **Page header** — `padding: 28px 32px 22px`, white surface, bottom border.
    - `<h1>`: "Orders" — Bricolage Grotesque, `28px / 600 / -0.022em`, `color: ink`.
    - Sub-line: `<count> total` in ink, then optional `<review-count> need review` in amber (`#B36D14`). Sub-line is `12.5px` ink-muted with the inline numerals in ink.
    - Right side: ghost refresh icon button + primary `Upload` button (`navy` bg, `#fff` fg, `32px` tall, `12.5px / 500`).
  - **Filter bar** — `padding: 14px 32px`, white surface, bottom border. Single row:
    - Search input — `max-width: 420px`, `1px solid borderStrong`, radius `6px`, leading search icon, trailing clear button when filled.
    - Status dropdown — `160px min-width`, same border, chevron icon. Opens a `200px` popover with the 7 status options.
  - **Table** — wrapped in a `bg-page` (`#F7F8FA`) area with `padding: 20px 32px 32px`. The table card is `radius: 10px` with a single `1px` `border` and no shadow.

#### Table grid

7 columns, identical for header + body rows:

```css
grid-template-columns:
  minmax(220px, 1.4fr)   /* PO Number */
  minmax(220px, 1.6fr)   /* Supplier */
  110px                  /* Date */
  70px                   /* Lines */
  110px                  /* Total */
  140px                  /* Status */
  90px;                  /* Updated */
gap: 14px;
min-width: 1040px;       /* so the card horizontally scrolls instead of collapsing */
```

Wrap the rows in a parent with `overflow-x: auto` for narrow viewports.

#### Header row

- Padding: `10px 24px`
- Background: white, border-bottom `1px solid border`
- Each column: `10.5px / 500 / uppercase / 0.08em tracking`, `color: inkFaint`
- Click to sort — when active: `color: inkMuted` and a `↑` / `↓` glyph appears.

#### Body row

- Padding: `cozy: 16px 24px` / `compact: 11px 24px` (density toggle).
- Border-top: `1px solid borderFaint` between rows (no border above the first row).
- **Hover:** background shifts to `surface2` (`#F1F3F7`). No chrome change, no animation beyond a `120ms` background transition.
- **Click** anywhere on the row → open detail.

Column contents:

| Column | Content | Style |
|---|---|---|
| **PO Number** | `<SrcChip type="PDF" />` + mono PO with `gap: 10px` | SrcChip: `10px / 700 / mono / letter-spacing 0.04em`, `surface2` bg, `border 1px`, foreground color per type (see below). PO: `12.5px / 500 / mono / ink`. |
| **Supplier** | Supplier name (line 1) + `from <buyer>` (line 2) | Line 1: `13px / 500 / ink`. Line 2: `11.5px / inkFaint`, `margin-top: 2px`. Truncate with ellipsis. |
| **Date** | `"May 27, 2026"` | `12.5px / inkMuted`. |
| **Lines** | `12` — or `12 · 2` (with the unresolved count after a dot, in amber `500`) | `13px / ink / mono / tabular-nums`, right-aligned. Unresolved badge inline: `11.5px / 500 / amber`. |
| **Total** | `€ 4,436.73` | `12.5px / 500 / mono / ink`, right-aligned. |
| **Status** | `<StatusPill status={...} size="sm" />` | See [StatusPill](#statuspill). |
| **Updated** | `"09:05"` (time only — date is implicit from the row) | `11.5px / inkFaint`, right-aligned. |

#### Table footer

- Padding: `12px 24px`, white, border-top.
- Left: `"Showing 8 of 8"` (`11.5px / inkMuted`, numerals in ink).
- Right: pagination — `chevron-left | "1 / 1" | chevron-right`. Disabled chevrons at `opacity 0.4`.

#### SrcChip color map

Backgrounds are uniform `surface2`. Only the foreground letter color varies. Border is `1px solid border` so the chip reads as a tag, not signage.

| Type | Foreground |
|---|---|
| PDF | `#B43838` |
| XLSX | `greenDeep #1E6D29` |
| CSV | `#345470` |
| XML / cXML | `#5E3DB0` |
| EDI | `amber #B36D14` |
| EMAIL | `#4A5568` |
| API | `greenDeep` |
| JSON | `#846100` |

---

### 2. Order detail

**Route:** `/orders/[id]`
**Purpose:** Resolve any blocking issues (missing supplier item codes), then cross the bridge.

#### Layout

Within the main column (sidebar + topbar identical to the list):

- **Scroll container** — `flex: 1; overflow: auto`. Inside it: `max-width: 1280px; margin: 0 auto; padding: 0 36px 40px`.
- **Header block** — `padding: 28px 0 22px`.
  - "← Back to orders" text link above the title (`12px / 500 / inkMuted`, `gap: 8px` with the icon).
  - Title row: `<h1>` PO number (Bricolage `30px / 600 / -0.022em`) + `<StatusPill />` (medium size).
  - Meta line: `<buyer> → <supplier> · Created May 27, 2026 · 09:05` — `12.5px / inkMuted`, the buyer/supplier names in `ink / 500`, separators are `borderStrong`.
  - Right side: `View source` (ghost) · `Save draft` (secondary) · `Cross the bridge` (primary, navy, disabled while unresolved). Buttons wrap below the title on viewports < ~960px.
- **Meta strip** — single horizontal card, `padding: 16px 22px`, `border 1px / radius 10px`. 5 cells separated by `1px borderFaint` vertical dividers:
  1. **Source** — SrcChip + filename (`12px / mono / inkMuted`).
  2. **Lines** — count (`13px / mono / ink`).
  3. **Total** — order total in Bricolage `17px / 600 / -0.015em / ink`. **This is the second display-font moment on the page.**
  4. **Currency** — `"EUR"` mono.
  5. **Status** — current stage word ("Validating") + `· 3 of 5` in faint.
  Cell labels above values: `10.5px / 500 / uppercase / 0.08em / inkFaint`, `margin-bottom: 8px`.

- **Body grid** — `grid-template-columns: 1fr 304px; gap: 22px; align-items: flex-start`.
  - **Left column (`min-width: 0`)**:
    - **Resolve form** (shown only while unresolved lines exist) — see [Resolve form](#resolve-form).
    - **Line items table** — see [Line items table](#line-items-table).
  - **Right column (`304px`)**:
    - **Details card** (KV list).
    - **Counterparties card** (Buyer / Supplier / Output).
    - **Activity card** (timeline).

All right-column cards use the same chrome: `white / 1px border / radius 10px / margin-bottom 14px`. Card header: `padding: 14px 20px; border-bottom 1px solid border; font 12.5px / 600 / ink`. No edge strips.

#### Resolve form

A bordered card titled `Resolve <N> missing supplier code(s)`. Header right-action slot reads, in `11.5px / inkFaint`: "AI suggestions are prefilled — review each one".

Inner table grid:
```
grid-template-columns: 40px 1.1fr 1.7fr 70px 90px 1.4fr 32px;
gap: 14px;
```

Columns: `Line`, `Buyer SKU`, `Description`, `Qty`, `Price`, `Supplier code`, clear-button slot.

- **Header strip** at `padding: 11px 20px`, bg `surface2`, label `10.5px / 500 / uppercase / 0.08em / inkFaint`.
- **Rows** at `padding: 16px 20px`. Borders between rows are `1px borderFaint`.
- **Supplier-code input**: `height 32px`, `padding 0 10px`, `radius 6px`.
  - Empty state: `1px solid amber@33%`, bg `amberSoft@33%`.
  - Filled / non-AI: `1px solid borderStrong`, bg `surface`.
  - AI prefill: input text appears in `inkMuted` (not full ink) — visually indicating it's a suggestion. A tiny `✦ 84%` hint sits at the right inside the field, `10.5px / 500 / inkFaint`, `gap 4px`. Once the user edits the value, the AI-prefill state turns off and the text becomes full ink.
- **Clear** button: `26×26px`, transparent, just an `×` icon (`12px / inkFaint`).
- **Footer strip** at `padding: 14px 20px`, bg `surface2`, border-top:
  - Left: `<label>` with a `Checkbox` + `"Save as mappings for future orders"` (`12.5px / inkMuted`).
  - Right: `Skip` (ghost) + `Resolve and continue` (primary, disabled when any code blank).

#### Line items table

Bordered card titled `Line items`. Header right-action shows `<count> items · <N> unresolved` (the unresolved chunk in amber when present).

Grid:
```
grid-template-columns: 40px 1.2fr 1.2fr 2.2fr 60px 90px 100px;
gap: 14px;
```

Columns: `#`, `Buyer SKU`, `Supplier code`, `Description`, `Qty`, `Unit price`, `Line total`. Each row at `padding: 13px 20px`. Borders between rows: `1px borderFaint`. No row tint; the only colored thing in an unresolved row is the supplier-code cell, which reads as italic amber text: `<dot 6×6 amber> Needs code` instead of a code.

**Totals row** at `padding: 14px 20px`, border-top `1px solid border`:
- Empty cells through `Description`.
- Column 4: `"Total"` (`11.5px / 500 / inkFaint`).
- Column 5: total qty in mono.
- Column 7: total amount — `14px / 600 / mono / ink`.

#### Sidebar — Details card

KV list, each row `padding: 9px 0`, `grid-template-columns: 100px 1fr`, separator `borderFaint` between rows.
- Label: `11.5px / 400 / inkFaint`.
- Value: `12.5px / 500 / ink` (or `12px mono` when the value is a code).

Rows: Order date · Reference · Currency (mono) · Incoterm (mono) · Payment · Ship to · By.

#### Sidebar — Counterparties card

Two stacked blocks separated by a `1px borderFaint` divider.

Each block:
- `<dot 8px>` colored buyer-blue / supplier-green.
- Right side, vertical stack:
  - Eyebrow label "BUYER" or "SUPPLIER" — `10.5px / 500 / uppercase / 0.06em / inkFaint`.
  - Name — `13px / 500 / ink`.
  - Code — `11px / mono / inkFaint`.

Below: a third row `Output → <SrcChip type="cXML" /> <template-name>` aligned horizontally. Template name in mono.

#### Sidebar — Activity card

Each entry:
- `<dot 7px>` colored by event kind: ok → green, warn → amber, err → danger, ai → inkFaint.
- Vertical stack with line and who (`12.5px / ink` then `11px / inkFaint`).
- Time-ago on the right (`11px / inkFaint`).
Separators: `1px borderFaint` between entries.

Top-right action: "View all" link (`11.5px / inkMuted`).

---

## Components reference

### App shell

#### Sidebar (`224px` wide, navy `#0B1A2F` background)

- **Brand row**: ProcuLink mark (gradient blue→green chain glyph, `24px`) + wordmark (`14.5px / 600 / #fff`).
- **Workspace switcher**: `navySurface` bg (`#14253D`) tile, `9×11px` padding, `radius 7`. 24px-square gradient avatar + name (`12.5px / 500 / #fff`) + plan (`10.5px / navyMuted`).
- **Nav items**:
  - Group labels (`Inbox`, `Workbench`, `Library`, `Operations`): `10px / 600 / uppercase / 0.1em / navyMuted`.
  - Item: `padding 7px 12px`, `radius 6px`, `13px / 400 / navyText`. Active state: `navySurface` bg + `#fff` fg + `500` weight. **No** colored side-bar — active state is just the bg change.
  - Right-side count (only when > 0): `10.5px / 500 / navyMuted` (or `danger` for the Failed item).
- **Footer**: `<dot 6px green>` + "Bridge healthy" — `11.5px / navyMuted`, border-top `1px navyBorder`.

#### Topbar (`54px` tall, navy bg)

- Breadcrumb on the left: `13px / navyText`, with `›` separators in `navyMuted`. On the detail page: `Orders › PO-…` with the PO in mono `12.5px / 500 / #fff`.
- Right cluster: search input (`width: 320px; bg: navySurface; radius: 6; padding: 0 11px`, leading search icon + placeholder + `⌘K` chip), bell icon button (`32×32`, transparent), avatar circle (`30×30`, gradient bg, initials `11px / 600 / #fff`).
- **Underline**: 1.5px **soft** link-spine — `linear-gradient(90deg, transparent, blue@88 30%, green@88 70%, transparent)`. Subtle.

### StatusPill

```ts
<StatusPill status="review" size="sm | md" />
```

Shapes: `inline-flex / gap: 7px / padding: 2-3px 10px / radius: 999px`.
Dot: `7×7px / radius: 999px`.
Font: `11–12px / 500 / lineHeight 1.3`.

| status | dot | text color | bg | border |
|---|---|---|---|---|
| `new` | inkFaint | ink | none | `1px border` |
| `extracting` | blue | ink | none | `1px border` |
| `review` | amber | amber | amberSoft (`#FAF1DD`) | none |
| `ready` | green | ink | none | `1px border` |
| `sent` (delivered) | greenDeep | ink | none | `1px border` |
| `failed` | danger | danger | dangerSoft (`#FAE6E6`) | none |

In other words: amber and red are the only "loud" pill states. Everything else is monochrome with a colored dot.

### Button

| variant | bg | fg | border |
|---|---|---|---|
| `primary` | `navy #0B1A2F` | `#fff` | `navy` |
| `secondary` | `#fff` | `ink` | `borderStrong #CBD0DA` |
| `ghost` | transparent | `inkMuted` | transparent |
| `send` | `green #2E8E3A` | `#fff` | `greenDeep` |
| `danger` | `danger #B43838` | `#fff` | `danger` |

Sizes: `sm 28px / md 32px / lg 38px`. Font weights `500`. Radius `6px`.

Heights and paddings: `sm: 10px`, `md: 14px`, `lg: 16px` horizontal.

---

## Interactions & behavior

- **List → Detail**: click any row anywhere → `router.push('/orders/' + order.id)`. Cursor pointer on the whole row.
- **Detail → List**: "Back to orders" link OR breadcrumb "Orders" in topbar.
- **Search** filters across `po`, `supplier`, `buyer` (case-insensitive substring).
- **Status filter** is a single-select dropdown (`all` + 6 statuses).
- **Sort**: column header click toggles `asc ↔ desc`. Default is `created desc`.
- **Resolve form**:
  - Editing the input cancels the "AI prefill" visual state for that row (text becomes full ink, the `✦ %` hint disappears).
  - The clear button (`×`) empties the input and also cancels the AI state.
  - `Resolve and continue` is disabled when any code is blank.
  - On success: form collapses; a slim green confirmation strip replaces it ("All lines resolved · ready to cross") with `Undo` (ghost) and `Cross the bridge` (primary).
- **No animations beyond `120–250ms` bg transitions on row hover and button press.** No fades, no slides.

## State management

Server state via TanStack Query:
- `useOrders()` — list, includes filter/sort args.
- `useOrder(id)` — detail, includes `lineItems`, `activity`, `savedMappings`.
- `useResolveMutation()` — POST to `/orders/:id/resolve` with `{ codes: { [line]: string }, saveMappings: boolean }`.

Local UI state on the detail page:
- `codes: Record<lineNumber, string>` — controlled inputs in the resolve form.
- `aiAccepted: Record<lineNumber, boolean>` — tracks which inputs still hold the AI suggestion verbatim, for the muted-text rendering.
- `saveMappings: boolean` — the "Save as mappings" checkbox.
- `resolved: boolean` — after a successful mutation, swap the form for the confirmation strip.

URL state:
- List search/filter/sort can stay in route query params (`?q=…&status=…&sort=…`).

---

## Design tokens

All tokens already exist in `design-system/tokens/tokens.css` + `tokens.ts` + `tailwind.config.ts`. Use the existing identifiers — do not redefine.

### Colors used

```
Brand
  --brand-blue        #1E66C9
  --brand-blue-deep   #0F4FA8
  --brand-green       #2E8E3A
  --brand-green-deep  #1E6D29

Chrome (sidebar + topbar only)
  --navy              #0B1A2F
  --navy-surface      #14253D
  --navy-border       #1F3252
  --navy-text         #C8D1E0
  --navy-muted        #7C8DA6

Work-area surfaces
  --bg                #F7F8FA
  --surface           #FFFFFF
  --surface-2         #F1F3F7
  --border            #E5E8EE
  --border-strong     #CBD0DA
  --border-faint      #EEF0F4   /* internal row dividers — softer than --border */

Text
  --ink               #0B1A2F
  --ink-muted         #5E6779
  --ink-faint         #98A0AE

Semantic
  --amber             #B36D14
  --amber-soft        #FAF1DD
  --danger            #B43838
  --danger-soft       #FAE6E6
```

> Note: `--border-faint` is a slightly softer variant of `--border` used inside the table for row separators. If your tokens file doesn't already have it, add it (`#EEF0F4`) — it makes the difference between "list of rows" (right) and "stack of cards" (wrong).

### Typography

Already defined:
- `--font-sans: "Inter"` — body / table / labels / buttons.
- `--font-display: "Bricolage Grotesque"` — `<h1>` page title (28–30px) and the order total (17px). That's it for these screens.
- `--font-mono: "JetBrains Mono"` — PO numbers, SKUs, supplier codes, currencies, file paths, times.

Sizes used on these screens (px, no fluid scaling): `10.5 · 11 · 11.5 · 12 · 12.5 · 13 · 14 · 17 · 28 · 30`.

### Spacing & radii

- Card radius: `10px`.
- Input / button radius: `6px`.
- Chip radius: `3px` (file-type) / `999px` (status pill).
- Padding scale used: `4 / 6 / 8 / 10 / 11 / 12 / 14 / 16 / 18 / 20 / 22 / 24 / 28 / 32 / 36`.

### Shadows

None on these screens. Cards are defined by `1px solid border`, not shadow.

---

## Files in this bundle

- `ProcuLink Orders.html` — entry point (loads React + Babel-standalone + the JSX files).
- `orders/components.jsx` — `T` tokens, `MarkSystem`, `LinkSpine`, `StatusPill`, `Button`, `SrcChip`, `Icon`, `Sidebar`, `Topbar`.
- `orders/screen-orders.jsx` — Orders list screen.
- `orders/screen-order.jsx` — Order detail screen (header, meta strip, resolve form, line items, sidebar cards).
- `orders/data.jsx` — mock data shape (use as reference for the API contract).
- `orders/app.jsx` — top-level router state + Tweaks panel wiring (only useful for the prototype).
- `orders/tokens.css` — copy of the design-system tokens used by the prototype.

To run the prototype locally: open `ProcuLink Orders.html` directly in a modern browser. No build step.

---

## Implementation order (suggested)

1. **App shell** (sidebar + topbar) — likely already exists; just confirm the active-state styling and counts.
2. **Orders list** — table grid, status pill, src chip, status dropdown, search input. Hook up TanStack Query.
3. **Order detail** route — header + meta strip + the three sidebar cards. Render with mock data first.
4. **Line items table** — read-only; the "Needs code" treatment for missing supplier codes.
5. **Resolve form** — the AI-prefill behavior is the subtle bit. Build the muted-text-on-untouched-input visual carefully — it's easy to get wrong.
6. **Resolve mutation** + the confirmation strip swap-in state.
7. Polish: keyboard nav (`↑/↓` in the list, `cmd+Enter` to submit the resolve form), focus rings, responsive header wrap.

---

## Out of scope (do not implement here)

- Upload flow (separate handoff).
- Bridge dashboard / wire topology.
- Source-document viewer ("View source" button can route to a placeholder).
- Bulk actions on the list (checkbox column, multi-select, bulk-resolve).
- Drafts / Sent / All-crossings list variants — same component, different filter.

— end of handoff —
