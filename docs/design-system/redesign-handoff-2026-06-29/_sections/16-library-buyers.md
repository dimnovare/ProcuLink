## 16. Buyers (reference library) — `/library/buyers`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/buyers/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (wide variant, 1480px container)
  - `src/components/bridge/layout/PageHeader.tsx` (title "Buyers" + count subtitle + actions slot)
  - `src/components/bridge/layout/Card.tsx` (create panel + table card)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile stacked card row)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (variant `blue`, which is actually green)
  - `src/components/bridge/EmptyState.tsx` → `MarkSystem` (`src/components/bridge/MarkSystem.tsx`)
  - `src/components/bridge/BridgeLoader.tsx` → `BridgePageLoader` (route `loading.tsx`)
  - Local in-file helpers: `BuyerIcon`, `ChannelPill`, `SkeletonTrow`, `SkeletonCard`, `MobileField`
- **Capture URL (mock):** `/library/buyers` (the page reads `isApiMockMode`; in mock mode it renders 3 seeded buyers — `Example Buyer 1 / 2 / 3` from the in-file `MOCK_BUYERS`, NOT the api-client mock names). No detail route exists; rows navigate to `/inbox?buyer=HEI` etc.

### What it is & why it exists
This is the **Buyers reference list** — the directory of the organizations that *send* ProcuLink purchase orders (ProcuLink runs inbound: a buyer is the upstream party whose POs land in the inbox). It is the "learn" / setup memory of the loop: each buyer is the entity ProcuLink fingerprints a layout against so it can auto-parse the next PO from that sender. A procurement coordinator opens it to register a new sender ("a buyer that sends you purchase orders"), to see at a glance how many orders each buyer has sent and in what format, or to jump into the inbox filtered to one buyer's orders.

### Who uses it & the primary job
**Procurement coordinator / operator.** The single most important task is **registering a new buyer** (name + short code) so ProcuLink can start recognising that sender's PO layout — the page's intro note states "After creating, upload a sample PO and ProcuLink learns the buyer's layout automatically." Secondary jobs: scan order volume per buyer and drill into the inbox for one buyer.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px, gutter 16→24→34px, vertical pad 20→28px, background `var(--bg)`):

1. **PageHeader** — `h1` "Buyers" (Bricolage Grotesque, 28→30px, weight 600, letter-spacing -0.02em) over a 13px muted subtitle that doubles as a count: `"3 buyers · where every order starts"` (or `"Loading…"` while fetching). Right-aligned **action slot** holds the single primary button.
2. **Primary action button** — `Button variant="blue" size="md"` labeled **"New buyer"** with a plus icon. It is a TOGGLE: clicking flips `addOpen`; while the panel is open the same button relabels to **"Cancel"**. Note: `variant="blue"` resolves to brand-GREEN (`#2E8E3A`) per `DSPrimitives` BUTTON_VARIANT — the only blue here is the icon tiles, not the button.
3. **Create-buyer panel** (conditional, `addOpen`) — a `Card` (`mb-[18px]`, 18px padding, 8px radius, 1px border, `--shadow-card`). Contains: panel title "New buyer" (15px/600) + sub "A buyer that sends you purchase orders"; a row that is `flex-col gap-3` on mobile and `sm:flex-row sm:items-end` on desktop with **Buyer name** input (flex:1), **Short code** input (120px, mono, auto-uppercased, maxLength 10), and a green **"Create buyer"** button (full-width on mobile); a buyer-blue info callout ("After creating, upload a sample PO and ProcuLink learns the buyer's layout automatically."); and an inline error paragraph when validation/mutation fails.
4. **Table card** — a single `Card` with `overflow-hidden !p-0` wrapping two mutually-exclusive renderings:
   - **Desktop/tablet (`hidden sm:table`)**: a real `<table>` with `<colgroup>` widths — Buyer (fluid), Primary format (170px), Orders all time (150px), Last order (130px), chevron (44px). Header row is 10.5px uppercase 0.06em-tracked `--ink-faint` labels with a `--border` bottom rule. Body rows are 14px/18px padded, full-row clickable, hover-tinted `--brand-blue-soft`.
   - **Mobile (`sm:hidden`, `flex-col gap-2 p-3`)**: `MobileListRow` cards — identity row (icon + name + mono code + delete button) plus a 2-column grid of labelled fields (Primary format / Orders all time / Last order).

