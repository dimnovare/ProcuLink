## 06. Supplier setup hub (detail) — `/library/suppliers/[id]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/suppliers/[id]/page.tsx` (thin server wrapper → renders `<SupplierDockProfile id={id} />`)
- **Key components:**
  - `src/components/bridge/SupplierDockProfile.tsx` (the whole page: header + 7 tabs; also hosts `AcceptanceTab`, `LiveMappingsTab`, `CatalogTab`, `CatalogPushCard`, `SupplierRuleBindingsPanel`, `LiveEditNotice`, `SrcChip`, `MiniStatusPill`)
  - `src/components/bridge/PoMappingEditor.tsx` (PO Mapping tab — magic auto-map drag/wire editor; also `TemplatePicker`, `ConfidenceChip`, `ColumnCombobox`, `SourceStatus`, `SaveFeedback`)
  - `src/components/bridge/DeliveryConfigEditor.tsx` (Delivery tab)
  - `src/components/bridge/CatalogSourceEditor.tsx` (auto-sync inside Catalog tab; also `TestReport`)
  - `src/components/bridge/ConnectorRequirementsPanel.tsx` (inside Delivery)
  - `src/components/bridge/StandardsFieldPopover.tsx` + `src/components/bridge/StandardsRefList.tsx` (standards mapping popovers/lists)
  - `src/components/connections/SupplierHistoryTab.tsx` → `HistoryContent` (from `HistoryDrawer.tsx`), `useConnectionRevisions`, `ConnectionNotice` + `ConnectionConfirmDialog` (from `ConnectionLifecycleUI.tsx`)
  - `src/components/bridge/layout/PageShell.tsx`, `src/components/bridge/BridgeLoader.tsx` (route `loading.tsx`)
- **Capture URL (mock):** `/library/suppliers/s1` — **important:** in mock mode (`NEXT_PUBLIC_USE_MOCK=true`) the page renders the rich `DEMO_MOCK` ("Acme Components", code `ACME`, health 97%, 1,284 orders) for **any** id, because the header/Overview branch keys off `isApiMockMode` and not the id. The id `s1` matches `DEMO_MOCK.id`; the prompt's UUID `22222222-2222-2222-2222-222222222222` is a backend test fixture, not a frontend mock — it renders identically in mock mode. Append `?tab=overview|mappings|catalog|po-mapping|delivery|acceptance|history` to land on a specific tab.

### What it is & why it exists
This is the per-supplier (or per-customer, for inbound orgs) **setup hub** — the place a coordinator configures everything ProcuLink needs to turn a buyer PO into the exact file this one supplier accepts and deliver it. It sits at the `transform → deliver → learn` end of the workflow: SKU code mappings (learn), PO column layout (normalize), validation rules (the acceptance gate), the output format + delivery channel + credentials (transform/deliver), the product catalog (grounds AI suggestions), and a version history of every saved config. A coordinator opens it during onboarding for a new supplier and whenever a supplier changes its requirements, an endpoint, or a code.

### Who uses it & the primary job
**Procurement coordinator** (with an integration-leaning mindset for the Delivery/credentials tab). The single most important task: **make this supplier deliverable** — set the required output format + a working delivery channel (Delivery tab) and prove it with a test-fire, so orders for this supplier can be sent automatically.

### Layout & structure (current)
`PageShell variant="wide"` on the grey canvas (`#F6F7FA`), a single full-width column, no max-width card; everything is flat panels.

