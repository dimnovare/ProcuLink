## 12. Item Code Mappings — `/library/mappings`

- **File:** `src/app/(app)/library/mappings/page.tsx` (1-liner: renders `<MappingEditor />`)
- **Key components:**
  - `src/components/bridge/MappingEditor.tsx` — the entire page (table + filters + `MappingPanel` modal + `SourceTag` / `Field` / `RequiredField` helpers all live in this one file)
  - `src/components/bridge/layout/PageShell.tsx` — `variant="wide"` page wrapper (max-width `var(--container-wide)` ≈ 1480px, grey `var(--bg)` canvas, gutter 16/24/34px)
  - `src/components/bridge/layout/PageHeader.tsx` — canonical title row ("Mappings" + subtitle + actions slot)
  - `src/components/bridge/BridgeLoader.tsx` — `BridgePageLoader` used by `loading.tsx`
  - `src/hooks/useOrderDirection.ts` — swaps the word "Supplier"→"Customer" for inbound orgs (display only)
- **Capture URL (mock):** `/library/mappings` — in mock mode (`NEXT_PUBLIC_USE_MOCK=true`) the page renders 12 `MOCK_ROWS` immediately with no supplier selection required, so the base table, all overlays and the filter states are reachable from this URL with no ids.

### What it is & why it exists
This is the **Learn** loop made visible: a reference library of buyer-item-code → supplier-item-code translations the engine reuses automatically on every future order so coordinators never re-map the same SKU twice. It is the persistent store behind the per-line code resolution that happens during review/transform. A procurement coordinator opens it to audit which translations exist, fix a wrong code, bulk-import a supplier's price-list mapping as CSV, or export the library for a colleague/ERP. It is not part of a single order's flow — it is the cross-order memory.

### Who uses it & the primary job
**Procurement coordinator** (and occasionally an integration expert seeding mappings up front). The single most important task: **find a mapping and trust/correct it** — either confirm the buyer→supplier code pair is right, or click a row to edit/delete it. The secondary high-value task is **bulk Import** a CSV of code pairs for a supplier.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (grey canvas, vertical flex column):

1. **PageHeader row** — `h1` "Mappings" (Bricolage Grotesque, 28→30px, 600). Subtitle reads `Buyer → supplier item code library · {N} saved` (or, in live mode with no supplier picked, `… · select a supplier to view its mappings`). Right side **actions slot**: a 2-col grid on mobile / inline `flex` on desktop containing **Import** (white outline, upload icon) and **Add mapping** (buyer-**blue** `#1E66C9` filled, plus icon; label collapses to "Add" under `sm`). Both are 40px tall on mobile, 34px on `lg`.
2. **Toolbar row** (on the grey canvas, above the card) — a `flex-col → lg:flex-row` strip:
   - Result count: `Showing {filtered} of {total}` (bold ink numbers) — or "No supplier selected".
   - A spacer `flex-1`.
   - **Source filter chips**: `All · AI · Manual · Imported · Inherited` (horizontally scrollable on mobile, scrollbar hidden). Active chip = green-soft fill + green text + green-tinted border.
   - **Supplier route `<select>`** ("All suppliers" default + one option per supplier).
   - **Search input** (300px on `lg`) with a `⌕` glyph prefix; green focus ring.
   - **Export** ghost button (white, grey text) — the design header has none, kept reachable here.
3. **Notice banner** (conditional) — green-soft rounded strip for local/demo confirmations.
4. **Table card** — a white `rounded-[10px]` card with `1px #E5E8EE` border + faint shadow, in a scroll area. It contains exactly one of: the *select-a-supplier* prompt, the *loading skeleton*, the *desktop table / mobile card list*, or an *empty/no-match* state.

**Desktop table** (`min-w-[760px]`, `12.5px`): sticky white header, 6 columns —
`Buyer | Buyer code | Supplier | Supplier code | Source | Used` (Used right-aligned). Header labels are 10.5px uppercase, `tracking-0.07em`, `var(--ink-faint)`. Rows are full-width clickable (`cursor-pointer`, hover `#F7FAFD`), `px-4 py-3.5`. Buyer name + buyer code are **blue** (`#0F4FA8` link / `#0B1A2F` mono code); supplier name + supplier code are **green** (`#1E6D29`). `Used` is a mono `{n}×`.

