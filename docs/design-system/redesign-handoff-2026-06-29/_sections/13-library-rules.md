## 13. Rule catalog (Validation rules) — `/library/rules`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/rules/page.tsx`
- **Key components:**
  - `src/components/bridge/ValidationRules.tsx` — the entire screen (table, mobile cards, inline editor, mobile bottom-sheet, loading/error states). It also defines the internal `RuleEditor`, `SeveritySegment`, `CheckRow`, `Field`, and `Toggle` sub-components in-file.
  - `src/components/bridge/layout/PageShell.tsx` — page wrapper (`variant="wide"`, max-width `var(--container-wide)` = 1480px).
  - `src/components/bridge/layout/PageHeader.tsx` — canonical title + subtitle + actions row.
  - `src/hooks/useOrderDirection.ts` — swaps the word "Supplier" → "Customer" for inbound orgs (display only).
  - API: `getRules` / `createRule` / `updateRule` / `toggleRule` / `deleteRule` / `RuleDto` from `src/lib/api-client.ts` (lines 1693–1749).
- **Capture URL (mock):** `/library/rules` (in mock mode `getRules()` returns `[]` and the component falls back to its own hard-coded `RULES` array of 10 rules — see `ValidationRules.tsx` lines 58–69; valid mock row ids are `r1`–`r10`).

### What it is & why it exists
This is the **descriptive catalog of validation checks** an org cares about (currency = EUR, line items need supplier codes, quantity > 0, warn over €50k, etc.). It sits in the *validate* stage of `parse → normalize → validate → review → transform → deliver → learn`, but with an honest caveat baked into the code: **this screen does NOT gate or block delivery.** The backend `ValidationRule` has no executable condition and `IValidationRuleService` is never called by the transform/delivery pipeline (see file-header comment, lines 9–15). The only validation that actually runs is the per-supplier **Acceptance** tab. So this page documents/classifies checks and links out to where enforcement is really configured. A procurement coordinator opens it to see "what checks exist, how severe each is, how often each fired in 30 days," and to author/edit/toggle catalog entries.

### Who uses it & the primary job
**Persona:** procurement coordinator / integration expert (admin-ish, but self-serve). **Primary job:** browse the rule catalog and **edit or create a rule** (name, scope, severity, description, active toggle), with the secondary job of flipping a rule's Active toggle on/off inline.