There is no toolbar, no search, no filter, no sort, no pagination, and no footer/action bar.

### Data shown
**Entity:** `BuyerDto` (`src/types/procurement.ts`): `id`, `name`, `code`, `orderCount: number`, `lastOrderAge: string | null`, `formats: string[]`.

**Columns / fields rendered:**
- **Buyer** — soft-blue 32px rounded icon tile (building glyph) + buyer `name` (13.5px/600) + `code` below (10.5px mono, grey `#9196A5`).
- **Primary format** — `formats[0]` rendered as a `.chip` (`ChannelPill`: 22px tall, surface-2 fill, `--ink-muted` text); em-dash if `formats` is empty.
- **Orders (all time)** — `orderCount.toLocaleString()`, right-aligned, mono, 15px/600.
- **Last order** — `lastOrderAge` relative string (e.g. "2m", "14m", "1h"); em-dash + fainter color when null.
- **chevron** — resting right chevron (`#A4ADBD`); a delete (×) button is revealed on row hover just left of it.

**Source:** `getBuyers()` in `src/lib/api-client.ts` → `GET /api/buyers`. Mutations: `createBuyer(name, code)` → `POST /api/buyers`; `deleteBuyer(id)` → `DELETE /api/buyers/{id}`. The query is `enabled: !isApiMockMode`; in mock mode the page substitutes the **in-file** `MOCK_BUYERS` array (Example Buyer 1/2/3), which differs from the api-client's own mock (Heinrich Industries / Nordmark / Steelhouse). Note: `updateBuyer(id, name, code)` → `PUT /api/buyers/{id}` EXISTS in the api-client but is **not imported or used** by this page — there is no edit affordance.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| "New buyer" / "Cancel" button (header) | `setAddOpen(v => !v)` + clears error | Toggles the inline create panel open/closed |
| Buyer name input | `setAddName`; on focus paints blue ring (inline style), Enter triggers save | Local state; no autosave |
| Short code input | `setAddCode(value.toUpperCase())`, maxLength 10, mono; Enter triggers save | Local state; force-uppercased |
| "Create buyer" button (panel) | `handleSaveAdd()` → validates non-empty name+code → `createMut.mutate` | On success: invalidates `["buyers"]`, closes panel, clears fields. On error: sets `addError` |
| Table row (desktop) | `router.push(\`/inbox?buyer=${b.code}\`)` | Navigates to **inbox filtered by buyer code** (title tooltip: "Filter inbox to orders from this buyer") |
| MobileListRow (mobile) | same `router.push(/inbox?buyer=…)`; Enter/Space keyboard-operable | Navigates to filtered inbox |
| Row delete (×) button | `handleDelete` → `e.stopPropagation()` → `window.confirm(...)` → `deleteMut.mutate(id)` | Native browser confirm; on confirm deletes + invalidates list |
| Chevron icon | decorative only (no own handler; row click handles nav) | — |
| "Retry" link (error state) | `refetch()` | Re-runs the buyers query |
| EmptyState "New buyer" action | `setAddOpen(true)` | Opens create panel |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Create-buyer panel | Inline panel (in-flow `Card`, NOT a modal/drawer) | "New buyer" header button, or the EmptyState "New buyer" action | Title + sub, Buyer name input, Short code input, "Create buyer" button, blue info callout, inline error text | "Cancel" (same toggle button), or a successful create (`onSuccess` sets `addOpen=false`). **No X, no Esc, no backdrop** — there is no scrim and Esc does nothing. |
| Delete confirmation | Native `window.confirm()` dialog | Row delete (×) button (desktop hover-revealed; mobile always-visible) | Browser-chrome text: `Delete buyer "{name}"? This cannot be undone.` | OK (proceeds to delete) / Cancel (aborts) — browser-controlled, not styled |
| Row delete (×) button itself | Hover-revealed inline control (desktop) | Hovering a desktop table row (`opacity 0→1`, `pointerEvents` toggled) | Single × icon button | Mouse leaves the row (fades back to opacity 0) |

