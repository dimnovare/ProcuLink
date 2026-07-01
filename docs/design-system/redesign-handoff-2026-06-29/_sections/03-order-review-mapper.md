## 03. Order Review / Mapper (the "Workshop") — `/inbox/[orderId]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/inbox/[orderId]/page.tsx`
- **Key components:**
  - `src/components/bridge/workshop/OrderWorkshop.tsx` — the orchestrator (header, send gate, flow notice, body switch, all overlays)
  - `src/components/bridge/mapper/MapperWorkbench.tsx` — the 3-pane mapper shell (toolbar, attention-first split, wire layer host)
  - `src/components/bridge/mapper/IncomingPane.tsx` — left "What we received" column (grouped source rows + values)
  - `src/components/bridge/mapper/OutgoingPane.tsx` — center "What we'll send" column (output rows, picker chip, inline AI fix, transform/fixed-value controls, "Add output field")
  - `src/components/bridge/mapper/MapperPreviewPane.tsx` — right "Live preview" column (navy code body, format toggle, copy/download)
  - `src/components/bridge/mapper/SourcePickerChip.tsx` — the inline searchable source dropdown on each output row (picker mode)
  - `src/components/bridge/mapper/TransformPopover.tsx` — the per-field "Edit value" (manipulator-chain) portal popover
  - `src/components/bridge/workshop/IssuesPanel.tsx` — the plain-language "Fix these to send" issue list with inline per-line resolution
  - `src/components/bridge/workshop/SendReadinessStrip.tsx` + `WorkshopStepper.tsx` — slim readiness bar + 5-stage pipeline stepper
  - `src/components/bridge/workshop/MobileTriage.tsx` — the < 1024px review-and-send fallback (no drag canvas)
  - `src/components/bridge/workshop/OrderDetailsDrawer.tsx` — the right "Details" drawer (Audit trail / Standards check / Response sub-tabs)
  - `src/components/bridge/review/ConfirmDialog.tsx` — the send confirmation modal
  - `src/components/bridge/OutputStructureDesigner.tsx` — full-screen "Customize output layout" power editor
  - `src/components/bridge/FailedPanels.tsx` (`ParseFailedPanel` / `FailedPanel`) — parse/transform/delivery failure recovery screens
  - Hooks: `review/hooks/useOrderReview.ts`, `useResolveActions.ts`, `useAcceptanceValidation.ts`, `useSendFlow.ts`; `workshop/useWorkshopLayout.ts`
- **Capture URL (mock):** `/inbox/ord-002` (status `pending_review`, 4 lines, 2 unresolved with AI suggestions — the richest "needs work" state; `ord-001` = clean/ready, `ord-003` = delivered)

### What it is & why it exists
This is the money screen: the place where a messy parsed purchase order becomes the exact file a specific supplier accepts. It sits at `review → transform` in the `parse → normalize → validate → review → transform → deliver → learn` loop. A procurement coordinator opens it to confirm/fix item-code mappings, see (and tune) which incoming field fills each output field, watch a byte-accurate live preview of what the supplier will receive, clear every blocking issue, and press one Send that transforms and delivers. It is deliberately the only screen that gates Send on server-truth validation (`exceptionCount === 0 && blockingIssues === 0`) so HTTP 200 is never mistaken for supplier acceptance.

### Who uses it & the primary job
Primary persona: the **procurement coordinator** (approves a €399 plan alone, lives in Excel today). The single most important task: **resolve every unresolved line / required field, confirm the preview is correct, and Send.** A secondary **integration expert** persona uses the deeper affordances (source picker re-pointing, fixed values, value transforms, the Output Structure Designer, the Standards-check tab) but everything is one experience with progressive disclosure — no mode toggle.

### Layout & structure (current)
Top-to-bottom, the whole screen is a flex column (`100% h`, `#F6F7FA` background, `overflow:hidden`):

