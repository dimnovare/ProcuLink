## 14. Rule definitions (validation rule catalog) — `/library/rule-definitions`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/rule-definitions/page.tsx` (thin wrapper — renders `<RuleDefinitionsCatalog />`)
- **Key components:**
  - `src/components/bridge/RuleDefinitionsCatalog.tsx` (the entire page: header, context banner, loading/error/empty states, grouped catalog, the `RuleDefinitionRow` + `SeverityPill` sub-components)
  - `src/components/bridge/StandardsRefList.tsx` (the "Maps to" standards-reference grid + `hasStandardsRefs()` guard, rendered inside an expandable row panel)
  - `src/components/bridge/layout/PageShell.tsx` (wide variant, 1480px max-width, full-height scroll)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle row)
  - `src/components/bridge/layout/Card.tsx` (used for error + empty states)
  - `src/hooks/useQueriesEnabled.ts` (auth/mock query gate)
- **Capture URL (mock):** `/library/rule-definitions` (no ids/query — single static route; mock returns 3 definitions via `mockListRuleDefinitions`)

### What it is & why it exists
This is the org-wide, **read-only** catalog of reusable validation rule *definitions* — the building blocks (e.g. "Order currency is present", "Every line has a supplier item code", "Quantity greater than zero") that a supplier's executable acceptance rules bind to. It sits in the **validate** stage of the parse → normalize → validate → review → transform → deliver → learn loop: it is the reference shelf, not the enforcement surface. Each definition also carries the standards reference it maps to (UBL / EDIFACT / X12 / cXML), which satisfies the project's standards-visibility rule so a 30-year procurement veteran can confirm "this currency check is `cbc:DocumentCurrencyCode` / `CUR02` / `Total/Money/@currency`."

### Who uses it & the primary job
Primarily the **integration expert / operator** setting up suppliers (a procurement coordinator may skim it for orientation). The single most important task is **reference/orientation**: browse the available checks grouped by scope, confirm what each rule means and which standard field it maps to, then go author/enforce them on a specific supplier's Validation rules tab. There is no create/edit/delete here by design — authoring lives per-supplier.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px; responsive gutter `px-4 sm:px-6 lg:px-[34px]`, vertical `py-5 sm:py-7`):

1. **PageHeader** — `h1` "Rule definitions" (Bricolage Grotesque, 28→30px, weight 600, `-0.02em`). Subtitle is a single 13px muted line concatenating a static sentence + a live count: `"Your reusable validation rule catalog, with the standard each field maps to."` then `"{N} definition(s)"` (joined by a double-space; count omitted while loading/error). No `actions` slot — header is title-only.
2. **Read-only context banner** — a full-width info strip (`mb-4`, `rounded-[8px]`, `px-3.5 py-2.5`, 12px text, border `#D6E2F4`, background `--brand-blue-soft`). Bold lead "Built-in rule definitions." + muted sentence with an inline underlined link **"supplier's Validation rules tab"** → `/library/suppliers`. Stacks vertically on mobile, row on `sm`.
3. **Body — grouped catalog** (`grid gap-4`): one block per scope, ordered **Order-level → Line-level → Header → (other scopes alphabetical)**. Each group block has:
   - A group heading row (`mb-2`): `h2` scope label (13px, weight 600) + a faint count number.
   - A single bordered surface card (`rounded-[12px]`, `--surface`, `1px --border`, `--shadow-card`, `overflow-hidden`) containing the rows for that scope. Rows are divided by a hardcoded `1px solid #F0F2F6` bottom border (NOT a token).

**Row anatomy** (`RuleDefinitionRow`, `flex items-start gap-3 px-4 py-3`):
- Left/main column: title (13px, weight 600, `--ink`) + inline **SeverityPill** + optional **System** chip; second line shows mono `code` · mono `fieldPath operator [expectedValue]` (e.g. `quantity greater_than 0`); optional description paragraph (12px, `--ink-muted`).
- Right: a **Standards** toggle button (28px tall, info-circle Lucide-style icon + word) — only rendered when at least one standards ref is present.
- Expanded panel (when toggled open): a nested `--surface-2` box (`rounded-[8px]`) with an uppercase "Maps to" label and the `StandardsRefList` definition list (`cXML 1.2 / UBL 2.1 / EDIFACT / X12` → mono ref values in a two-column `dl`).

Density/type/spacing observations: heavy reliance on inline `style={{...}}` with **fractional pixel font sizes** (`13px`, `12.5px`, `11.5px`, `10.5px`, `10px`) and CSS-var colors rather than the Tailwind token scale; padding/gaps mix the 4/8 scale (`px-4 py-3`, `gap-3`) with off-scale values (`py-0.5`, `mt-0.5`, `gap-y-0.5`, `pb-3.5`, `px-3.5`).

### Data shown
Entity: **`RuleDefinition`** (mirrors backend `RuleDefinitionDto`). Source: `listRuleDefinitions` → mock `mockListRuleDefinitions` (200ms delay, returns `MOCK_RULE_DEFINITIONS`, 3 rows) or real `GET /api/rule-definitions` (org-scoped server-side; `RuleDefinitionsController.cs`).