There are **no toasts, no drawers, no sheets, no popovers, no dropdown menus, and no styled modals** on this page. The only true overlay is the unstyled native `window.confirm`. All other "transient" surfaces are in-flow (the create panel) or hover-state reveals. Success/failure of create + delete is communicated only by list refresh and (for create errors) an inline `<p>` — there is no toast confirming "Buyer created" or "Buyer deleted."

### States
- **Empty:** Handled — when `buyers.length === 0` (and not loading/error) the `EmptyState` renders inside the table card: ProcuLink mark, Bricolage title "No buyers yet", sub "A buyer is an organization that sends you purchase orders, in whatever format they use.", and a navy "New buyer" action button (note: this empty-state button is **navy `#0B1A2F`**, inconsistent with the green header "New buyer").
- **Loading:** Two layers. (1) Route-level `loading.tsx` → `BridgePageLoader label="Loading buyers…"` (animated buyer→supplier wire mark over `#F6F7FA`). (2) In-page skeletons while the query is loading and not in mock mode: 3× `SkeletonTrow` (desktop) / 3× `SkeletonCard` (mobile), pulsing `--surface-2` bars. The header subtitle shows "Loading…".
- **Error:** Handled — when `isError` and not mock, the table body shows a centered single-cell (`colSpan=5`) "Failed to load buyers. **Retry**" with a brand-blue text button calling `refetch()`; mobile shows the same message. No error reason/detail is surfaced beyond "Failed to load buyers."
- **Success/feedback:** Minimal — create success silently closes the panel + clears fields + refetches; delete success silently refetches. Create errors show an inline red `<p>` (and inline "Name/Code is required." validation). The create button shows a spinner + "Creating…" while pending. There is **no positive toast/confirmation** for either action.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered at 1480px max-width. Full data table; chevron column 44px; delete × hidden until row hover.
- **Tablet 768:** Still the `sm:table` desktop table (breakpoint is `sm` = 640px, so 768 already shows the table). Create panel goes horizontal (`sm:flex-row`), inputs shrink to 34px height; short-code field fixed 120px.
- **Mobile 390 (< 640px):** Table swaps to stacked `MobileListRow` cards (`sm:hidden`), each with a 2-column field grid; delete × is always visible (40px tap target) instead of hover-revealed; create-panel row stacks vertically with full-width inputs (44px tall) and a full-width green button; header action button wraps below the title.
- **Breakpoint cliffs:** The single `sm` (640px) breakpoint is the only switch — there is no intermediate density between 640px and 1480px, so on a 1440px screen the table is very sparse (5 columns, lots of whitespace, a 44px-wide trailing chevron column). The header→table transition is clean; no known broken state.

