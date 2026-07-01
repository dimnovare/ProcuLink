## 15. Output templates — `/library/templates`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/templates/page.tsx`
- **Key components:**
  - `src/app/(app)/library/templates/page.tsx` — the page + the `TemplatePanel` editor modal + the `Field` + `PreviewLine` helpers (all defined in-file)
  - `src/app/(app)/library/templates/previewModel.ts` — `PREVIEW_BY_FORMAT`, `previewFor()`, `bodyForPreview()` (the per-format envelope skeletons and the export/preview body resolver)
  - `src/components/bridge/layout/PageShell.tsx` — page wrapper (`variant="wide"`, max-width `--container-wide` = 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` — title row ("Output templates" + subtitle + actions slot)
  - `src/components/bridge/EmptyState.tsx` (renders `MarkSystem` from `src/components/bridge/MarkSystem.tsx`) — zero-template state
  - `src/components/bridge/DSPrimitives.tsx` — `SrcChip` (format chip), `Button` (Export / Edit / Save / Cancel / Delete / Retry)
  - `src/app/(app)/library/templates/loading.tsx` — route loader → `BridgePageLoader` (`src/components/bridge/BridgeLoader.tsx`)
  - Data: `getTemplates` / `createTemplate` / `updateTemplate` / `deleteTemplate` / `TemplateDto` from `src/lib/api-client.ts`
- **Capture URL (mock):** `/library/templates` (mock mode renders the 4-card `MOCK_TEMPLATES` array; card `t1` "cXML 1.2.045 — OrderRequest" is auto-selected and shown in the right-hand preview)

### What it is & why it exists
This is the library of **output templates** — the file shape (envelope) each supplier receives when an order is transformed and delivered. It sits in the `transform → deliver` part of the workflow: a template defines the cXML / UBL / EDIFACT / X12 / JSON / CSV body, with `{token}` placeholders that are filled from the canonical order at delivery time. A procurement coordinator opens it to see what format goes out the door, to read/edit a template body, to create a new template for a new supplier standard, or to export/inspect the envelope before assigning it.

### Who uses it & the primary job
Persona: **integration expert / power-user procurement coordinator** (someone who cares which standard a supplier accepts). The single most important task is **opening a template's body editor to view or edit the envelope and `{token}` mappings** — i.e. selecting a card, reading the code preview, then clicking "Edit template" to open the modal body editor and save changes.

### Layout & structure (current)
Top to bottom inside `PageShell variant="wide"` (centered, max 1480px; gutter ramp 16→24→34px, vertical padding 20→28px):

1. **PageHeader row** — `h1` "Output templates" (Bricolage Grotesque, 28/30px, weight 600, `--ink`) with subtitle "The format each supplier receives · N templates" (13px `--ink-muted`). Right-aligned **green "+ New template"** primary CTA (`--brand-green`, white text; 27px tall desktop / 44px full-width mobile).
2. **Notice banner** (conditional) — full-width rounded bar above the grid; green-left-border for success, red-left-border for error. Shown after create/update/delete/export.
3. **Body** — a **two-column split grid**: `lg:grid-cols-[340px_minmax(0,1fr)]`, `gap-4`, `items-start`. Below `lg` it collapses to a single stacked column.
   - **Left column — template cards** (`flex flex-col gap-2`). Each card is a `<button>` (radius 8, `--surface`, 1px `--border`; selected = 1px `SELECT_BLUE`=`--brand-blue` border + `0 0 0 1px` blue ring; hover lifts `-translate-y-[2px]`). Padding `13px 15px 13px 17px`. Each card has a 3px left **accent strip** colored per format family (cXML=violet `#6F4FCE`, UBL=brand-blue, EDI/EDIFACT/X12=amber, JSON=`#A06200`, CSV=ink-muted). Inside: a `SrcChip` (format), an optional green **"Default"** pill (top-right), the template name (13px weight 600), a one-line plain-language description (`FMT_DESC`, 11.5px muted), and a supplier-assignment line (11px faint): "N suppliers: name, name" or italic "Not assigned to a supplier" when count = 0.
   - **Right column — code preview panel** (plain div, radius `--radius-md`=8, 1px `--border`, `--shadow-card`, `self-start`). Three stacked regions:
     - **Header bar** (`px-4 py-3`, bottom border): `</>` glyph + template name (13px weight 600, truncates) on the left; on the right a mono `v{version}` (hidden < sm) and a ghost **"↓ Export"** button.
     - **`<pre>` code body** — the envelope as monospace lines (JetBrains Mono, 11.5px, line-height 1.7, background `#FCFCFD`, text `#345470`). `{token}` segments are highlighted violet (`#6F4FCE`, weight 600) by `PreviewLine`. Horizontally scrollable.
     - **Footer bar** (`px-4 py-3`, top border): a faint hint "`{tokens}` are filled from the order at delivery time" on the left; a secondary **"✎ Edit template"** button on the right (full-width on mobile).

Density/type/spacing observations: spacing is a mix of Tailwind 4px-scale classes (`gap-4`, `px-4`, `py-3`) and many **hand-tuned odd pixel values via inline styles** (card padding `13px 15px 13px 17px`, `marginTop: 8/3/9`, header `py-2.5`, preview `padding 14px 16px`). Font sizes are fractional and ad-hoc (11px, 11.5px, 12.5px, 13px). Colors come from CSS vars but several literals are inlined (`#6F4FCE`, `#FCFCFD`, `#345470`, `#C6CDDA`, `#D5DAEA`, `#0B1A2F`).

### Data shown
Entity: **output template**. Fields per card (left): `name`, `format` (→ `SrcChip` + accent), `version`, `suppliersCount` (+ mock-only `supplierNames[]`), `lastUsed` (defined in mock but **not actually rendered anywhere**), `isDefault` (mock-only → "Default" pill), `config.body`. Preview (right): the resolved body lines from `bodyForPreview()` (authored `config.body` if present, else the static `PREVIEW_BY_FORMAT` skeleton for that format) + `name` + `version`.

Data source: live = `getTemplates()` → `GET /api/templates` (`TemplateDto[]`: `id, name, format, version, suppliersCount, lastUsed, config`), gated by `enabled: !isApiMockMode`. Mock mode (dev only, `NEXT_PUBLIC_USE_MOCK=true`) uses the in-file `MOCK_TEMPLATES` array (4 entries: `t1` cXML/2 suppliers/Default, `t2` UBL/1, `t3` EDIFACT/1, `t4` X12/0/unassigned); `getTemplates` returns `[]` in mock mode but the query is disabled. Mutations: `createTemplate` (`POST /api/templates`), `updateTemplate` (`PUT /api/templates/{id}`), `deleteTemplate` (`DELETE /api/templates/{id}`) — all no-op-to-success in mock mode.

### Interactive elements

| Control | Action | Result/where it goes |
| --- | --- | --- |
| "+ New template" button (header) | `newTemplate()` | Opens `TemplatePanel` modal with a blank `{id:"new"}` template (no Delete button) |
| "+ New template" button (empty state) | `newTemplate()` | Same as above (only visible when 0 templates) |
| Template card (left, each) | `setSelId(t.id)` + clears notice | Selects card → updates right preview panel; no navigation |
| "↓ Export" button (preview header) | `exportTemplate(selected)` | Builds a Blob from `bodyForPreview()`, triggers a browser download (`{name}.{ext}`), shows green "Exported …" notice |
| "✎ Edit template" button (preview footer) | `setEditing(selected)` + clears notice | Opens `TemplatePanel` modal pre-filled with the selected template |
| Retry button (error state) | `refetch()` | Re-runs the `templates` query |
| **Modal — Template name** input | `nameRef` (uncontrolled) | Required; empty → inline validation message |
| **Modal — Standard** `<select>` | `fmtRef` | Options: cXML / UBL / EDI / X12 / JSON / CSV |
| **Modal — Version** input | `versionRef` (mono) | Free text, default "1.0" |
| **Modal — Template body** `<textarea>` | `bodyRef` (dark navy, mono) | The editable envelope; defaults to a cXML snippet for new templates |
| **Modal — Save** button | `handleSave()` | Validates name → calls `createTemplate`/`updateTemplate`, closes modal, fires notice + invalidates query |
| **Modal — Cancel** button | `onClose()` | Closes modal, no save |
| **Modal — Delete** button (edit-only) | `onDelete()` → `deleteMutation.mutate(id)` | Deletes template, closes modal, green notice, invalidates query. **No confirm dialog.** |
| **Modal — × close** button | `onClose()` | Closes modal |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
| --- | --- | --- | --- | --- |
| **Template editor / body editor** (`TemplatePanel`) | Modal (fixed full-screen scrim, centered card desktop / bottom-sheet mobile) | "+ New template" (header), "+ New template" (empty state), or "✎ Edit template" (preview footer) | Header: ▤ blue icon tile + title ("New output template" or the template name) + "The format a supplier receives" subline + × button. Body: intro paragraph, a 3-up field row (Template name / Standard select / Version), a large dark-navy monospace **Template body textarea** (`min-h-180px`), a `{Like_this}` helper note, and (conditionally) a blue validation message. Footer: Delete (edit-only, left) · Cancel · Save (green primary, shows spinner while saving). | × button, Cancel button, or successful Save / Delete (programmatic close via `onClose`/`onSaved`). **No Esc-key handler and no backdrop-click-to-close** — clicking the scrim does nothing. |
| **Success / error notice** | Inline banner (not an overlay; renders in the page flow above the grid) | Save, Delete, Export (success); failed delete/save (error) | One line of feedback text, colored green/red | Auto-replaced/cleared on next card select, new/edit open, or next action. Has **no dismiss control** and **does not auto-dismiss**. |
| **File download** | Browser-native (programmatic `<a download>`) | "↓ Export" | The previewed envelope as a `.xml/.edi/.x12/.json/.csv/.txt` file | N/A (handled by browser) |

No dropdowns, popovers, tooltips, drawers, or toasts. The only true overlay is the `TemplatePanel` modal; everything else is in-place. (Notably the standards-visibility hint is a static footer string, not an info popover.)

### States
- **Empty:** Handled. `EmptyState` with the ProcuLink Mark, title "No output templates", sub "Templates define the format each supplier receives when an order is sent to them.", and a navy "+ New template" action. (Note: the empty-state action button is **navy**, while the header's New-template button is **green** — inconsistent.)
- **Loading:** Handled (two layers). The route `loading.tsx` shows `BridgePageLoader` on first navigation; the in-page `isLoading` branch (live mode only) renders a skeleton — four 104px pulsing card placeholders on the left and a 340px pulsing block on the right (hidden < lg). Good — a real skeleton, not a bare spinner.
- **Error:** Handled (live mode only). A centered red card: "Could not load templates" / "Check the API connection and try again." + a secondary Retry button. Mutation errors surface as the red inline notice ("Delete failed — please retry.", "Save failed — please retry.").
- **Success/feedback:** Inline green notice for create/update/delete/export. Save button shows an inline spinner + "Saving…" label while the mutation is in flight.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two-column split (`340px` card rail + fluid preview), centered within 1480px. Full header bar with `v{version}` visible. Modal is a centered 680px card with the 3-up field row.
- **Tablet 768:** Below `lg` (1024px) the split grid collapses to **one stacked column** — cards first, then the preview panel below. So at 768 the preview is full-width under the card list. `v{version}` (hidden < sm=640) still shows. Modal still centered.
- **Mobile 390:** Single stacked column. Header CTA becomes full-width 44px. Card list stacks. Preview footer's hint + "Edit template" stack vertically (`flex-col`), Edit button full-width. The modal becomes a **bottom sheet** (`items-end`, `rounded-t-10`, `max-h-92vh` scroll); its field row stacks; footer buttons stack full-width. `v{version}` is hidden.
- Breakpoint notes: the split only exists at `lg`+, so 768–1023px loses the side-by-side affordance (the preview is far below the cards — you must scroll past all cards to see the selected body). No drag canvas here, so no mapper-style mobile cliff.

### Current UX issues
- **Spacing drift (Bar 1):** padding/margins are a grab-bag of off-scale inline pixels (`13px 15px 13px 17px`, `marginTop: 8/3/9`, `py-2.5`, `14px 16px`) instead of one 4/8 rhythm. Card internal padding is asymmetric for no clear reason.
- **Type scale drift (Bar 2):** font sizes span 11 / 11.5 / 12 / 12.5 / 13 / 13.5px with hierarchy partly carried by faint grays (`--ink-faint` #98A0AE on white is borderline for the supplier line and `v{version}`), risking sub-4.5:1 contrast.
- **Two competing accent systems (Bar 4/7):** selection + the modal's primary intent use **brand-blue**, but the page's primary CTA and the "Default" pill use **brand-green**, and format accents add violet/amber. The empty-state action is **navy** while the header action is **green** — no single primary-action color story.
- **No status-badge consistency (Bar 4):** the "Default" marker is a one-off green pill (height 22, radius 4) that doesn't match `Pill`/`UnifiedStatusBadge`. The format `SrcChip` is the only badge that's systemized.
- **Numbers aren't tabular (Bar 3):** `v{version}`, supplier counts, and `lastUsed` use default figures; columns/labels can jitter. `lastUsed` is defined in mock data but never rendered — dead field.
- **Modal accessibility gaps (Bar; modals):** no `Esc`-to-close, no backdrop-click-to-close, no focus trap, no `role="dialog"`/`aria-modal`, and it does not animate from its trigger. The × is a custom 32px button (under the 44px target).
- **Destructive action unguarded (Bar; destructive):** Delete fires immediately with no confirm step and sits in the same footer row as Cancel/Save (only separated by `mr-auto`).
- **Inputs are uncontrolled refs with thin affordances:** border `#C6CDDA`, `h-10`; labels are tiny uppercase 11px faint — visible but low-emphasis. No helper text under fields, validation appears as a blue (not red/amber) box, which mis-signals an error as informational.
- **Honesty gap (Bar; offer⇔works):** the body preview/export is an **illustrative skeleton**, not the actual transform output for a real order — fine as a demo but the panel reads like the real envelope. The standards mapping (which `{token}` ↔ UBL `cbc:ID` / X12 `BEG03`) is implied by the raw code, not surfaced as field-level standards visibility.
- **Notice has no dismiss / no auto-timeout:** it lingers until another action, and it renders mid-flow (pushing the grid down) rather than as a consistent toast.
- **768–1023px usability:** the preview sits far below a tall card list (no side-by-side until 1024), so on tablets selecting a card then finding its body requires a long scroll.

### Redesign recommendations (for Claude Design)
1. **Make the body editor the hero, in a proper modal.** Add Esc-to-close, backdrop-click-to-close (with unsaved-changes guard), focus trap, `role="dialog"`/`aria-modal`, animate-from-trigger, and a 44px close target. Give the textarea real affordances: monospace with line numbers, `{token}` syntax highlighting (reuse `PreviewLine`'s violet), and a live mini-preview pane so authoring and result sit side-by-side.
2. **Resolve the primary-action color conflict.** Pick ONE in-app primary = brand-green for the dominant CTA per screen (header "New template" and modal "Save"); demote selection/intent accents to outline/ghost. Make the empty-state action green too (match the header).
3. **Unify the status/marker system (Bar 4).** Replace the bespoke "Default" pill with the shared `Pill`/badge primitive (one shape, size, padding, icon+word). Consider a small per-card "assigned / unassigned" badge using the same system instead of italic gray text.
4. **One spacing rhythm + one type scale (Bars 1, 2).** Convert all inline odd pixels to the 4/8 scale; collapse font sizes to heading 600 / label 500 / body 400 at fixed steps; raise the supplier line and `v{version}` above 4.5:1 (drop `--ink-faint` for `--ink-muted`). Use tabular figures for version, counts, and any timestamp.
5. **Guard destructive delete (Bar; destructive).** Move Delete to a separated zone and add a confirm step ("Delete template — assigned to N suppliers?") so it can't fire by accident, especially since it's adjacent to Save on mobile.
6. **Surface real standards visibility (CLAUDE.md standards rule).** Next to each `{token}` (or as a hoverable legend), show the canonical field name and its mapping (e.g. `{po}` = Order number → UBL `cbc:ID` / X12 `BEG03` / cXML `orderID`). Lead with the human field name, not the raw tag.
7. **Be honest about preview vs real output (Bar; offer⇔works).** Label the code panel as an "Example envelope — filled from a real order at delivery"; if a sample order exists, offer a "Preview with sample order" toggle that shows actual filled values vs `{tokens}`.
8. **Fix the 768–1023 layout.** Keep the card rail + preview side-by-side from `md` (or make the preview a sticky drawer on tablet) so selecting a card doesn't bury its body below a long list.
9. **Promote the notice to a consistent dismissible toast** (one elevation tier, auto-timeout + manual close), and use red/amber for true errors — switch the modal's validation box from blue to the danger token with the message below the offending field.
10. **Add visible labels + helper text to modal fields** (what "Standard" and "Version" mean), keep focus-visible rings, and ensure every interactive control (×, Export, Edit, card) has a 44px hit area and visible hover/pressed states; add `aria-label`s to icon-only/glyph buttons (`</>`, ↓, ✎, ×).