### Layout & structure (current)
Top-to-bottom on the grey page canvas (`--bg` #F6F7FA), inside `PageShell variant="wide"`:

1. **PageHeader** — title "Rule catalog" (Bricolage Grotesque, 28/30px, weight 600). Subtitle: "A catalog of the checks you want to run · {N} active. Enforcement is configured per supplier — set up blocking checks on each [supplier's Validation rules tab]" (last clause is a blue underlined link to `/library/suppliers`). Right-aligned **+ New rule** primary button (blue `#1E66C9`, 38px tall desktop / 44px mobile, full-width on mobile).
2. **Enforcement callout** (`hidden sm:block`) — a soft-blue info banner (`#F2F7FE` bg, `#D6E2F4` border): bold "This is a catalog, not a gate." + explainer + the same supplier-tab link. Hidden on mobile.
3. **Notice strip** (conditional) — a green inline confirmation pill ("Rule saved." / "Rule created." / "Rule deleted." / error text) with a 6px green dot.
4. **Split-detail body** — a CSS grid: `lg:grid-cols-[minmax(0,1fr)_minmax(340px,400px)]`, `gap-5` (20px). Below `lg` it collapses to one column.
   - **Left / main:** the **rules table** (desktop, `hidden lg:block`) inside a white card (radius 12px, border `#E5E8EE`, shadow `0 1px 3px rgba(16,24,40,0.05)`).
   - **Left (mobile):** the same rules as **stacked row-cards** (`lg:hidden`).
   - **Right:** the **inline `RuleEditor`** card — `lg:sticky lg:top-0`, `hidden lg:block`. Always rendered on desktop for the current selection (defaults to first rule).
5. **Mobile bottom-sheet** (`lg:hidden`, conditional) — a full-height-ish dialog (`top-12 bottom-0`) hosting the same `RuleEditor`, opened only by a card tap or +New rule on mobile.

**Table columns** (header is uppercase 10.5px, `#9AA3B5`, letter-spacing 0.07em): `Rule` | `Scope` | `Supplier` (relabels to "Customer" inbound) | `Severity` | `Triggered 30d` | `Active` (right-aligned). Row height ~ `py-3.5` (14px vertical). Disabled rules render at `opacity 0.62`. Active row = light-blue fill `#EAF0F8` + 2px blue left-border.

Spacing/type/density observations: heavy reliance on **inline `style={{}}` with hard-coded hex + odd px sizes** (10.5px, 11.5px, 12.5px, 13px, 14.5px) rather than tokens or the 4/8 scale. Numbers (Triggered, code) use JetBrains Mono. Severity uses a pill; Scope uses a grey chip.

### Data shown
Entity: **validation rule** (`RuleDto`). Fields displayed/edited:
- `name` (bold rule title; "Untitled rule" italic placeholder if empty)
- `code` — display-only, derived client-side via `codeFor(name, entity)` (e.g. `HEA-PA-YM`, mock uses pretty codes like `GLOBAL-CUR-01`); shown mono under the name.
- `entity` → **Scope** chip; one of `Line item | Header | Supplier | Buyer | Amount`.
- `supplier` — display-only, **always "All suppliers"** for live data (RuleDto has no per-rule supplier binding; mock has a few named ones like "Acme Components", "VanDerBerg Metaal").
- `severity` → pill: `error`=Critical (`#FBE3E3`/`#B43838`), `warning`=Warning (`#FAF1DD`/`#B36D14`), `info`=Info (`#EAF0F8`/`#1E66C9`).
- `triggers` (`triggerCount`) → "Triggered 30d" mono number (`#0B1A2F` if >0, else faint `#CBD0DA`).
- `enabled` → Active toggle.
- `description`, `lastTriggered`, `autoBlock` (autoBlock preserved but UI removed), `createdAt`.

Data source: live `GET /api/rules` via `getRules()` (TanStack Query key `["rules"]`, enabled only when `!isApiMockMode`). In mock mode the component uses its local `RULES` array and mutates it in React state.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **+ New rule** (header button) | `setNotice(null); setSelId("new"); setEditorOpen(true)` | Loads a blank `NEW_RULE` into the editor (desktop right panel; mobile opens bottom-sheet). |
| "supplier's Validation rules tab" link (subtitle + callout) | `next/link` | Navigates to `/library/suppliers`. |
| Table row click | `setNotice(null); setSelId(r.id)` | Selects that rule → editor panel re-keys to it. |
| **Active toggle** (table cell / mobile card) | `handleToggle(id)` — `stopPropagation` so it doesn't select | Mock: flips local state. Live: `toggleRule(id)` → `PATCH /api/rules/{id}/toggle`, invalidates `["rules"]`. |
| Mobile rule card tap | `setSelId(r.id); setEditorOpen(true)` | Opens the mobile bottom-sheet editor for that rule. |
| Editor **Rule name** input | `defaultValue` ref | Captured on save. |
| Editor **Applies to** `<select>` | native select (Line item/Header/Supplier/Buyer/Amount) | Captured on save (Scope). |
| Editor **Severity** segmented control | `SeveritySegment` — Warning / Critical buttons | Sets local `severity` state; recolors the "Recommended enforcement" banner. (`info` rules display as Warning until the user picks one.) |
| Editor **Condition (WHEN)** box | read-only mono panel | Shows `rule.condition` / falls back to description; not editable. |
| Editor **Recommended enforcement** box | read-only | Severity-colored sentence telling you to set the check up per-supplier (descriptive only). |
| Editor **Description** textarea | `defaultValue` ref | Captured on save. |
| Editor **In catalog (active)** checkbox | `CheckRow` ref | Captured on save as `enabled`. |
| Editor **Save rule / Create rule** (green) | `save()` → `handleSave` | Mock: mutates local array. Live: `createRule` (`POST /api/rules`) or `updateRule` (`PUT /api/rules/{id}`); invalidates `["rules"]`; sets notice; closes mobile sheet. |
| Editor **Delete** (red icon button, `onDelete` only when not new) | `handleDelete(id)` | Fires `window.confirm`; on OK mock-removes or `deleteRule` (`DELETE /api/rules/{id}`); clears selection; sets notice; closes sheet. |
| Mobile sheet **Close (X)** button | `setEditorOpen(false)` | Closes the bottom-sheet. |
| Mobile sheet **backdrop** | `setEditorOpen(false)` | Closes the bottom-sheet. |
| Error-state **Retry** button | `refetch()` | Re-runs the rules query. |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Rule editor (desktop)** | Inline sticky panel (NOT an overlay) | Always present; content swaps on row click or **+ New rule** | Rule name, Applies-to select, Severity segment, read-only Condition + Recommended-enforcement boxes, Description textarea, "In catalog (active)" checkbox, trigger stats, Save + Delete | Never closes — it is a permanent grid column. Selecting another row replaces its content. |
| **Mobile editor bottom-sheet** | Drawer / sheet (`role="dialog" aria-modal="true"`) | Mobile card tap, or **+ New rule** on mobile (`setEditorOpen(true)`) | Sticky header (title "New rule"/"Edit rule" + code, 44×44 close button) over a scrollable `RuleEditor` (`variant="sheet"`) | X button, **Escape** key (window listener), backdrop tap, or a successful Save/Delete (`setEditorOpen(false)`). Body scroll is locked while open. |
| **Delete confirmation** | Native `window.confirm` dialog | Delete (trash) icon button in the editor footer | Browser-native "Delete this validation rule? This cannot be undone." OK/Cancel | OK (proceeds with delete) / Cancel (aborts). Not a styled component. |
| **Notice pill** | Inline toast-like strip (not a true toast) | Successful/failed save, create, delete | "Rule saved." / "Rule created." / "Rule deleted." / "Couldn't save the rule — try again." with a colored dot | Persists until the next row selection or action sets/clears it (`setNotice(null)`); no auto-dismiss, no close button. |

There is **no import/upload panel, no modal dialog on desktop, no dropdown menu, no real toast system, and no popover.** The FOCUS HINT's "rule edit/import panel" maps to the always-on inline `RuleEditor` (edit) — there is no import.

### States
- **Empty:** Handled. Desktop table renders a single full-width cell: "No rules in your catalog yet. Create one to document a check you want to run." Mobile renders the same copy in a white card. (Note: `getRules()` returns `[]` for the live empty case; the message is informative but does not include a prominent CTA beyond the header's +New rule.)
- **Loading:** Handled (live only). Returns a `PageShell` with a pulsing title bar skeleton (`#E5E8EE`, 28×200) + one large pulsing card skeleton (360px tall white card). No bare spinner. (No `loading.tsx` exists for this route — handled in-component.)
- **Error:** Handled (live only). Centered white card: red "Could not load validation rules" + "Check your connection and try again." + a navy **Retry** button calling `refetch()`.
- **Success/feedback:** The green notice pill (above). Save button shows "Saving…" at 0.6 opacity while `saveMutation.isPending`. Important: in live mode the success notice is set in the mutation's `onSuccess` (not synchronously) so a failed save shows the error string instead of a false success.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two-column split-detail. Content capped at 1480px (`--container-wide`), centered with `34px` gutters. Table on left, sticky editor (340–400px) on right. Enforcement callout visible.
- **Tablet 768:** Still below the `lg` (1024px) breakpoint → **single column**: mobile row-cards stack, the desktop table and inline desktop editor are hidden, and editing happens via the **bottom-sheet**. The `sm:`-only enforcement callout shows; the +New rule button is auto-width.
- **Mobile 390:** Stacked row-cards (name+code, 44×44 toggle, severity pill + scope chip + supplier, description, "Triggered N in the last 30 days" footer). Editor is a full bottom-sheet. Enforcement callout hidden (intentional, so the list isn't pushed below the fold). Form controls grow to 44px tall and 15px text on mobile.
- **Breakpoint cliff:** The editor switches host at **lg (1024px)**, not at the visual table breakpoint, so the 768–1023px range shows mobile cards + sheet while still on a wide-ish viewport — fine, but the desktop table never appears on tablet. The desktop table has `overflow-x-auto`; with 6 columns it can scroll horizontally on narrower desktop widths.

### Current UX issues
- **Token drift / inline-style sprawl (DESIGN BAR 1, 2, 8):** nearly every color, radius, shadow, and font-size is a hard-coded inline hex/px (`#B43838`, `#FBE3E3`, `#EAF0F8`, `#9AA3B5`, sizes 10.5/11.5/12.5/14.5px). Not on the 4/8 spacing scale, not using the semantic CSS variables (`--ink`, `--brand-green`, `--border`) that the rest of the system defines. Severity hexes are duplicated from the global pill classes instead of reusing them.
- **Two parallel status-pill systems (DESIGN BAR 4):** severity uses a bespoke `SEV` map (rounded-full, 22px, dot+word) that doesn't share the global `.pill-*` badge classes; the Scope chip is yet another shape. Editor severity is edited via a 2-option segment (Warning/Critical) that silently collapses the third `info` value — info rules can't be re-selected as info.
- **Honesty tension surfaced but heavy (DESIGN BAR "never show healthy when failing"):** the page correctly admits it's "a catalog, not a gate," but it still shows "Triggered N in last 30 days" stats and severity that imply enforcement. Two near-identical "enforcement is per supplier" explanations (subtitle + callout) repeat the same link, which is redundant.
- **Notice is not a real toast (DESIGN BAR 6):** it's an inline strip with no auto-dismiss and no close affordance; it lingers until the next click. Inconsistent with a proper toast system.
- **Condition box is dead on live data:** the read-only "Condition (WHEN)" shows `description` for live rules (RuleDto has no condition), so it just echoes the description field below it — looks like a real expression but isn't.
- **Toggle accessibility (DESIGN BAR 9):** the `Toggle` is a `role="switch"` button with no visible focus ring styling beyond the global one and no label/`aria-label`; in the table its only context is the column header.
- **Delete uses `window.confirm` (DESIGN BAR confirm-before-destroy):** functional but jarring/native, not matching the styled sheet/dialog language; destructive button is an icon-only trash with only a `title`/`aria-label`.
- **No sortable columns / no `aria-sort` (DESIGN BAR 5):** the table header looks sortable-adjacent but isn't; no filter/search over potentially long catalogs.
- **Editor "code" is fabricated client-side:** `codeFor()` generates codes like `HEA-CU-RR` that look authoritative but are derived, not stored — risks user confusion vs the prettier mock codes.
- **Empty state lacks a strong next action (DESIGN BAR 6/7):** copy is good but there's no in-context primary button; the only CTA is the header +New rule.

### Redesign recommendations (for Claude Design)
1. **Replace inline styles with the design tokens + a shared badge/pill component.** Map severity to ONE status-badge primitive (green/amber/red/neutral, single shape/size/padding, icon+word) reused from the global system; same for the Scope chip. Keep navy `#0B1A2F` + the blue accent for selection, green for the primary Save/CTA. (DESIGN BAR 1, 2, 4, 8.)
2. **Normalize the table to the canonical list density:** one row height, 8px-grid cell padding, `gray-200` gridlines, sticky header, real sortable columns with `aria-sort`, and a search/filter bar (by name, scope, severity, active) — catalogs will grow. Tabular figures for the Triggered count and code. (DESIGN BAR 3, 5.)
3. **Make the "catalog, not a gate" story honest and singular:** collapse the duplicated subtitle + callout into one clear banner; consider visually de-emphasizing trigger stats (or labeling them "observed in review") so the screen never implies it blocks delivery. Keep the single deep-link to the supplier Acceptance tab. (DESIGN BAR "never show healthy when failing".)
4. **Promote one primary action per screen:** keep **+ New rule** as the single dominant primary (green, ≥44px) in the header OR an empty-state CTA; demote it to one place. The editor's Save is the contextual primary inside the panel. (DESIGN BAR 7.)
5. **Fix the severity control to support all three values** (Warning / Critical / Info) as a 3-segment control or radio group, instead of silently dropping `info`. Surface the read-only Condition only when a real machine-readable condition exists; otherwise hide it rather than echoing the description. (DESIGN BAR 2, forms.)
6. **Upgrade feedback + destructive flows:** swap the lingering notice strip for a real auto-dismissing toast; replace `window.confirm` with a styled confirm dialog (scrim, focus trap, animate from trigger, destructive button separated/red-outlined). Give the toggle an explicit `aria-label` and visible pressed state. (DESIGN BAR 6, 9, confirm-before-destroy.)
7. **Strengthen empty + loading:** add an illustrated/iconed empty state with an inline "Create your first rule" button and a one-line link to the supplier Acceptance tab; keep the skeleton but match it to the real two-column layout. (DESIGN BAR 6.)
8. **Mobile sheet polish:** ensure the bottom-sheet animates up from the trigger, keeps Save reachable (sticky footer), and the close affordances (X / Esc / backdrop) are all present (they are — preserve them). Keep the desktop sticky inline editor; it's a good pattern. (DESIGN BAR 10.)