1. **Header bar** (white, `1px #E5E8EE` bottom border, `px-4 lg:px-6`, ~`pt/pb 2.5`). Left cluster: a 30×30 boxed back arrow (`← Back to inbox`), then the PO number as an `h1` (Bricolage Grotesque, 22px, weight 800), a `UnifiedStatusBadge`, an optional amber "Looks like an invoice" pill (`InvoiceBadge`, when `documentType === "invoice"`), and an optional red dead-letter pill. Sub-line: `buyer (blue #1E66C9) → supplier (green #2E8E3A) · grand total (mono #566982)`. Right cluster (`ml-auto`, `flex-wrap`, lg+ only for the first two): **Details** outline button, the **Focus** segmented control (All / Mapping / Output), and the dominant **Send** button (green when armed `#2E8E3A`, muted gray-green `#5A7660` when disabled; label morphs to `Fix N to send` / `Preparing the file…` / progress / done).
2. **Flow notice strip** (conditional) — full-width `role="status"` bar tinted by severity (blue info / green success / red error) carrying send progress + outcome text from `useSendFlow`.
3. **Send-readiness strip** (lg+ only, `SendReadinessStrip`) — slim full-width bar: green "Ready to send — every required field is filled and validated" OR amber "N fields to fill before sending" + one mono chip per blocker (click → scroll/flash the issue card). At its right end the `WorkshopStepper` (xl+ only) renders the 5-stage Parse → Normalize → Validate → Transform → Deliver pipeline.
4. **Body** (`flex:1; overflow:auto`):
   - **Desktop (lg ≥1024):** an optional `IssuesPanel` card on top, then the `MapperWorkbench`. The workbench is a **two-level CSS grid**: OUTER `[ received+output canvas | live-preview ]` at `minmax(0,1.85fr) minmax(380px,1.05fr)` (collapses to a 46px rail when a pane is collapsed); INNER canvas `[ received | output ]` at `minmax(300px,0.92fr) minmax(360px,1fr)` with the SVG wire overlay drawn over the inner pair. Above the canvas sits the workbench toolbar ("Map this order", an N-of-M mapped chip, saving/✓Saved/error/AI-unavailable inline status; right side: Show/Hide connections toggle, required-unmapped warning, **Customize output layout**, **Fill from catalog · N**, optional **Save mappings**, optional **Send**). Each of the three panes has an identical 52px column header forming one connected strip (blue dot "What we received" · green dot "What we'll send" · green dot "Live preview · FORMAT").
   - **Mobile/tablet (< lg):** the `MobileTriage` review-and-send surface renders instead (summary cards + the same issue list + one-click fixes + a sticky Send bar). The drag canvas is intentionally not shown.
5. **No persistent footer** — the primary action lives in the header (desktop) or a sticky bottom Send bar (mobile triage).

**Density/type/spacing observations:** spacing is highly hand-tuned with **fractional pixel values everywhere** (font sizes 9.5/10.5/11.5/12.5/13.5, paddings like `9px 11px`, `3px 11px`, gaps 6/7/10/12) — heavily inline-styled, not on a strict 4/8 scale. Numbers correctly use `'JetBrains Mono'` + `fontVariantNumeric: tabular-nums` in most places (values, totals, output paths). Headings use Bricolage Grotesque; labels weight 600–800.