**Mobile card list** (`md:hidden`): each row becomes a tappable card — buyer name (blue) + SourceTag on the top line, then `buyerCode → supplierCode` with an arrow glyph, then supplier name (green) + `{used}×`.

Density/type/spacing observations: the page is heavily **inline-styled with hard-coded hex constants** (a ~20-line `BLUE/GREEN/…` palette block at the top) and **hard-coded px font sizes** (12 / 12.5 / 13 / 18 / 28px) rather than design tokens. Control heights drift (34px desktop vs 40px mobile vs 30px chips vs 9px-padding rows). Numbers are mono but the page does not request `font-variant-numeric: tabular-nums`.

### Data shown
**Entity:** `SupplierMapping` (`src/types/procurement.ts`) — `{ id, buyerItemCode, supplierItemCode, confidence?, source? }`. The component maps it into a local `MappingRow { id, buyer, buyerCode, supplier, supplierCode, source, used? }`.

- **Columns displayed:** Buyer (org name — **always blank `"—"` from the API mapper, only populated in mock**), Buyer code, Supplier (the selected supplier's name), Supplier code, Source (AI / Manual / Imported / Inherited, derived from `m.source` = `suggested`→AI / `imported` / `inherited` / else Manual), Used (mock-only count; never returned live).
- **Source of data:**
  - Mock mode → `MOCK_ROWS` (12 hard-coded rows in `MappingEditor.tsx`).
  - Live mode → `apiClient.getSupplierMappings(supplierId)` → `GET /api/suppliers/{id}/mappings`. The query is `enabled` only when a supplier is selected (no cross-supplier list endpoint exists). Supplier list comes from `apiClient.getSuppliers()`.
  - Mutations: `createSupplierMapping` → `POST /api/suppliers/{id}/mappings`; `updateSupplierMapping` → `PUT …/mappings/{mappingId}`; `deleteSupplierMapping` → `DELETE …/mappings/{mappingId}`; `importSupplierMappings(file)` → `POST …/mappings/import` (multipart `file`), returns `{ created, updated }`; export is built client-side (CSV blob `buyer_code,supplier_code`).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Import** button (header, outline) | `openPanelForSupplier("import")` | Opens the modal in import mode; if no supplier selected, silently selects the first supplier (or shows a green notice "Add a supplier before saving…") |
| **Add mapping** button (header, blue primary) | `openPanelForSupplier("add")` | Opens the modal in add mode (same first-supplier fallback) |
| **Source filter chips** (All / AI / Manual / Imported / Inherited) | `setSrc(s)` | Filters `filtered` rows in place; active chip turns green-soft |
| **Supplier route `<select>`** | `setSelectedSupplierId` + `setRoute` | Switches which supplier's mappings the live query fetches; "All suppliers" clears selection → shows the select-a-supplier prompt (live mode) |
| **Search input** | `setSearch` | Live client-side filter across buyer/buyerCode/supplier/supplierCode |
| **Export** button (toolbar, ghost) | `openPanelForSupplier("export")` | Opens the modal in export mode |
| **Table row click** (desktop) | `setPanel({ kind: "edit", row })` | Opens the edit modal pre-filled with that row |
| **Mobile card tap** | same `{ kind: "edit", row }` | Opens the edit modal |
| **Modal: Choose file** label/input | `setImportFile(file)` | Stores selected CSV; label text becomes the file name |
| **Modal: Buyer item code / Supplier item code** inputs | `setBuyerCode` / `setSupplierCode` | Edits the pair; both required (mono, `*`) |
| **Modal: primary button** ("Save mapping" / "Validate import" / "Export CSV") | `handleAction()` | Calls the matching API mutation, invalidates `["supplier-mappings", supplierId]`, closes + sets a notice |
| **Modal: Delete** (edit only, left, red outline) | inline `deleteSupplierMapping` | Deletes the mapping, invalidates query, closes with "Mapping deleted." |
| **Modal: Cancel** / **× Close** | `onClose()` | Closes the modal, restores focus to trigger |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Mapping panel — Add** | Modal (`role="dialog" aria-modal`, scrim `#0B1A2F66`; bottom-sheet on mobile, centered 600px card on `sm+`) | Header **Add mapping** button | Blue link-icon eyebrow + title "Add item code mapping"; read-only **Buyer** ("All buyers") + required **Buyer item code** input; read-only **Supplier** (route) + required **Supplier item code** input; blue info banner ("Saved mappings are reused automatically…"); footer Cancel + green **Save mapping** | × button · Cancel · **Esc** · backdrop click · successful Save (`onDone`) |
| **Mapping panel — Edit** | Modal (same shell) | Row click (desktop) / card tap (mobile) | Same code form pre-filled with the row's codes; footer adds a left-aligned red **Delete** button | × · Cancel · Esc · backdrop · Save (update) · Delete |
| **Mapping panel — Import** | Modal (same shell) | Header **Import** button | Title "Import mappings"; green dashed **Drop CSV here** dropzone + "Choose file" file input (accepts `.csv`); helper noting expected `buyer_code, supplier_code` columns; grey info note; footer Cancel + green **Validate import** (disabled until a file is chosen) | × · Cancel · Esc · backdrop · successful import (`Imported: N created, M updated`) |
| **Mapping panel — Export** | Modal (same shell) | Toolbar **Export** button | Title "Export mappings"; read-only **Supplier** context field (the route); blue info banner ("Downloads this supplier's mappings as a CSV…"); footer Cancel + green **Export CSV** (triggers a client-side blob download) | × · Cancel · Esc · backdrop · successful export |
| **Notice banner** | Inline strip (not an overlay) | Any `onDone(message)` or the no-supplier guard | Green-soft strip with a one-line confirmation/error | Replaced/cleared on next panel open (`setNotice(null)`) |

There is **one modal component** (`MappingPanel`) reused in four `kind` modes. It is a hand-rolled fixed-overlay div (not shadcn `Dialog`) but implements its own focus trap, Escape handler, autofocus of the first field, and focus restore on close. There are **no toasts** — all feedback is the inline notice strip. There are no dropdown menus, popovers, or row-action kebabs.

### States
- **Empty (live, no supplier chosen):** dedicated centered prompt inside the card — `⇅` glyph, "Select a supplier to view its mappings", explanatory copy. This is the default live landing because mappings are per-supplier.
- **Empty (supplier chosen, zero mappings):** `⊘` glyph, "No item mappings yet" + "Add mappings to automatically translate your buyer item codes to supplier item codes."
- **Empty (filter excludes all):** `⊘` glyph, "No mappings match your filter".
- **Loading (route-level):** `loading.tsx` → `BridgePageLoader label="Loading mappings…"` (animated blue→green wire mark, reduced-motion frozen).
- **Loading (in-card, after picking a supplier):** 3 grey skeleton bars (`h-9`, `#F0F2F6`) inside the card while `mappingsLoading`.
- **Error:** **Not handled at the list level** — `useQuery` exposes `isLoading` only; a failed `getSupplierMappings` shows no reason/retry (falls through to `liveRows ?? []` → looks like an empty supplier). Modal mutations **do** surface errors: a red `#FBE3E3` strip above the footer ("Delete failed", server message, "Choose a CSV file first.").
- **Success/feedback:** green inline notice strip ("Mapping saved." / "Mapping updated." / "Mapping deleted." / "Imported: N created, M updated." / "Export downloaded."). In mock mode the modal short-circuits to local-only messages.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full 6-col table inside the wide (≈1480px) card; header actions + toolbar inline on one row; search 300px. Table can `overflow-x` below 760px content width.
- **Tablet 768:** still the desktop table (`md:` breakpoint = 768, so the table shows from 768 up); toolbar starts wrapping at `lg` so chips/select/search/Export may stack into multiple rows between 768–1024px.
- **Mobile 390:** header actions become a 2-col grid (Import | Add); "Add mapping" → "Add"; the table is replaced by the **stacked card list**; filter chips scroll horizontally; select/search/Export go full-width and stack; the modal becomes a **bottom sheet** (`items-end`, `rounded-t-[12px]`, `max-h-92vh`).
- **Known cliffs:** between 768 and ~1024 the toolbar wraps to 2–3 rows (chips + select + search + Export) which is dense/awkward; the `<select>` and `Export` competing for width at `lg` is the tightest point.

### Current UX issues
- **No status-badge system parity (Bar 4).** `SourceTag` is a bespoke pill (`rounded-[6px]`, 11px, hand-picked hex per source) — not the app's one status-badge component, and it carries meaning by colour + word but with its own shape/padding distinct from every other pill in the app.
- **Tokens bypassed (Bars 1, 2, 8).** The whole file is inline `style={{}}` with a private hex palette and raw px font sizes; this drifts from `globals.css`/Tailwind tokens and from the navy/violet system the rest of the app uses. Control heights are inconsistent (34/40/30px; row `py-3.5`; chip `h-9/h-[30px]`).
- **No tabular figures (Bar 3).** `Used` counts and codes are mono but not `tabular-nums`; the right-aligned `Used` column will jitter (e.g. `9×` vs `312×`).
- **Two primary actions compete (Bar 7).** "Add mapping" is blue and "Save mapping"/Import are green — there is no single dominant green primary on the list screen; the header primary is blue, which conflicts with the app's green=primary/commit convention.
- **Error state missing on the data surface (Bar 6).** A failed mappings fetch is invisible — it reads as an empty supplier with no reason/retry. "Never show healthy when something is failing" is violated by silence.
- **Buyer column is dead in live mode.** `apiMappingToRow` always sets `buyer: ""`, so the live desktop table's first column is always `—`; a whole column carries no information except in mock data.
- **"Used" column is mock-only.** Live rows never have `used`, so the column is permanently `—` in production — a column promising reuse evidence that shows nothing real.
- **`<select>` + `⌕`-glyph search look unfinished.** A native `<select>` with `appearance-none` and no chevron, plus a Unicode `⌕` instead of a Lucide `Search` icon, read as placeholder UI next to the polished header.
- **Empty-supplier UX is a dead-end on a per-supplier model.** The default live state ("All suppliers") shows a prompt with no inline supplier picker in the empty card — the only way forward is the toolbar `<select>` higher up.
- **Backdrop-click closes a form modal without confirm.** Clicking the scrim discards unsaved Add/Edit input silently; destructive (Delete) is in the same footer as Cancel/Save without a confirm step.
- **Icon-only × and Export lack consistent affordances.** `×` has `aria-label` (good) but is a tiny 32px control; the toolbar Export ghost button is visually identical-weight to the source chips, muddying hierarchy.

### Redesign recommendations (for Claude Design)
1. **Adopt the one status-badge component for Source** (Bar 4) — same shape/size/padding as the rest of the app, with the existing AI=violet / Manual=neutral / Imported=green / Inherited=blue semantics expressed via tokens, each with a small Lucide icon (sparkle off — keep "AI" word). Keep navy/violet brand.
2. **Replace the inline hex palette with design tokens** (Bars 1, 2, 8) — drive every colour/size from `globals.css`/Tailwind so this page matches the navy `#0B1A2F` + violet system; normalize to ONE control height (recommend 36–40px) and the 4/8px spacing scale; one card radius/border/shadow tier.
3. **Add a real ERROR state to the table card** (Bar 6) — surface `getSupplierMappings` failures with a reason + Retry button instead of a silent empty supplier; never render an empty library when the fetch actually failed.
4. **Make `Used` (reuse count) real and tabular** (Bar 3) — have the API return per-mapping usage and right-align with `tabular-nums` so the column is the trust signal it implies; if usage isn't available, drop the column rather than show permanent `—`.
5. **Resolve the primary-action conflict** (Bar 7) — make ONE dominant green primary. Since the page's core verb is "add/save a mapping", consider green for "Add mapping" (≥44px) and demote Import/Export to outline/ghost; keep buyer=blue / supplier=green only inside the table semantics, not on the global CTA.
6. **Fix the live Buyer column** — either populate buyer name from the API or remove the column in live mode so the table doesn't lead with a dead `—` field; lead each row with the human-meaningful pair (buyer code → supplier code).
7. **Upgrade the toolbar controls** — replace the native `<select>` with a styled supplier combobox (chevron, search) and the `⌕` glyph with a Lucide `Search`; give the toolbar a single rhythm so it collapses cleanly at 768–1024 instead of wrapping into 3 rows (the current cliff).
8. **Put a supplier picker in the empty state** — in the "Select a supplier" card, embed the picker/CTA inline so the next action is in the empty surface (Bar 6's "next action"), not only in the toolbar above.
9. **Guard destructive + unsaved actions** — separate Delete from Cancel/Save with a confirm step; warn (or block) backdrop-dismiss when the Add/Edit form is dirty. Keep Esc/× as the explicit closers and animate the sheet from the trigger.
10. **Make the Import modal a proper dropzone** — wire real drag-and-drop onto the green dashed area (currently the box is decorative; only the file input works), show the parsed row count preview before commit, and keep the write-only nature clear ("existing codes updated, new added").