### Current UX issues
- **Misleading primary-button color (DESIGN BAR #4/#7):** The header "New buyer" uses `variant="blue"` which actually renders **green** (`#2E8E3A`), while the EmptyState's "New buyer" renders **navy** (`#0B1A2F`). Two different "New buyer" buttons in two different colors for the identical action.
- **Pervasive magic-number sizing breaks the 4/8 rhythm (DESIGN BAR #1):** Fractional pixel sizes everywhere — `fontSize: 12.5 / 13.5 / 11.5 / 10.5`, `height: 34`, gaps of 13px, padding `14px 18px`, input height `34px`. None of this is on the 4/8 scale; type sizes drift across the page.
- **Type hierarchy carried partly by color and ad-hoc sizes (DESIGN BAR #2):** Header labels are `--ink-faint` uppercase 10.5px; the buyer code is `#9196A5` (a hardcoded grey literal, no token) — likely below 4.5:1 on white. Three named literals (`CODE_GREY`, `BORDER_STRONG`, `CHEVRON`) exist precisely because they have no design token.
- **No styled confirm / destructive pattern (DESIGN BAR: confirm-before-destroy):** Delete uses raw `window.confirm` — unbranded, not focus-trapped, and inconsistent with the rest of the app. Destructive action isn't visually separated; it sits inline next to the navigational chevron.
- **Inline-style focus rings instead of `:focus-visible` (DESIGN BAR #9):** Inputs paint their focus ring via `onFocus`/`onBlur` inline style mutation, not a CSS `:focus-visible` rule; row hover is JS state (`hoverRow`) rather than CSS `:hover`, and there is no visible keyboard focus state on table rows (only mobile cards are keyboard-operable).
- **No table affordances (DESIGN BAR #5):** No sortable headers / `aria-sort`, no sticky header, no zebra/row-rule consistency beyond a flat bottom border, no pagination — fine for 3 buyers, weak at scale.
- **No edit path:** `updateBuyer` exists in the API but the UI offers create + delete only. A user who mistypes a name/code must delete and recreate (the FOCUS HINT's "edit buyer panel" is not implemented).
- **No positive feedback (DESIGN BAR #6):** Create and delete give no toast/inline success; the only signal is the list quietly changing.
- **"Primary format" is a single value with no meaning of "primary":** It just shows `formats[0]`; multiple formats are hidden, and there's no tooltip/expansion.
- **Inline `style={{}}` everywhere** instead of the design-system tokens/classes — the page largely bypasses the shared primitives' styling, making it the odd-one-out vs. neighbouring library pages.

### Redesign recommendations (for Claude Design)
1. **Unify the create surface and add edit (highest impact).** Make "New buyer" open ONE styled overlay (a right `Sheet`/`Drawer` or a centered `Dialog`) with scrim, X, Esc-to-close, and focus trap — reused for **both create and edit** (wire the existing `updateBuyer`). Add a row "Edit" action (kebab menu or pencil) alongside delete. Keep buyer-blue accent for the buyer entity.
2. **One primary-action color, one button.** The header CTA and the empty-state CTA must be the same green primary (`--brand-green`, ≥44px, dominant). Stop using `variant="blue"`/navy for the same "New buyer" action; demote any secondary to outline/ghost.
3. **Replace `window.confirm` with a branded destructive confirm dialog** (red confirm, named buyer, "cannot be undone", focus-trapped), and add a success toast for create/delete/edit. Separate the destructive control from the navigational chevron.
4. **Normalize the type + spacing scale.** Collapse 10.5/11.5/12.5/13.5px into the system scale (e.g. label 12/500, body 13/400, heading 600), put all padding/gaps on 4/8 (e.g. 16px cell padding, 8px gaps), and replace the three hardcoded grey literals (`CODE_GREY`, `BORDER_STRONG`, `CHEVRON`) with tokens that meet 4.5:1.
5. **Tabular figures + real table affordances (DESIGN BAR #3/#5).** Keep mono/`tabular-nums` on `orderCount` (good already) and apply it to `lastOrderAge`; add sortable headers with `aria-sort` (sort by orders / last activity / name), a sticky header, low-contrast `gray-200` row rules, and a consistent single row height.
6. **CSS-driven states over JS hover (DESIGN BAR #9).** Move row hover to CSS `:hover`, add a visible `:focus-visible` ring to table rows (make rows keyboard-operable like the mobile cards), and convert input focus rings to a CSS rule rather than inline `onFocus` mutation.
7. **Make "Primary format" honest.** Either show all `formats` as a chip cluster (capped + "+N") using the canonical `SrcChip` palette, or label the column "Formats" — and surface the standards mapping per the standards-visibility rule when relevant.
8. **Add a search/filter toolbar** above the table for when buyer count grows (name/code search), with the same density as other library pages.
9. **Strengthen the empty + first-run story.** The empty state is good; add a secondary "Upload a sample PO" hint that mirrors the create-panel callout, so the learn-loop intent ("ProcuLink learns the buyer's layout") is reinforced at zero state.
10. **Drop inline styles for shared primitives.** Re-base the table/cards on the canonical `Card`, token spacing, and a shared list/table component so this page matches `/library/suppliers` and the rest of the library cluster.