### Data shown
- **Order** (`Order` from `getOrderById(orderId)` → `["order", orderId]`): `poNumber`, `status`, `documentType`, `supplierId`, `supplierName`, `buyerName`, `orderDate`, `currency`, `grandTotal`/`subTotal`/`taxTotal`, `paymentTerms`, `sourceFileKey`, `artifacts[]`, and `lines[]` each with `lineNumber`, `buyerItemCode`, `supplierItemCode`, `description`, `quantity`, `unit`, `unitPrice`, `confidence`, `needsReview`, and optional `aiSuggestion {supplierItemCode, confidence, reason, provenance}`. (Mock store: `mockOrders` in `api-client.ts`.)
- **Incoming pane** rows = the model's `sourceFields` (header / parties / line items / raw extras), each `label` + real `value` (mono) + mapped/AI-suggested flags. Built from the parsed order directly (`incomingFromOrder.ts`), not a separate fetch.
- **Outgoing pane** rows = the model's `targetFields` (output path + human label + per-row resolved status: wired / fixed / auto / unmapped + `valuePreview`). Plus read-only auto-filled Ship-to/Bill-to/Contact/Tax-ID blocks for structured formats.
- **Live preview** = `previewMappingOverride(orderId, override, format, honorFormat)` — the actual delivered bytes (or honest amber warning).
- **Mapping override** seed = `getMappingOverride(orderId)` → `["mapping-override", orderId]`.
- **Issues** = `buildFixQueue(order, validationResult)` → mapped to `WorkshopIssue[]` (title + why + kind + severity).
- **AI calibration** = `getAiCalibration()` → drives the trust threshold for the attention-first split.
- **Audit events** = `getOrderAudit(orderId)` (only when `status === "failed"`, to seed the parse-failure copy).
- **Drawer** sub-panels (`OrderPassport`, `ConformancePanel`, `SupplierResponsePanel`) each fetch their own data on mount.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Back arrow (header) | click | `router.push("/inbox")` |
| PO `h1` / status badge / invoice pill | display only | — |
| **Details** button (lg+) | click | opens `OrderDetailsDrawer` on the Audit-trail tab (`?tab=passport`) |
| **Focus** segmented control (All / Mapping / Output, lg+) | click | sets `useWorkshopLayout` focus → collapses/expands the incoming + preview panes |
| **Send** button | click (only when `canSend`) | opens `ConfirmDialog`; hovering while disabled shows a navy tooltip "Fill N required field(s) below first" |
| Send-readiness blocker chip | click | `onJumpToIssueCard` → scroll + amber flash the matching `IssuesPanel` card |
| IssuesPanel "Enter code" / inline code input + Save / Esc | click/type/Enter | `useResolveActions.startLineEdit` → `commitLineCode` (server commit → refetch) |
| IssuesPanel "Accept suggestion" | click | `resolve.acceptSuggestion(ref)` (one-click AI accept) |
| IssuesPanel "Confirm" / "Change code" (review-flag) | click | `confirmFlaggedLine` / `startLineEdit` |
| IssuesPanel "Accept all AI suggestions" / "Accept ≥85% only" | click | `bulkAcceptSuggestions(0)` / `(0.85)` (same `POST /accept-ai-suggestions` as upload preview) |
| IssuesPanel "Where →" | click | `onFocusField(ref)` → select + scroll the row in the mapper |
| Incoming search box | type (150ms debounce) | filters incoming rows, auto-reveals collapsed groups |
| Incoming filter chips (All / Unmapped / Mapped / Has AI suggestion / Has value) | click | filters rows; counts shown in mono |
| Incoming group header (Header / Parties / Line items / Raw extras) | click | collapse/expand group |
| Incoming row drag grip (⠿, 22px, blue ring) | drag onto an output row | wires the field (drag-to-connect) — wires mode; in picker mode wires are hidden by default |
| **Show / Hide connections** toggle (toolbar, picker mode) | click | reveals/hides the wire SVG layer |
| Output row source picker chip "← pick a field ▾" | click | opens `SourcePickerChip` portal dropdown |
| Source picker option / "= Fixed value…" / "Clear" / search / arrows / Enter | click/type/keyboard | `onPickSource` (→ wire-connect dispatch) / open fixed-value editor / disconnect |
| Output row "= value" chip | click | opens inline fixed-value input (Set / Clear) |
| Output row "Edit value · N" (ƒx) chip | click | opens `TransformPopover` (manipulator chain) |
| Output row status tag ✕ (wired) / ✎ (fixed) | click | `onDisconnect` / open fixed-value edit |
| Output "Apply" (inline AI fix strip) | click | `onPickSource(path, suggestedId)` — maps + clears blocker |
| Output "N mapped · review" attention chip | click | expand/collapse the auto-mapped rows |
| Output "N fields ready · mapped automatically" summary | click | collapse/expand auto group (picker mode) |
| **Add output field** (+ dashed green) | click | opens the canonical-field combobox + custom-create footer |
| **Customize output layout** (toolbar) | click | opens `OutputStructureDesigner` (full-screen power editor) |
| **Fill from catalog · N** (toolbar) | click | scrolls to first line with a catalog price/code hint |
| **Save mappings** (toolbar, when handler present) | click | promotes the per-order mapping to the supplier |
| Preview format toggle (CSV/JSON/XML/cXML/UBL/X12) | click | re-renders the preview in that format (exploratory vs delivered) |
| Preview **Copy** / **Download** | click | clipboard / blob download with correct extension+mime |
| `AI suggestions to review` banner "Dismiss all" | click | rejects every AI wire suggestion |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Send confirmation** (`ConfirmDialog`) | modal (scrim + blur, `z 9990`, focus-trap, `aria-modal`) | the green **Send** button | title, summary grid (grand total · lines · issues · format), policy confirm checkbox, failing-rules acknowledgement checkbox, stale-validation note, retry note, Cancel + primary CTA | Cancel button · Esc · backdrop click · confirm (`onConfirm` → `useSendFlow.confirmSend`) |
| **Order details drawer** (`OrderDetailsDrawer`) | right drawer (scrim `rgba(11,26,47,.18)`, slide-in, focus-trap, `aria-modal`, 760px / max 64%) | header **Details** button, or `?tab=` deep link | sub-tab strip (Audit trail / Standards check / {Supplier} response) + the matching panel (`OrderPassport` / `ConformancePanel` / `SupplierResponsePanel`) | × close button · Esc · backdrop click (clears `?tab=`) |
| **Output Structure Designer** (`OutputStructureDesigner`) | full-screen inline editor (replaces body when `showDesigner`) | toolbar **Customize output layout** | output-node tree editor, paste-supplier-sample inference, JSON/XML/CSV format, value-format presets, live preview, Save | onClose (its own close control) · onSaved (saves + invalidates queries) |
| **Source picker dropdown** (`SourcePickerChip`) | popover (body portal, `position:fixed`, flips above on overflow, `role="listbox"`, `z 1000`) | output row "pick a field ▾" chip | search box, grouped incoming options (AI suggestion first + % confidence + value), "= Fixed value…", "Clear" footer | outside mousedown · Esc · pick an option · footer action |
| **Transform / Edit-value popover** (`TransformPopover`) | popover (body portal, `position:fixed`, flips above, `role="dialog"`, `z 1000`) | output row "Edit value" (ƒx) chip | manipulator chain rows (plain-English labels + params), "+ Add an adjustment…" select, Done; live-saves on change | outside mousedown · Esc · Done button · trigger toggle |
| **Add output field combobox** | popover (in-flow absolute, transparent click-away scrim, `role="dialog"`, `z 31`) | "Add output field" + button | search/type input, grouped canonical Header/Line picker items (with standards ref tooltip), custom-create footer with header/line scope toggle | scrim click · Esc · pick a field · add custom |
| **Inline fixed-value editor** | inline panel (in-row, transient) | output row "= value" chip · picker "= Fixed value…" · status-tag ✎ | text input + Set + Clear | Set (commit) · Esc · Clear |
| **Inline supplier-code editor** | inline panel (in IssuesPanel row) | "Enter code" / "Change code" / "Enter manually" | mono text input + Save + Esc | Save (commit→refetch) · Esc · Cancel |
| **Send-disabled tooltip** | tooltip (absolute navy bubble, `z 60`) | hovering the disabled Send button | "Fill N required field(s) below first…" | mouse leave |
| **Flow notice** | inline status strip (`aria-live`) | `useSendFlow.setFlow` during transform/deliver | progress / success / error text | replaced/cleared by the next flow state |
| **Standards-check** | reuses the Details drawer | OutgoingPane/`onValidate` → `openDetails("conformance")` | the Conformance panel | (see drawer row) |