1. **Back link** — `‹ Suppliers` (or `‹ Customers`), ghost button, muted.
2. **Detail header row** (`flex`, stacks on mobile): 48×48 green-soft avatar tile with a `Truck` glyph · `h1` supplier name at 24–26px Bricolage Grotesque · inline meta row beneath (mono code `ACME` · in mock: a format `SrcChip` "cXML" · a neutral channel chip "HTTP" · a green "Auto-process: ON" pill). Right side: **History** button (outline, only when a versioned connection exists) + **Delete supplier** button (outline, red text + `Trash2`).
3. **Tab strip** — horizontal, 44px tall, bottom hairline, horizontally scrollable with a right-edge fade gradient; 7 tabs: **Overview · Mappings · Catalog · PO Mapping · Delivery · Validation rules · History**. Active tab = ink text + 2px blue (`#1E66C9`) underline; inactive = muted, transparent underline.
4. **Tab body** (`pt-4`, scrolls):
   - **Overview:** a 4-up KPI grid (`grid-cols-2 lg:grid-cols-4`) of `.monument` stat cards (Total orders / Avg cycle time / Exception rate / Acceptance; 30px values) — **all show `—` "no data yet" in real mode**; only mock shows real numbers. Below, a 2-col grid (`lg:grid-cols-2`): "Delivery summary" card (key/value rows: Required format, Delivery channel, Endpoint, Standards profile, Saved SKU mappings, Last delivery — mock only; real mode shows a "Configure this supplier in the Delivery tab" link) and "Recent deliveries" card (PO id · amount · `MiniStatusPill` — mock only; real mode "No deliveries yet").
   - **Mappings:** one card; header (Link2 icon, "Saved SKU mappings", "Add mapping" outline link → `/library/mappings`). Body = a table (Buyer code / Supplier code / Source / Confidence) with mono codes, a source `chip` (AI=violet, Manual=neutral, Inherited=blue, Imported=green) and a `conf` chip; on `<sm` it becomes stacked row-cards. Mock adds a Description column; live (`LiveMappingsTab`) drops Description (no backend field).
   - **Catalog:** title "Product catalog · {total}" + helper · green "Import CSV / XLSX" button + outline "Clear" · notice line · search input (when items exist) · a 5-col table (Code / Name / Unit / Price / Barcode) with a "Showing N of total" footer; plus a `<details>` "Keep the catalog in sync automatically" disclosure wrapping `CatalogSourceEditor` + `CatalogPushCard`.
   - **PO Mapping:** a sub-label ("Order file layout") + `LiveEditNotice` + `PoMappingEditor` (a bordered card with a blue→green top edge, header with "Apply starter template ▾" + `SourceStatus`, an optional amber apply-confirm strip, an optional violet AI banner, a "Connect columns → ProcuLink fields" toolbar with a "Show standards" toggle, then a two-panel wire canvas: left = detected source columns, right = canonical fields each with Accept/Edit/Reject + a standards "i" popover + `ConfidenceChip`; a "How we read the source file" options strip; footer with Delete mapping / required-fields status / "Save mapping").
   - **Delivery:** `LiveEditNotice` + `DeliveryConfigEditor` — a card with an "Auto-deliver" checkbox header, a left 220px protocol radio rail (HTTP/SFTP/FTPS/Email(SMTP)/Erply/Directo) + "How sending works" note, and a right form pane whose fields swap by protocol (output format select, conditional cXML credentials block, endpoint/host/port/timeout, auth section, `ConnectorRequirementsPanel`, a dark JSON config `<pre>` preview), and a footer with Delete / Test-fire / Save delivery.
   - **Validation rules** (tab labelled "Validation rules", code key `acceptance`): `LiveEditNotice` + `AcceptanceTab` — profile header card (ShieldCheck, version+status pill, Activate/Edit/Save buttons), a blue "How validation works" info card, a "Rules" card (read = compact table Scope/Field path/Operator/Value/Severity/Blocks; edit = per-rule form rows with a "+ Add common rule…" select and "Add rule"), then `SupplierRuleBindingsPanel` (read-only active bindings with a "Standards" expander).
   - **History:** when a connection exists, `SupplierHistoryTab` (version-history `Card` with `HistoryContent` — version list + Test/Make live/Restore lifecycle controls + live config + replay); otherwise a dashed "No versions yet" empty panel.

Density/type/spacing observations: heavy use of **inline `style={{}}` with hardcoded hex** (the file declares its own token consts that duplicate `globals.css`); font sizes are a scattered ladder (10px/10.5px/11px/11.5px/12px/12.5px/13px/14px/15px) rather than a clean scale; many borders are `1px solid #E5E8EE`/`#E5E8EE`/`var(--border)` referenced three different ways; the three editor children (PoMapping, Delivery, Catalog source) each re-implement their own card chrome, `Field` label component, and button styles independently, so chrome is close-but-not-identical across tabs.