Fields displayed per row:
- `title` (e.g. "Order currency is present")
- `defaultSeverity` → SeverityPill (`error` / `warning` / `info`; unknown falls back to neutral)
- `isSystem` → "System" chip when true
- `code` (mono, e.g. `ORDER.CURRENCY.REQUIRED`)
- `fieldPath` + `operator` + `defaultExpectedValue` (mono, e.g. `currency required`, `quantity greater_than 0`)
- `description` (optional paragraph)
- Standards refs `ublRef` / `edifactRef` / `x12Ref` / `cxmlRef` (in the expanded panel)
- `scope` (used for grouping; not shown as a field, surfaced as the group heading)

Fields present in the type but **not displayed**: `id` (key only), `paramHint`, `createdAt`. Mock ids are `rd-1`, `rd-2`, `rd-3`. Mock data: `rd-1` order/currency/required/error; `rd-2` line/supplierItemCode/required/error; `rd-3` line/quantity/greater_than/0/error — so the mock renders an **Order-level** group (1) and a **Line-level** group (2), all severity = error. (A sibling `getSupplierRuleBindings` / `GET /api/suppliers/{id}/rule-bindings` exists for the supplier authoring surface but is NOT used on this page.)

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| "supplier's Validation rules tab" link (context banner) | `next/link` navigate | `/library/suppliers` |
| **Standards** toggle button (per row) | `onClick` toggles local `open` state (`aria-expanded`) | Expands/collapses the inline "Maps to" standards panel below that row. In-place, no navigation. Only present when `hasStandardsRefs()` is true. |
| **↻ Retry** button (error state only) | `refetch()` the TanStack query | Re-runs `listRuleDefinitions`; replaces error with data on success |

No sort controls, no filters, no search, no row click-through, no create/edit/delete, no menus. The page is browse-only.

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Standards "Maps to" panel | Inline expand panel (NOT an overlay — in-flow, per row) | The per-row **Standards** toggle button | `--surface-2` box with "Maps to" label + `StandardsRefList` (cXML 1.2 / UBL 2.1 / EDIFACT / X12 → mono ref values) | Clicking the same **Standards** button again (toggles `open` back to false). No Esc/backdrop — it is in-flow content, not a popover. |

**No true overlays** — the page itself opens NO modal/drawer/sheet/dialog/popover/dropdown/tooltip/toast. The only transient surface is the inline per-row standards disclosure, which is in-document flow (it pushes siblings down), not a floating layer. Navigation to `/library/suppliers` is a plain in-place route change. (The global app shell can open the `HelpSlideover` / SectionGuide drawer for this route from the topbar, but that is owned by the shell, not triggered by anything on this page.)