This screen is overlay-rich (the user excepted it from exhaustive open/close mapping, but the table above is complete). It also navigates in place for back (`/inbox`) and uses `?field=` / `?tab=` query params for deep-link focus rather than separate routes.

### States
- **Empty:** no true "empty list" — an order always has lines/fields once parsed. Incoming pane has honest empty copy per cause ("arrived already-structured…", extraction-failed fallback). Output pane shows "No output fields yet — add one…" when truly empty.
- **Loading:** `loading.tsx` → `BridgePageLoader`. In-component: `BridgePageLoader` with "Preparing your order…" while the order query is in flight; `WorkbenchSkeleton` (3-column pulse grid) while the mapper model loads; preview shows "Rendering…"; a dedicated **parsing** screen (`BridgeLoader` + "We're reading your order…" + PO mono, auto-polls every 3s).
- **Error:** order load failure / not-found → centered card with icon, "Order not found" / "Failed to load order", and a "← Back to inbox" button. Status-driven failure screens: `ParseFailedPanel` (status `failed`, seeded from audit events), `FailedPanel` for `transform_failed` and `delivery_failed`. Preview render failure → amber inline note (never crash). Save failure → inline red error text in the toolbar.
- **Success/feedback:** "✓ Saved" inline flash (~2s) after a clean auto-save; green flow notice on delivered ("Delivered to supplier / Order confirmed. The audit trail has been updated."); preview "Valid" green pill + one-shot content flash; copy "✓ Copied". Issues panel collapses to a green "Ready to send" bar at zero issues.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full 3-pane mapper — received + output canvas docked beside the live preview (`1.85fr / 1.05fr`); the xl WorkshopStepper shows the full 5-stage pipeline; Details + Focus controls visible.
- **Desktop 1024–1439 (lg):** mapper still renders (the inner canvas fits ~1000px so a 13"/14" laptop keeps the full field mapper); below ~1440 the preview wraps under the canvas rather than docking beside it. The xl stepper hides below 1280.
- **Tablet 768 / Mobile 390 (< lg):** the drag canvas is dropped entirely; `MobileTriage` renders the review-and-send surface (summary cards, full issue list with one-click fixes, read-only "what we'll send" preview, sticky Send bar). The header's Details + Focus controls are hidden so Send isn't clipped.
- **Known cliffs:** the layout leans on hand-tuned `minmax()` tracks and many fractional inline sizes, so column balance is fragile between ~1024–1280; the wire overlay depends on "nothing is sticky / scroll the canvas as one unit," which is correct but brittle to future layout edits.