### Data shown
- **Supplier identity** (`apiClient.getSuppliers()` → find by id): `id`, `name` (code is **derived client-side** via `deriveCode(name)`); metrics are honest `—` placeholders in real mode.
- **Versioned connection** (`listConnections()`): `connectionId` = first connection whose `supplierId === id`; gates the History button + `LiveEditNotice` link + History tab.
- **SKU mappings** (`apiClient.getSupplierMappings(id)`): `id`, `buyerItemCode`, `supplierItemCode`, `confidence` (0–1 → %), `source` (manual/imported/suggested/inherited). No `description` on backend.
- **Catalog** (`getSupplierCatalog(id, q, 200)`): `{ total, items:[{ id, code, name, unit, price, barcode }] }`; mutations `importSupplierCatalog`, `clearSupplierCatalog`. `CatalogPushCard` reads `getOrgSettings()` for the ingress slug.
- **Catalog auto-sync** (`getCatalogSource(id)`): protocol, host/port/url, username, remotePath, syncIntervalHours, fileFormat, isEnabled, `hasPassword`/`hasAuthConfig`/`authMethod`, last-sync status.
- **PO mapping**: `getMappingSourceColumns(id)` (detected columns + format + sample + hint + sourceOrderId), `suggestMappingFields(id)` (AI field suggestions + confidence), `getPoMappingTemplates()` (starter templates), `applyPoMappingTemplate`/`upsertPoMapping`/`deletePoMapping`. Config held in local state, not a query.
- **Delivery** (`getDeliveryConfig(id)`): protocol, autoDeliver, outputFormat, `configJson`, `hasCredentials`, `cxmlCredentials` (+ `hasSharedSecret`); mutations `upsertDeliveryConfig`/`deleteDeliveryConfig`/`testFireDelivery` (`DeliveryTestResult`: success, responseCode, errorMessage).
- **Acceptance** (`getAcceptanceProfile(id)`): `versionNo`, `status` (active/draft), `rules:[{ scope, fieldPath, operator, expectedValue, severity, blockOnFail }]`; `saveAcceptanceProfile`/`activateAcceptanceVersion`. Rule bindings via `getSupplierRuleBindings(id)` (`SupplierRuleBinding` + standards refs on `definition`).
- **History** (`useConnectionRevisions(connectionId)`): revisions list, activeRevisionId, liveSummary, testEvidence, lifecycle mutations (publish/rollback/archive/test).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| `‹ Suppliers` back link | `router.push("/library/suppliers")` | List page |
| Tab buttons ×7 | `setTab(id)` + scrolls active into view | Switches tab body in place (does not write `?tab=` back to URL) |
| **History** header button | `setTab("history")` | History tab (only shown when `connectionId` exists) |
| **Delete supplier** header button | `setConfirm(true)` | Opens delete-confirm modal |
| Overview "Delivery"/"delivery tab" link (real mode) | `setTab("delivery")` | Delivery tab |
| Mappings "Add mapping" link | `href="/library/mappings"` | Global Mapping Editor |
| Mappings empty-state "Mapping Editor" link | `/library/mappings` | Global Mapping Editor |
| Catalog "Import CSV / XLSX" | opens hidden file input → `importSupplierCatalog` | Imports; shows notice; invalidates caches |
| Catalog "Clear" | `confirm()` then `clearSupplierCatalog` | Native confirm → clears catalog |
| Catalog search input | `setQ` → refetch with query | Filters catalog table |
| Catalog "Keep in sync automatically" `<details>` | native disclosure | Reveals `CatalogSourceEditor` + `CatalogPushCard` |
| Catalog source protocol radios | `selectProtocol` (roving radiogroup) | Swaps host vs URL fields |
| Catalog source Test / Save / Delete | `testFetchCatalogSource` / `upsertCatalogSource` / `deleteCatalogSource` | Inline test report / save notice / delete (native confirm) |
| Catalog push "Copy" | `navigator.clipboard.writeText(url)` | Copies ingress URL; "Copied" 1.8s |
| Catalog push Settings/API links | `/settings?tab=api`, `/help/api-and-integrations` | Navigates |
| PO Mapping "Apply starter template ▾" | `TemplatePicker` open dropdown | Pick template → amber apply-confirm strip |
| PO Mapping per-field Accept / Edit / Reject | `handleAccept` / `handleEditStart` / `handleReject` | Mutates mapping state; wires redraw |
| PO Mapping `ColumnCombobox` | choose/confirm a source column | Sets accepted column |
| PO Mapping "Accept all" (AI banner) | `handleAcceptAll` | Accepts all pending suggestions |
| PO Mapping "Re-detect" / "Re-detect columns" | `sourceQuery.refetch()` | Re-fetches source columns |
| PO Mapping standards "i" button (per field) | shadcn `Popover` | Standards-mapping popover |
| PO Mapping "Show/Hide standards" | `setShowStd` | Reveals standards column |
| PO Mapping separator select / "Has header row" | `setSourceOpts` | Re-reads source file |
| PO Mapping "Save mapping" / "Delete mapping" | `onSave(config)` / `onDelete()` | Persists via `upsertPoMapping`/`deletePoMapping` |
| Delivery protocol radios ×6 | `selectProtocol` (roving radiogroup) | Swaps the form fields |
| Delivery "Auto-deliver" checkbox | `setAutoDeliver` | Marks edited |
| Delivery output-format select | `setOutputFormat` | cXML reveals credentials block |
| Delivery auth-type / SFTP auth-method selects | `setAuthType`/`setSftpAuthMode` | Swaps credential fields |
| Delivery SMTP / OAuth "Advanced" `<details>` | native disclosure | Reveals advanced fields |
| Delivery "Save delivery" | `upsertDeliveryConfig` | Saves; shows post-save test nudge |
| Delivery "Test-fire" (footer + nudge "Send a test now") | `testFireDelivery` | Inline verbatim result strip |
| Delivery "Delete" | `window.confirm` then `deleteDeliveryConfig` | Native confirm → delete |
| Validation "Edit rules"/"Add profile" / "Save rules" / "Cancel" | `startEdit`/`handleSave`/`setEditRules(null)` | Enters/saves/exits edit mode |
| Validation "Activate rules" | `activateAcceptanceVersion` | Activates draft version |
| Validation "+ Add common rule…" select / "Add rule" / "Remove" | `addQuickRule`/`addRule`/`removeRule` | Edits rule list |
| Rule-bindings "Standards" expander | `setOpenId` | Inline standards refs |
| History Test / Make live / Restore / Discard | hook mutations | Lifecycle actions → confirm dialog |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Delete supplier confirm** | Modal (custom, `fixed inset-0 z-50`, scrim `rgba(11,26,47,0.45)`) | "Delete supplier" header button | Title "Delete {name}?", body ("Past orders kept for audit, can't be undone"), inline delete-error box, Cancel + red "Delete supplier" | Backdrop click, Cancel button, or successful delete (→ `router.push("/library/suppliers")`). **No X icon, no Esc handler.** |
| **Apply-template confirm** | Inline panel (amber strip inside PoMappingEditor) | Selecting a template in `TemplatePicker` | "Apply the {name} starter?" + warning copy + inline apply-error, Cancel + "Apply template" | Cancel button, or successful apply. Not a true modal (no scrim/Esc). |
| **Starter-template dropdown** | Dropdown (`role="listbox"`, absolute, z-1000, shadow) | "Apply starter template ▾" button | List of templates (name + description) | Pointer-down outside (document listener), or selecting an item. **No Esc handler.** |
| **Standards-field popover** | Popover (shadcn/ui `Popover`) | per-field "i" button (PO Mapping) | Field label + UBL/EDIFACT/X12/cXML reference rows | Click outside / Esc (Radix default), trigger re-click |
| **Rule-bindings "Maps to"** | Inline expander | "Standards" button on a binding row | `StandardsRefList` panel | Re-click the button (`setOpenId(null)`) |
| **History confirm dialog** | Modal (`ConnectionConfirmDialog`, `role="dialog"` `aria-modal`, scrim `#0B1A2F66`, z-80, bottom-sheet on mobile) | Make live / Restore / Discard in History tab | Title ("Make vN live?" etc), body copy, Cancel + primary/danger confirm (with loading) | Cancel button, or confirm action. **No backdrop-click or Esc close wired here.** |
| **Native browser `confirm()`** ×3 | Browser dialog | Catalog "Clear", Catalog-source Delete, Delivery Delete | OS-native confirm text | OK / Cancel |
| **`ColumnCombobox` listbox** | Inline combobox dropdown | Edit a field's source column | Filterable list of detected columns | Select option / blur |
| **`SourceStatus` redetect popover/menu** | Small inline status control | `SourceStatus` in PO Mapping header | format + column count + re-detect | inline |
| Inline transient notices/strips | Inline status (not overlays) | save/import/test actions across tabs | success/error text; some auto-dismiss (`setTimeout` 1.8–3s) | timeout or next edit |