### States
- **Empty:** Handled. When `total === 0`, renders a `Card` with centered "No rule definitions yet" (16px Bricolage, weight 600) + muted explainer "Your org has no reusable validation rule definitions. They appear here once defined." **Weakness:** no next-action CTA in the empty state (it only explains, doesn't direct the user to `/library/suppliers` or anywhere).
- **Loading:** Handled with a skeleton (not a bare spinner): two `h-44 rounded-[12px] animate-pulse` blocks (`--surface-2`). Shown when `!queryEnabled || (isLoading && data === undefined)` — so it also covers the pre-auth/pre-mock-ready window. Skeleton shape (two tall blocks) does **not** resemble the actual grouped-table layout.
- **Error:** Handled. `Card` with "Couldn't load rule definitions" (`--danger`), muted "This is usually transient.", and an **↻ Retry** button (dark `--ink` fill, 36px tall) calling `refetch()`. Reason is generic ("transient") — does not surface the actual HTTP status from the thrown `rule-definitions: {status}` error.
- **Success/feedback:** No toasts or transient confirmations — success simply renders the grouped catalog and the live "{N} definitions" count in the subtitle. The Standards toggle gives immediate visual feedback (button background flips to `--surface-2` when open).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered at 1480px max-width with 34px gutters. Single-column stack of group cards (the layout never goes multi-column — rows are full-width within each group card). Lots of empty horizontal space at 1920 since rows are short text + one button.
- **Tablet 768:** Same single-column layout; gutter drops to 24px (`sm:px-6`). Context banner becomes a row (`sm:flex-row`). PageHeader stays title-only.
- **Mobile 390:** Gutter 16px (`px-4`), vertical `py-5`. Context banner stacks (`flex-col`). Header `h1` 28px. Row content wraps via `flex-wrap` on the title line and the code/path line. **Breakpoint risk:** the row uses `flex items-start gap-3` with a `flex-shrink-0` Standards button on the right; long titles/codes wrap in the left column while the button stays pinned — acceptable, but the mono `fieldPath operator value` line can wrap awkwardly and the `#F0F2F6` divider plus dense fractional type read cramped at 390px. No mobile-specific card transform — it is the same table-in-card, just narrower (this page is simple enough that it survives, unlike the mapper).

### Current UX issues
- **Severity pill is a separate, off-spec badge system (DESIGN BAR #4).** `SeverityPill` is `rounded-[4px] px-2 py-0.5 text-[10.5px] uppercase` with its own `SEV_STYLE` map — it does NOT share the app's `UnifiedStatusBadge` shape/size/padding, and it carries meaning by **color + word but no icon**, and the "System" chip is yet another bespoke pill shape. Three different chip treatments on one row.
- **Numbers are not tabular (DESIGN BAR #3).** The subtitle count, per-group counts, and the mono `expectedValue` (e.g. `0`) use default figures; mono helps but no `font-variant-numeric: tabular-nums` is set anywhere.
- **Off-scale spacing + fractional type (DESIGN BAR #1, #2).** Pervasive `0.5` steps (`py-0.5`, `mt-0.5`, `gap-y-0.5`, `pb-3.5`, `px-3.5`) and fractional font sizes (`12.5`, `11.5`, `10.5`, `10`) drift off the 4/8 + clean type scale. Hierarchy is carried partly by tiny size deltas (13 vs 12 vs 11.5px) that compress poorly.
- **Hardcoded colors bypass tokens (DESIGN BAR #5, #8).** Row divider `#F0F2F6` and banner border `#D6E2F4` are literals, not `--border`/gray-200 tokens — inconsistent with the Card's `--border`. Two radii coexist (`rounded-[12px]` group card vs `rounded-[8px]` panels vs `rounded-[4px]`/`rounded-[6px]` chips/button).
- **Standards toggle button is 28px tall — below the 44px hit target (DESIGN BAR #9).** It has `aria-expanded` + `aria-label` (good) and a hover background, but no visible focus-visible ring defined locally and no pressed state beyond the open-background swap.
- **Retry button uses `↻` glyph not a Lucide icon (DESIGN BAR icon consistency).** Same for the loading/empty states using raw text glyphs; the brand uses Lucide elsewhere.
- **Empty state has no next action (DESIGN BAR #6).** It explains but doesn't route the user anywhere — should point to supplier authoring.
- **Skeleton doesn't match layout (DESIGN BAR #6).** Two big `h-44` blocks don't preview the grouped-rows shape, so the loaded layout "jumps."
- **No sort/filter/search affordance.** Even read-only, with a growing catalog there is no way to filter by scope/severity or search by code; grouping is the only organization.
- **Density not the canonical table density (DESIGN BAR #5).** Rows are custom flex blocks inside a card, not the app's standard table (no sticky header, no zebra/hover-row, no aria-sort) — inconsistent with other library list pages.

### Redesign recommendations (for Claude Design)
1. **Unify all three chips onto the single status-badge system.** Replace `SeverityPill` + the "System" pill with the one shared badge (same shape/size/padding) using error→red, warning→amber, info→neutral-blue semantics + a Lucide icon (AlertCircle/AlertTriangle/Info) so severity is never color-alone. Keep navy/violet brand; green stays reserved for success/output, so do NOT color any severity green.
2. **Promote this to the canonical read-only table density.** Render each scope group as a real table section: single row height, consistent cell padding, low-contrast `gray-200` gridlines, row hover, and a sticky group/column header. Even read-only, give it columns (Title · Severity · Field path / operator · Standards) so codes and operators align in tabular columns with `tabular-nums`.
3. **Add a single filter/search bar in the PageHeader `actions` slot.** A scope filter (Order/Line/Header) + severity filter + a code/title search keeps the catalog usable as it grows — this is the one obvious "primary affordance" the page lacks (no destructive primary needed since it's read-only).
4. **Normalize spacing to strict 4/8 and the type scale.** Replace fractional sizes (12.5/11.5/10.5) with the defined scale (label 12/500, body 13/400, meta 11/500), and collapse `0.5`-step paddings to 4/8. Carry hierarchy by size+weight, not by 0.5px deltas.
5. **Replace literal colors with tokens.** Row divider `#F0F2F6` → the same `--border` as the card; banner border `#D6E2F4` → a blue-soft border token. Consolidate to one radius for cards and one for inline panels/chips.
6. **Standards disclosure: keep inline but make it a proper expander.** Bump the toggle to ≥44px hit area, add a chevron that rotates, add focus-visible ring + pressed state, and respect reduced-motion on the expand. Keep it in-flow (not a popover) — that's the right pattern for a reference table.
7. **Make the empty state actionable.** Add a primary CTA "Set up validation on a supplier" → `/library/suppliers` so the nothing-yet state gives the next action (DESIGN BAR #6).
8. **Surface the real error reason + Lucide retry.** Show the HTTP status / a human reason instead of only "transient," and swap the `↻` glyph for a Lucide RefreshCw icon to match the rest of the app.
9. **Reshape the skeleton to the grouped-rows layout** (group heading bar + 3–4 row lines per group) so the loaded view doesn't jump.
10. **Reinforce the read-only → authoring handoff.** Keep the blue context banner but make the link a clear secondary button ("Enforce on a supplier") so the relationship between this catalog and the per-supplier authoring tab is unmistakable.