### Current UX issues
- **Spacing is off any single rhythm.** Padding/gaps/font sizes are pervasively fractional (9.5/10.5/11.5/12.5/13.5px; `3px 11px`, `9px 11px`, gaps 5/6/7/10/12) and almost entirely inline-styled rather than tokenized — violates "ONE 4/8px scale."
- **Glyph zoo / non-Lucide icons.** Arrows and marks are literal Unicode (`←`, `→`, `▾`, `▸`, `›`, `‹`, `⠿`, `✦`, `�e2`, `⚠`, `✕`, `◉`/`○`) plus ad-hoc inline SVGs. Inconsistent with a Lucide system; some are decorative-only without aria.
- **Status/badge inconsistency.** Many parallel pill styles: `UnifiedStatusBadge`, the invoice pill, dead-letter pill, "unmapped"/"needs a value"/"auto"/"not set" tags, AI "✦ AI %", catalog/validation badges, readiness chips. They share intent but not one shape/size/padding/icon system — violates "ONE status-badge system."
- **Two different "fix" surfaces for the same work.** Required/unmapped lines surface both in the top `IssuesPanel` (with inline code entry) AND inline in the OutgoingPane "SUGGESTED … Apply" strip. Powerful but potentially redundant/confusing about where the canonical action is.
- **Disabled-Send color reads as a second green.** The disabled Send is muted green `#5A7660` (not a neutral gray), which can read as "ready." A clearly neutral disabled state would better honor "ONE primary action, dominant."
- **Brand discipline drift on color-as-meaning.** Several states lean on tint alone (preview green wash, amber strips) with text that's sometimes low-contrast gray-on-tint (`#98A0AE`, `#AEB6C4`, `#7A8395`) — risk of < 4.5:1.
- **The mapper toolbar is busy.** "Map this order", mapped chip, saving status, AI-unavailable, Show/Hide connections, required warning, Customize output layout, Fill from catalog, Save mappings, Send — a lot competes; the single dominant action (Send) is duplicated between header and toolbar in some host configs.
- **Output paths still leak machine names as the secondary line** (`cbc:ID`, `BEG03`, `OrderRequestHeader@orderID`) — correctly demoted under the human label, but the mono second line is dense; for a coordinator it could be hidden behind a "show standards" disclosure.
- **Preview pane prominence is calibrated by magic numbers** (12px / specific heights) and uses its own navy code surface — good signature, but its 52px header + 46px format bar + info/error strips stack tightly with no spacing token.
- **Focus rings are partial.** Many controls are raw `<button>` with inline hover only; focus-visible is applied in places (drawer close) but not uniformly across the dense row chips (22px ƒx/= value chips are below 44px and have no visible focus ring).