### States
- **Empty:** Handled well per surface. Mappings ("No saved SKU mappings yet" + Mapping Editor link), Catalog ("No products yet. Import a CSV/XLSX…"), Validation ("No validation rules yet…"), Rule bindings ("No active rule bindings…"), History ("No versions yet" dashed panel), PO Mapping ("No source columns detected. Upload a PO…" + Re-detect). Overview in real mode is effectively a **dead empty state** — four `—` cards plus two "configure/no deliveries yet" panels, so a freshly-created supplier's hero tab carries no signal or next action.
- **Loading:** Route-level `loading.tsx` → `BridgePageLoader` (skeleton). Page-level: real-mode initial load shows a bare centered text "Loading supplier…" (no skeleton). Mappings tab = a proper 3-row pulse skeleton. Catalog/Delivery/Acceptance/Rule-bindings = bare text "Loading …" (no skeleton). PO Mapping = "Detecting columns…" text.
- **Error:** Page-level real-mode error → centered "Failed to load supplier" + "← Back to suppliers". Not-found → "Supplier not found" + back link. Per-tab: Mappings (red sentence), Acceptance ("Failed to load acceptance profile."), Rule-bindings (red text + "↻ Retry" button), Delivery/Catalog-source (red error box), PO Mapping apply-template (inline red). Mostly reason-without-retry except rule-bindings.
- **Success/feedback:** Inline strips, not toasts. Catalog import notice, delivery save→test nudge ("Prove the connection with a test payload" + "Send a test now") and verbatim test-fire result with the honesty caveat ("a successful test means their endpoint answered — it doesn't mean an order was accepted"), acceptance "Rules saved as draft" / "Version activated", catalog-source save notice + connect/preview report.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full layout; Overview 4-up KPIs + 2-col cards; PO Mapping shows the two-panel **wire/SVG canvas**; Delivery shows the 220px protocol rail + form (`lg:grid-cols-[220px_1fr]`); tables full-width.
- **Tablet 768:** KPIs drop to 2-up; the 2-col Overview cards and the Delivery left-rail/right-form collapse toward stacked (`lg:` breakpoints, so they stack below 1024). Tab strip scrolls horizontally with the right-edge fade.
- **Mobile 390:** Header stacks (avatar/name then actions). Tables → stacked row-cards (Mappings, Acceptance rules). **PO Mapping drops the wire canvas entirely** (`isMobile` → no SVG; columns stack and each field spells out "from: {column} · e.g. {sample}" + a "N of M matched" progress chip) — correct behaviour. Delivery/Catalog editors stack to single column; footers become full-width stacked buttons.
- **Cliffs:** The protocol rails and 2-col Overview cards collapse at `lg` (1024px), so the 768–1023px tablet band gets a wide single-column form (a lot of empty right gutter). The tab strip has 7 tabs and **always** relies on horizontal scroll + a fade rather than wrapping — on narrow desktop the last tabs (Validation rules, History) sit off-screen.

### Current UX issues
- **Two competing card systems / ad-hoc chrome (Bar 8).** SupplierDockProfile, PoMappingEditor, DeliveryConfigEditor and CatalogSourceEditor each declare their own hex tokens and re-implement card borders (`#E5E8EE` vs `var(--border)`), radii (6/7/8/10/12px all appear), buttons, and a private `Field` label component. Radius and border colour drift between adjacent panels in the same tab.
- **Spacing is not on one 4/8 rhythm (Bar 1).** Padding values like `py-3.5`, `px-4 py-3`, `py-2.5`, `8px 12px`, `11px 14px`, `6px 10px` are mixed freely; vertical gaps jump 6/8/10/12/14/16/18px.
- **Type scale sprawl (Bar 2).** At least nine font sizes between 10 and 15px; hierarchy is often carried by colour (muted greys, `var(--ink-faint)`) instead of size+weight, and several muted-on-light label rows (10.5px uppercase `#5E6779`/faint) are at risk below 4.5:1.
- **Numbers aren't consistently tabular (Bar 3).** Overview KPI values, the catalog Price column, confidence %s and counts don't all use `tabular-nums` (only the `conf`/`tabular-nums` chips do), so figures jitter and prices don't right-align cleanly.
- **Status/health pills are not one system (Bar 4).** Mock header uses `.pill pill-ready`; Mappings uses `.chip` source pills; Acceptance uses a custom version pill + coloured severity dots; History uses its own lifecycle pills; the catalog-source last-sync uses a coloured dot. No single badge shape/size/semantics.
- **Tables aren't one density (Bar 5).** Mappings table (`px-5 py-3`) vs Catalog table (`6–7px 10px`) vs Acceptance table (`px-4 py-3`) vs the test-report tables all differ in row height, padding and header styling; no sortable affordance/`aria-sort`; gridlines use several different greys.
- **Overview is a weak/dead hero in real mode (Bar 6).** Four `—` cards and two "nothing yet" panels mean the default landing tab shows no actionable next step; a real coordinator's first impression is empty placeholders.
- **No single dominant primary action (Bar 7).** Each editor tab has its own dark-navy "Save …" plus a Test button plus a Delete button, all ~32–34px and similar weight; the page-level "make this supplier deliverable" goal is never visually elevated. Green is reserved for save inside Catalog but navy elsewhere — inconsistent primary colour.
- **Modal/overlay inconsistency & a11y gaps (Bars 8/9).** The delete modal has no X and no Esc/`role=dialog`; the apply-template "confirm" is an inline strip, not a modal; the History confirm dialog has `aria-modal` but no backdrop/Esc close; the template dropdown has no Esc. Three native `confirm()` calls bypass the design system entirely.
- **Tab strip relies on scroll, not wrap (Bar 10 / nav).** 7 tabs + a fade gradient means deeper tabs (Validation rules, History) are easy to miss; there's no breadcrumb beyond the back link, and `?tab=` isn't reflected back into the URL on manual clicks (so deep state isn't shareable).
- **Loading states are mostly bare text (Bar 6).** Only Mappings has a real skeleton; Delivery/Catalog/Acceptance/page-initial use lone "Loading…" strings.
- **Jargon leakage in Validation read-table.** The read-mode rules table shows raw `fieldPath`/`operator` (e.g. `supplierItemCode` / `greater_than`) even though human labels (`OPERATOR_LABELS`, `FIELD_OPTIONS`) exist for the editor — leads with machine names, not human ones.
- **Mock-vs-real divergence.** Overview/Delivery-summary/Recent-deliveries are richly designed only in mock; real mode is far thinner, so the "designed" version a viewer sees in mock overstates the real page.

### Redesign recommendations (for Claude Design)
1. **Unify all panel chrome into one Card primitive** (one radius, one border `gray-200`, one shadow tier) and delete the four private token blocks + per-file `Field` components; reuse the shared `Card`/`DSPrimitives.Button`. Keep navy `#0B1A2F` + violet brand, green=save/success, red=blocking, amber=warning. (Bars 8, 7)
2. **Make Delivery the clear primary path.** Promote one green ≥44px primary "Save delivery" (and a prominent "Test-fire" secondary), demote Delete to a quiet/destructive-separated ghost; carry the page goal ("make this supplier deliverable") as a header status (e.g. "Not yet deliverable / Deliverable ✓ / Tested ✓"). Honour "200 ≠ acceptance" copy already present. (Bars 7, 9)
3. **Redesign Overview into a real status hub.** Replace the four `—` cards with a setup-progress summary (mapping set? rules active? delivery configured + tested? catalog imported?) each linking to its tab, plus the latest delivery; only show numeric KPIs once data exists. Give it a real skeleton. (Bars 6, 5)
4. **One badge system everywhere** — a single pill shape/size/padding for source provenance, acceptance version/status, severity, delivery health and last-sync, each with green/amber/red/neutral + icon-or-word (never colour alone). Replace severity dots and the dim version pill. (Bar 4)
5. **One table density.** Single row height, cell padding, low-contrast `gray-200` gridlines, sticky header, hover, and `aria-sort` affordances applied to Mappings, Catalog, Acceptance and the test-report tables; `tabular-nums` on every code/qty/price/%/count and right-align money. (Bars 3, 5)
6. **Standardise overlays.** One modal component (scrim, animate-from-trigger, X + Esc + backdrop close, focus trap) for the delete confirm, apply-template confirm (convert the inline amber strip into a real modal), and History lifecycle confirm; one popover style for standards. Replace the three native `confirm()` calls with it. (Bars 8, 9)
7. **Fix the tab strip / nav.** Let tabs wrap or use a responsive overflow menu so Validation rules + History are never hidden; reflect the active tab into `?tab=` for shareable deep links; add a breadcrumb (Library › Suppliers › {name}). (Bar 10, nav)
8. **Normalise spacing + type to the 4/8 scale and a 6-step type ladder**, carrying hierarchy via size+weight; audit all muted-grey labels for ≥4.5:1 contrast. (Bars 1, 2)
9. **Lead Validation with human names** in read mode (use the existing `OPERATOR_LABELS`/`FIELD_OPTIONS` labels), keeping the raw `fieldPath` as secondary mono text — matching the editor and the project's "lead with the human field name" rule.
10. **Real loading skeletons** for Delivery, Catalog, Acceptance and the page-initial load, matching the Mappings skeleton pattern. (Bar 6)
11. **Mobile:** keep the PO-Mapping triage fallback (it's already correct), but ensure the tab strip primary actions and the per-editor Save are reachable without horizontal scroll, and stacked footers stay ≥44px. (Bar 10)