### Redesign recommendations (for Claude Design)
1. **Tokenize the whole screen onto the 4/8 scale + one type scale.** Replace the fractional inline px (font sizes, paddings, gaps) with CSS variables; lead hierarchy by size+weight (heading 600 / label 500 / body 400), not color. This is the single biggest polish win given how inline-heavy the file is. (Bar 1, 2)
2. **Unify every pill into ONE badge component** — one shape/size/padding, green/amber/red/neutral semantics, always icon-or-word: status badge, invoice/dead-letter, output row status tags, AI chip, catalog/validation, readiness chips. (Bar 4)
3. **Make Send unmistakably the one primary action.** Keep green ≥44px in the header; make the disabled state neutral gray (not muted green); demote the in-toolbar Send to avoid duplication; keep secondaries (Details, Save mappings, Customize layout, Fill from catalog) as outline/ghost. (Bar 7)
4. **Converge the "fix a line" path.** Decide one canonical resolution surface (the inline OutgoingPane AI-fix strip vs the top IssuesPanel) or make the IssuesPanel a summary-that-jumps and the row the editor — eliminate the sense of two competing places. (Bar 6 honesty, Bar 5 density)
5. **Standardize Lucide icons** for arrows/marks/grips; keep the blue grip / green port semantics but as consistent iconography; ensure every icon-only control has an aria-label. (Bar 9)
6. **Lift contrast on all tinted/secondary text** to ≥4.5:1 (audit `#98A0AE`, `#AEB6C4`, `#7A8395`, `#566982` on tints), so hierarchy survives without relying on faint gray. (Bar 2)
7. **One overlay elevation system.** The screen has popovers (`SourcePickerChip`, `TransformPopover`, Add-field), a drawer, a full-screen designer, and a modal — give them one radius, one border color, and two shadow tiers (popover vs modal/drawer). Animate from trigger; all already have Esc/scrim/close — keep that, make it consistent. (Bar 8)
8. **Make the dense row chips touch-safe + focusable.** The 20–22px "= value" / "Edit value" / port chips need ≥44px hit areas (padding can stay tight visually with a larger hit box) and a visible focus-visible ring + pressed state. (Bar 9)
9. **Standards visibility as disclosure, not default density.** Keep the human field name as the lead; move the `cbc:ID`/`BEG03` machine path behind an info-icon popover or a per-pane "Show standards" toggle so coordinators see plain language and experts can reveal mappings. (CLAUDE.md standards-visibility rule, Bar 2)
10. **Keep the navy + violet brand and the signature blue→green "received → supplier" gradient,** the navy preview code body, and green=output/red=blocking/amber=warning — this is a polish pass, so preserve the visual canon while fixing the rhythm, badges, contrast, and focus. Confirm mobile stays STACKED (MobileTriage) rather than a broken canvas. (Bar 10)
