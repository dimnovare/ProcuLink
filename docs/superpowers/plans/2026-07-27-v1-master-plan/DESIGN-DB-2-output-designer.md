# DB-2 — The Output Designer

_Design spec produced 2026-07-30 from 02-DESIGN-BRIEFS.md. Feeds the packets named in the brief._

## Code actually read

- src/components/bridge/OutputStructureDesigner.tsx:33-35 — FORMATS is exactly [json, xml, csv]; cXML/UBL/X12 are simply absent from the segmented control, with the reasoning in the comment above it (envelope/DOCTYPE/profile ids not expressible as a generic tree)
- src/components/bridge/OutputStructureDesigner.tsx:44-51 — designerFormat() coerces cxml|ubl|x12 → "xml" and anything unknown → "json"
- src/components/bridge/OutputStructureDesigner.tsx:146-148 — the coercion is applied when SEEDING state from initialTree, and :239-249 save() writes `tree` verbatim, so opening + saving a cXml tree silently rewrites it to xml. Confirmed silent-rewrite bug.
- src/components/bridge/OutputStructureDesigner.tsx:71-81 — FORMAT_PRESETS is 8 date/number/currency presets; FORMAT_TYPES = {DateFormat, NumberFormat} only. No other manipulator is reachable.
- src/components/bridge/OutputStructureDesigner.tsx:495-500 — setFormatPreset() KEEPS non-format manipulators (`others`) but nothing ever renders them: a promoted/API-authored Trim/Replace/Split on a node is invisible and uneditable.
- src/components/bridge/OutputStructureDesigner.tsx:471-474, 478-488 — updateName / remove / addChild / setBinding. setBinding rebuilds `rule` from a fixed key set {outputPath, canonicalField, sourceToken, fixedValue, fieldManipulators} — any other rule key is DROPPED (see Expression finding below).
- src/components/bridge/OutputStructureDesigner.tsx:459 — `editing` is a single-slot union "name|format|condition|namespace"; one editor per row, no reorder state, no transform state.
- src/components/bridge/OutputStructureDesigner.tsx:657-671 — the conditional is a raw text input writing OutputNode.includeWhen, with a one-line example. No structured builder, no tester.
- src/components/bridge/OutputStructureDesigner.tsx:787-814 — NamespaceEditorRow: hand-typed prefix + URI, no presets. :822-881 RootNamespacesEditor: same, as prefix→uri rows.
- src/components/bridge/OutputStructureDesigner.tsx:372-380 — the per-node/root namespace collision warning uses #9A6B1E on #FFF7E8 = 4.39:1, FAILS WCAG AA for text, and offers no action ("clear the per-element namespaces" with no control to do it).
- src/components/bridge/OutputStructureDesigner.tsx:267-272 — role="dialog" + aria-label only. No aria-modal, no focus trap, no Escape handler, no focus restore. Inputs are 12px/26-30px tall throughout (fails the 16px iOS zoom floor and 44px tap minimum).
- src/components/bridge/OutputStructureDesigner.tsx:120-133 — useIsNarrow() at max-width 860px; :316-318 collapses to a single scrolling column with the dark preview BELOW the whole tree.
- src/components/bridge/OutputStructureDesigner.tsx:157,165,332-365 — firstRun empty state: one textarea + "Build from a sample" + "Start blank". Paste only, no file input, no starting shapes, no format guidance. :190 format is auto-detected from the sample's first character.
- src/components/bridge/outputNamespaceModel.ts:21-34 — updateAt / removeAt are the ONLY tree edit primitives. There is no move/reorder operation anywhere.
- src/lib/api/types.ts:48-65 — the frontend OutputFieldRule has NO `expression` field, while the backend record does (see next). Every designer write path reconstructs `rule` from the 5 known keys, so an Expression on a promoted rule is silently destroyed on any edit.
- src/lib/api/types.ts:319-328 — MANIPULATOR_TYPES: 8 entries (Trim, Replace, DateFormat, Concat[suffix], Fallback, Split, Multiply, Divide). Concat takes ONE param (a literal suffix) — there is no two-field join manipulator.
- src/lib/api/types.ts:331-332 — CANONICAL_HEADER_FIELDS = 5 names, CANONICAL_LINE_FIELDS = 8 names.
- src/lib/api/types.ts:442 + 406-413 — OutputFormatId is exactly xml|csv|cxml|json|ubl|x12; PREVIEW_FORMATS offers all six for preview while the designer offers three.
- src/components/bridge/mapper/MapperWorkbench.tsx:781-796 — the designer mounts ONLY when `variant === "order"`. There is no supplier/connection-scoped designer; a layout is per-order by construction.
- src/components/bridge/mapper/MapperWorkbench.tsx:952-960 — the "Save mappings" (promote) ToolbarButton renders only if `onSaveMappings` is passed. `git grep onSaveMappings=` over src returns ZERO hits, and `promoteMapping` has zero call sites outside api-client.ts:1877. Verified WP-13's claim independently.
- src/components/bridge/SupplierDockProfile.tsx:32,105-110 + 1690-1699 — supplier tabs are overview|mappings|catalog|po-mapping|delivery|acceptance|history. Output format + channel are configured under tab==="delivery" (DeliveryGuidedSetup + DeliveryConfigEditor). There is no output-layout surface on the supplier.
- src/components/bridge/OutputMappingEditor.tsx:113-123 — buildExpressionTestDraft() already exists and renders ONE Scriban expression server-side against the real order via the preview endpoint, nulling outputTree so the tree can't hijack it. Reusable verbatim as the condition/expression tester.
- src/components/bridge/OutputSourcePicker.tsx:69-91,271-306 — describeBinding computes a `sample` value and puts it only in the title attribute; the compact (designer) trigger renders the label alone, so the row never shows the resolved value.
- src/app/globals.css:9-121 — token set (--brand-blue #1E66C9, --brand-green-deep #1E6D29, --ink-muted #5E6779, --ink-faint #667085, --amber-text #8A5310, --danger #B43838, --ai #6F4FCE, --tap-min 44px). No token has ≥3:1 against white: --border-strong #CBD0DA is 1.55:1.
- src/app/globals.css:127-147 — a global :focus-visible ring (2px --brand-blue + 4px halo) already exists, so focus visibility is inherited rather than per-component.
- ProcuLink.Core/Services/Mapping/OrderMappingOverride.cs:187-235 — OutputFieldRule HAS `Expression` (free-form Scriban), precedence Expression → SourceToken → FixedValue → CanonicalField, documented with `{{ order.Currency }}-{{ line.SupplierItemCode }}` as an example. The brief's "join two fields with a dash" is already an engine capability with no frontend surface.
- ProcuLink.Core/Services/Mapping/OutputNode.cs:39-100 — Name, NodeType, Children, Rule, Collection, Namespace, Prefix, IncludeWhen. No value-type field, so typed JSON leaves need one added.
- ProcuLink.Core/Services/Mapping/OutputNode.cs:54-57 — Array.Collection supports only "lines"; null defaults to lines.
- ProcuLink.Transform/Output/OutputTemplateEmitter.cs:56-78 — format switch: Json/Xml/Csv emit; CXml/Ubl THROW ArgumentException with the envelope reasoning; anything else throws. So a saved cXml tree is a delivery-time crash, not a degraded output.
- ProcuLink.Transform/Output/OutputTemplateEmitter.cs:130-133 — WriteJsonValue's default case is w.WriteStringValue(...). Every JSON leaf is a string, confirmed.
- ProcuLink.Transform/Output/OutputTemplateEmitter.cs:281-315 — EmitCsv: header row from root's Field children + first Array item's Field children, `string.Join(",", …)` and `sb.AppendLine(...)`. Delimiter is a hardcoded comma; the newline is StringBuilder.AppendLine → Environment.NewLine (LF on the Linux container). There is NO trailer/footer row concept.
- ProcuLink.Transform/Output/OutputTemplateEmitter.cs:306-308 — CSV applies IncludeWhen to the line ITEM only (drops the row); a conditional COLUMN cannot vary per row in a fixed grid.
- ProcuLink.Transform/Output/OutputTemplateEmitter.cs:151-157 — the emitter throws when root-map AND per-node namespaces are both set, and when any node has a Prefix without a Namespace. Both are design-time-detectable.
- ProcuLink.Transform/Output/OutputTemplateEmitter.cs:343-365 — ShouldEmit fails OPEN on an unevaluable predicate and only logs a warning; the user never learns their condition was ignored.
- ProcuLink.Transform/Output/OutputTemplateEmitter.cs:40,320-329 — the tree path calls GuardResolved only. `grep -rn OutputFieldValidator` shows Csv/Cxml/Json/Ubl/X12/Xml TransformService all call ValidateEntity; OutputTemplateEmitter does NOT. The tree path skips those checks, confirmed.
- ProcuLink.Transform/Output/MappedTransformService.cs:592-597 — Escape() is RFC 4180 minimal quoting (quote only when the value contains , " CR or LF), hardcoded to comma. Quote policy and encoding are not parameterised.
- ProcuLink.Transform/Output/MappedTransformService.cs:~321-345,~390-415 — the header bag is 10 keys (PoNumber, OrderDate, BuyerName, Currency, SupplierName, SubTotal, TaxTotal, GrandTotal, PaymentTerms, RequestedDeliveryDate) and the line bag adds 11 (…LineAmount, TaxRate, DeliveryDate). No ShipTo/BillTo/Contact/Incoterms. The FE picker exposes only 5 + 8 of these.
- ProcuLink.Core/Services/ITransformService.cs:5-24 — OutputFormat: Xml, Csv, CXml, Json, Ubl, X12 are deliverable; UblOrder, X12_850, EdifactOrders are conformance-profile identifiers only. There is no EDIFACT ORDERS output transform and no Peppol BIS *order* transform (PeppolBisInvoiceTransformService is an invoice), so the "standard documents" list must be exactly cXML 1.2 / UBL 2.1 / EDI X12 850.
- ProcuLink.Transform/Output/OutputFieldValidator.cs:55-113 — CollectEntityProblems: NeedsReview, missing SupplierItemCode, missing BuyerItemCode for X12/EDIFACT, negative UnitPrice, non-positive Quantity. These are ORDER problems, not layout problems — the distinction the error state must draw.
- ProcuLink.Transform/Output/OutputNodeTemplateInferrer.cs:8-17,205-216 — deterministic, no AI, no network; JSON/CSV/XML; an unmappable column emits FixedValue = null (UNBOUND) deliberately so the designer can show it as TODO.
- src/components/ui/confirm.tsx:1-80 — useConfirm() is a Radix AlertDialog, already focus-trapped and Escape-closing; the correct primitive to reuse for every confirm in this spec.
- ProcuLink/docs/superpowers/plans/2026-07-27-v1-master-plan/01-WORK-PACKETS.md (branch origin/docs/v1-master-plan) WP-12/13/14/15/16 — read in full; WP-12 adds OutputTree to PromoteMappingService as additive JSONB on SupplierPoMapping.ConfigJson with a byte-parity AC, WP-13 wires the promote control, WP-14 widens the canonical row, WP-15/16 are this brief's two halves.

## Founder decisions this spec cannot make

- PLAN GATE — this is the commercial decision that most changes the screen. CLAUDE.md §11.5 assigns "custom output templates" to Integration (€999). Taken literally the differentiator is invisible below €999. I have specced: design + preview on every plan (including read-only Pilot), "Use for every order" from Operations, "Copy from another supplier" from Integration. Confirm or overrule — §6 of the spec is written against that split and the gate copy changes with it.
- NOUN — the locked list has both "Order layout" and "Output". I standardised on "Output layout" (matches the shipped MapperWorkbench button "Customize output layout") and reserved "Order layout" for the inbound/source side. Confirm, or say the word is "Order layout" on both sides and I will collapse them.
- CSV LINE-ENDING DEFAULT — today it is Environment.NewLine, i.e. LF on Railway. Changing the default to CRLF changes the bytes every existing CSV supplier receives. I specced: new layouts default to CRLF, existing layouts with no recorded dialect keep LF and show a one-time "Set line endings" nudge. That preserves byte-parity but leaves silently-wrong files in place until someone clicks. Alternative is a one-time migration with a notice. Your call.
- CSV TOTAL/TRAILER ROW — the coordinator's own words in this brief ("a total line only when the order is over 5000 euro") are NOT expressible by the emitter: EmitCsv writes a header row plus one row per line and has no trailer concept, and a CSV IncludeWhen can only drop a whole row. §9.4 specs the minimal backend addition (a `trailer` node under root, emitted once after the lines, honouring IncludeWhen at header scope). Either fund that inside WP-15 or accept that the flagship JTBD in this brief is not satisfied — but it must not be faked in the UI.
- SUPPLIER-SCOPED ENTRY — §2 makes the designer a real page at /library/suppliers/[id]/output and mounts the same component as a sheet from the order. That is the only way "see which supplier a design governs" can exist, and it is more than WP-15/16's stated file list. Confirm the route is in scope, or the reuse moment ships with only the footer commitment and no place to see the result.
- AI SCOPE — §11 keeps AI to three read-only/propose-only jobs (raise recall on unbound-leaf field guesses, restate an emitter error in plain language, and propose a starting tree from pasted spec PROSE with every leaf left unbound). It explicitly forbids AI authoring conditionals, namespaces, CSV dialects, typed values and reuse scope. Confirm the third one (spec-prose → tree) is wanted at all — it is the only one that sends supplier text to a model, so it breaks the designer's current no-egress property and needs to be opt-in per workspace.
- EXPRESSION EXPOSURE — OutputFieldRule.Expression already exists on the backend and is missing from the frontend type, which means the designer currently DESTROYS it on any node edit. §7 fixes the data loss and uses Expression to implement "Combine fields" with zero backend work. Confirm you want a raw-expression escape hatch visible per node at all (§10 puts it behind an Advanced disclosure with a tester), or whether Combine-fields-only is the ceiling for v1.

---

# DB-2 — The Output Designer

**Feeds WP-15 (depth I) and WP-16 (depth II). Written against `OutputStructureDesigner.tsx` @ 881 lines, `OutputTemplateEmitter.cs` @ 381 lines, both read in full.**

---

## 0. What is actually true today

Before any recommendation, the eleven facts this spec is built on. Every one verified in code, not inferred.

| # | Fact | Evidence |
|---|---|---|
| 1 | A layout is **per-order by construction**. The designer mounts only when `variant === "order"`. | `MapperWorkbench.tsx:781` |
| 2 | The promote control **never renders**. `onSaveMappings=` has zero occurrences in `src`; `promoteMapping()` has zero call sites. The help article documents a button that does not exist. | `MapperWorkbench.tsx:952`, `api-client.ts:1877` |
| 3 | There is **no move operation** in the tree model. `updateAt` and `removeAt` are the whole API. | `outputNamespaceModel.ts:21-34` |
| 4 | Every JSON leaf is a string. `WriteJsonValue`'s default case is `WriteStringValue`. | `OutputTemplateEmitter.cs:130-133` |
| 5 | CSV delimiter is a hardcoded `,`; the newline is `StringBuilder.AppendLine` → `Environment.NewLine` → **LF on the Linux container**; quoting is fixed RFC 4180-minimal; encoding is fixed UTF-8-no-BOM. | `OutputTemplateEmitter.cs:297,311`, `MappedTransformService.cs:592-597` |
| 6 | Six of the eight manipulators are unreachable from the designer, and `setFormatPreset` **preserves them invisibly** — a promoted node can carry a `Trim` nobody can see or remove. | `OutputStructureDesigner.tsx:81,495-500` |
| 7 | `OutputFieldRule.Expression` **exists on the backend** (precedence `Expression → SourceToken → FixedValue → CanonicalField`, documented with `{{ order.Currency }}-{{ line.SupplierItemCode }}`). It is **absent from the frontend type**, and every designer write path rebuilds `rule` from five known keys — so **the designer silently destroys an Expression on any edit**. | `OrderMappingOverride.cs:187-235` vs `types.ts:48-65`, `OutputStructureDesigner.tsx:478-488` |
| 8 | `Concat` takes **one** param (a literal suffix). There is no two-field join manipulator. The brief's "join these two fields with a dash" is only expressible via `Expression`. | `types.ts:323` |
| 9 | The tree path **skips `OutputFieldValidator`**. Csv/Cxml/Json/Ubl/X12/Xml `TransformService` all call `ValidateEntity`; `OutputTemplateEmitter` calls only `GuardResolved`. | `grep -rn OutputFieldValidator` |
| 10 | A falsy-but-unevaluable `IncludeWhen` **fails open and only logs**. The user is never told their condition was ignored. | `OutputTemplateEmitter.cs:353-363` |
| 11 | The dialog has `role="dialog"` and an `aria-label` and nothing else: **no `aria-modal`, no focus trap, no Escape, no focus restore**. Inputs are 12px and 26–30px tall throughout. | `OutputStructureDesigner.tsx:267,545,661,803` |

And the deliverable formats are exactly six — `Xml, Csv, CXml, Json, Ubl, X12` (`ITransformService.cs:9-14`). `UblOrder`, `X12_850`, `EdifactOrders` are **conformance-profile identifiers with no output transform**, and `PeppolBisInvoiceTransformService` is an *invoice*. So the honest standards list is three, not five. Do not write "Peppol" or "EDIFACT" as a producible order document anywhere in this UI.

---

## 1. The one-sentence job

> Set up the file this supplier needs, once, and see exactly what they will get.

Two nouns govern the screen: **Output layout** (the thing being designed) and **Supplier** (who it governs). Everything else is mechanics and must not become a taught concept. Banned from this surface: *tree, node, AST, revision, artifact, canonical, spine, template* (except in the one explicitly-labelled advanced mode), *idempotency, fingerprint*.

---

## 2. Where it lives — and why that is the first design decision

The reuse gap (#1) is not a missing button. It is a missing **scope**. A layout that can only be authored inside one order will always feel like it belongs to that order, whatever the button says. So:

**One component, two mounts.**

| Mount | Route | Scope | Primary commitment |
|---|---|---|---|
| Supplier | `/library/suppliers/[id]/output` — full page, new **Output** tab between *PO Mapping* and *Delivery* | The supplier's layout | `Save layout` |
| Order | Full-height right sheet from the Order Workshop toolbar (`Customize output layout`) | This order, with the supplier's layout as the base | `Use for every {Supplier} order` / `Just this order` |

The supplier page is the **home**; the order sheet is the **exception path**. That inversion is the whole fix. It also gives "SEE which supplier a design governs" somewhere to exist, which no amount of copy inside an order modal can.

The order mount stays a sheet, not a page: it is reached mid-review and must be dismissible without losing the review context. It is not a wizard and it does not hide the source *document* (the source values it needs are on the rows — see §3.4).

---

## 3. Annotated layout — 1440px (primary)

Dialog/page width `min(1320px, 96vw)`, height `min(92vh, 980px)`. Today's 1100px cap leaves the tree column at ~530px, which is why every row wraps. 1320 buys a real inspector.

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│ ▌ A  HEADER  (navy #0B1A2F, 56px)                                                      │
│ ▌ Output layout          ● Every Contoso order ▾    What does Contoso need?          │
│ ▌ The file Contoso receives                        [ CSV ][ XML ][ JSON ]   ✕ Close   │
│ ▌                                                   A standard document (cXML…)        │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ ▌ B  CONTEXT BAR  (white, 44px, only when it has something to say)                     │
│ ▌ File format · Comma · CRLF · UTF-8 · Column names   [Change]     XML namespaces: None│
├──────────────────────────────┬───────────────────────┬─────────────────────────────────┤
│ C  LAYOUT            520px   │ D  DETAILS      380px │ E  OUTPUT              380px    │
│                              │                       │ (dark #0B1626)                  │
│ Columns · 11                 │ quantity              │ WHAT CONTOSO RECEIVES · live   │
│                              │ Value                 │ CSV · text/csv · 412 bytes      │
│ ⠿ {} order                   │                       │ [Copy] [Download] [⏎ Show]      │
│   ⠿ 1 val orderNumber        │ Name in the file      │                                 │
│      ← PoNumber → PO-88421   │ [ quantity          ] │ order_no;item;qty;price         │
│   ⠿ 2 val orderDate          │ Exactly as Contoso   │ PO-88421;CTS-8891;10;12.50      │
│      ← OrderDate → 15/06/26  │ spelled it.           │ PO-88421;CTS-2214;4;89.00       │
│      ⟦Date · EU⟧             │                       │                                 │
│   ⠿ [] lines  repeats/line   │ Comes from            │                                 │
│     ⠿ {} line                │ [ Quantity        ▾ ] │                                 │
│       ⠿ 1 val item           │ → 10 for line 1       │                                 │
│          ← SupplierItemCode  │                       │                                 │
│       ⠿ 2 val qty  ⟦only…⟧   │ Change the value      │                                 │
│          ← Quantity → 10     │ Sent exactly as it    │                                 │
│                              │ arrives. [+ Add step] │                                 │
│  [+ Value][+ Group][+ List]  │                       │                                 │
│                              │ Include when          │                                 │
│                              │ ( ) Always include    │                                 │
│                              │ (•) Only include when │                                 │
│                              │   [Quantity▾][is more │                                 │
│                              │    than▾][ 0        ] │                                 │
│                              │   Rule: line.Quantity │                                 │
│                              │         > 0           │                                 │
│                              │   [Check this order]  │                                 │
│                              │                       │                                 │
│                              │ ▸ Advanced            │                                 │
├──────────────────────────────┴───────────────────────┴─────────────────────────────────┤
│ F  PROBLEMS STRIP (48px)  ✓ This layout will produce a valid file.                     │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ G  FOOTER (64px)  Saving for every order also applies to this one.                     │
│                        [Cancel]  [Just this order]  [ Use for every Contoso order ]   │
└────────────────────────────────────────────────────────────────────────────────────────┘
▌ = the existing 2px blue→green left edge (OutputStructureDesigner.tsx:275). Keep verbatim.
```

### 3.1 Region A — header

Three things, in priority order: **what this governs**, **what format**, **how to leave**.

The **scope chip** is the single most important new element on the screen and it is the answer to "which supplier does this govern". It is a button (opens a menu), 32px, always visible, four states in §5.

The **format fork** is not a format picker. See §9.

### 3.2 Region B — context bar

Only renders when it has content: the CSV dialect summary (CSV only) and the namespace mode (XML only). JSON shows nothing and the bar collapses. This is where WP-15's dialect and WP-16's namespaces become *visible facts* rather than buried panels — a wrong line ending is invisible in the preview and must be legible without opening anything.

### 3.3 Region C — layout (stays visual)

The tree answers exactly one question: **what does the file look like?** Shape, order, repetition, presence. Nothing else. Per row, left to right:

1. **Drag handle** `⠿` — 24×24 visual, 44×44 hit area. Also the keyboard reorder control (§6).
2. **Column index** — CSV only, on the root's value children and the line item's value children. This is the column order made literal; it is the reason reorder is legible at all in CSV.
3. **Type pill** — keep the existing four (`{ }` / `[ ]` / `val` / `@`) and the existing colours (`OutputStructureDesigner.tsx:423-428`). Change the `title` from the code word to `Group` / `Repeats for each line` / `Value` / `Attribute`.
4. **Name** — mono 13/700, click to edit inline (keep).
5. **Binding pill + resolved value** — `← SupplierItemCode → CTS-8891`. **New:** `OutputSourcePicker.describeBinding` already computes `sample` and throws it away in compact mode (`OutputSourcePicker.tsx:302-304`). Rendering it is the single highest-value density change on the screen, and it is what removes the need for a third "source" column. Truncate at 22ch, full value in `title`.
6. **State pills** — only when set: format, condition, namespace, transform-count, value-type. Max three visible, then `+2`.
7. **Row status bar** (existing `inset 3px 0 0 0`): grey container / green bound / violet fixed / **amber unbound** / **red has a Tier-1 problem**. The last two are new and are how the Problems strip (§8) points at the tree.

Clicking anywhere on the row that is not a control **selects** it and fills Region D. Selection is a persistent state (`selectedPath`), replacing today's per-row `editing` single-slot union (`:459`), which can only hold one editor per row and cannot survive a scroll.

### 3.4 Region D — details (the structured property editor)

A node has ≥9 properties. That is an inspector, not a popover. Sections, in this order, each collapsible, all closed-by-default except Name and Value:

`Name` · `Value` (binding → combine → transform stack → send-as) · `Include when` · `XML` (XML only) · `Advanced`

At 1440 Details sits *between* Layout and Output so the eye goes layout → property → result. It is empty-stated when nothing is selected: **"Pick a value on the left to change how it's filled."**

### 3.5 Region E — output

Keep the dark pane and the byte-identical promise. Add:
- **Meta line**: `{FORMAT} · {contentType} · {n} bytes`. `contentType` already comes back on `MappingOverridePreview`. Byte length must come from the server (`byteLength`) once encoding is selectable; until then compute `new TextEncoder().encode(content).length` and prefix `≈`.
- **`Copy` / `Download`** — the preview is the artefact a coordinator emails to their supplier contact to confirm. Not having these is the most-requested-thing-nobody-asked-for.
- **`⏎ Show line endings`** — renders `␍␊` / `␊` as dim glyphs at 0.55 opacity. Without this, CRLF is unverifiable in-product and the whole dialect feature is unfalsifiable.
- Line numbers in a gutter for >20 lines.

### 3.6 Regions F/G — problems and commitment

§8 and §4 respectively.

---

## 4. The reuse moment (design #1)

### 4.1 The footer

Today: `Cancel | Save structure`. "Save structure" is the bug — a coordinator reasonably reads it as "saved for Contoso", and it isn't.

```
Saving for every order also applies to this one.
                          [Cancel]  [Just this order]  [ Use for every Contoso order ]
```

The **default is reuse**, because the JTBD is literally *"set that up once and never think about it again."* A default that contradicts the stated intent is a design error, not a safe choice.

- `Use for every Contoso order` — primary, `#297F34` fill, white text (5.02:1). Calls `upsertMappingOverride` **then** `promoteMapping(orderId)` — one user action, two calls, one outcome. If the promote leg fails, the order-level save has already succeeded and the message says so exactly (§5).
- `Just this order` — secondary, neutral. `upsertMappingOverride` only.
- On the **supplier mount** there is one button: `Save layout`. No fork — the scope is unambiguous from the route.

### 4.2 The success state

Do not close the dialog. The user's next instinct is to check that the preview still looks right, and slamming the sheet shut denies them that. The footer's two buttons are **replaced in place** for 8s by:

> ✓ **Saved.** Every Contoso order will now be built this way — including this one. `Undo`

then it settles, and the scope chip in the header flips to `● Every Contoso order`. The chip flipping *is* the confirmation; the message is the receipt.

`Undo` re-promotes the previous config. If WP-12 does not ship an undo path, drop `Undo` and say `View Contoso's layout` instead — do not ship a button that cannot keep its promise.

Render `PromoteMappingResult.Message` verbatim beneath when present (WP-13's requirement) so the user learns exactly what was saved.

### 4.3 Seeing what a layout governs — three surfaces

**(a) The scope chip** (header, always). Four states, §5.

**(b) The supplier Output tab.** `/library/suppliers/[id]?tab=output`, sitting between *PO Mapping* and *Delivery* — because that is the actual pipeline order: how we read their file, how we write their file, how we send it.

```
Output layout
The file this supplier receives.

┌ (bridge-gradient left edge) ────────────────────────────────────┐
│ CSV · 11 columns · Semicolon · CRLF · UTF-8 · Column names      │
│ Saved 14 Jun 2026 by Maria Koch                                 │
│ ─────────────────────────────────────────────────────────────── │
│ order_no;item;qty;price                                         │
│ PO-88421;CTS-8891;10;12.50                                      │
│ ─────────────────────────────────────────────────────────────── │
│ Used by 47 orders                [Edit layout] [Stop using it]  │
└─────────────────────────────────────────────────────────────────┘
```

**(c) The order header badge.** On `/inbox/[orderId]`, next to the output format: `Layout · Contoso CSV` (or `Layout · this order`). One glance answers "why does this output look like this".

### 4.4 Divergence — the state nobody designs and everybody hits

Once a supplier layout exists and someone edits it inside one order, that order permanently differs and nothing says so. This produces the classic "why did *this* one come out different" support ticket.

Scope chip becomes `◐ Edited for this order` (amber), and the header gains `Revert to Contoso's layout`. In the Problems strip, as a **warning, not an error**:

> This order uses a changed layout. Contoso's other orders still use the saved one. `See the difference` `Revert`

`See the difference` opens the preview pane in a two-up diff (saved | current), text-diff only, no fancy AST diff. If that is too much for WP-15, ship the warning and the revert without the diff — the warning alone closes 80% of the gap.

---

## 5. State matrix

Every state below has exact copy. `{S}` = supplier name, `{F}` = format.

### 5.1 Scope chip

| State | Chip | Dot | Menu |
|---|---|---|---|
| Never saved | `Not saved yet` | grey `#7D8797` | — |
| Order only | `This order only` | blue `#1E66C9` | `Use for every {S} order` |
| Supplier-wide | `Every {S} order` | green `#2E8E3A` | `Edit for this order only` · `View on {S}` |
| Diverged | `Edited for this order` | amber `#B36D14` | `Revert to {S}'s layout` · `Save these changes for every order` |

### 5.2 The six required states

**LOADING.** Skeleton, never a spinner over content. Layout column: 5 shimmer rows at the real row height (38px) so nothing reflows. Output pane: `Building the preview…`. The format fork and footer are disabled but present. Never show the default tree while the saved one is in flight — `OutputMappingEditor.tsx:561-579` documents the exact data-loss bug that causes (an empty editor latched over a saved override, then saved).

**EMPTY.** §12.

**ERROR — could not load.**
> We couldn't load {S}'s output layout. Nothing has been changed. `Try again`

Saving is **disabled** while this is showing, with the tooltip *"Saving now could overwrite the layout we couldn't read."* Mirrors the honest guard already at `OutputMappingEditor.tsx:825-829`.

**ERROR — preview failed.** Two distinct kinds, and conflating them is the current bug (§8).

**READ-ONLY.** Three causes, three messages, all rendered as a full-width bar under Region A. Every input becomes text; the drag handles disappear; `Copy`/`Download` stay live.
- Pilot ended (locked copy, CLAUDE.md §11.5): *"Your Pilot has ended. You can still view previous orders, but new processing is paused. Upgrade to Growth to continue."* `Upgrade to Growth`
- Order already sent: *"This order has already been sent, so its layout is locked. You can still change what {S} receives from now on."* `Edit {S}'s layout`
- Published version: *"This layout comes from a published version of {S}'s setup and can't be edited here."* `View versions`

**PLAN-GATED.** Pending the founder decision in the open questions. Specced split: design + preview on every plan; `Use for every {S} order` from Operations; `Copy from another supplier` from Integration. Gate presentation: the button **renders enabled-looking and is not disabled** — clicking it opens an inline panel, because a disabled primary teaches nothing.
> **Saving a layout for every order is part of Operations.**
> You can design and preview any layout on your current plan — saving it as {S}'s default is on Operations and above. `Compare plans` `Just this order`

**SUCCESS.** §4.2.

---

## 6. Reordering (design #2)

### 6.1 Model

Add to `outputNamespaceModel.ts` (pure, unit-testable, same file as `updateAt`/`removeAt` so the namespace-preservation tests cover it):

```ts
/** Move the node at `path` by `delta` among its SIBLINGS. Clamped: a no-op at the
 *  boundary returns the SAME object reference so callers can skip a re-render.
 *  Preserves every field on every node (namespaces, includeWhen, rule) — the
 *  reason this lives here and not in the component. */
export function moveAt(root: OutputNode, path: number[], delta: -1 | 1): OutputNode
```

Sibling-bounded only. No cross-parent moves in v1 — see §14.

### 6.2 Pointer (desktop)

Pointer-events, not HTML5 drag-and-drop (HTML5 DnD has no usable touch story and no styleable drop indicator).

- Grab anywhere on `⠿`. `cursor: grab` → `grabbing`.
- The dragged row lifts: `--shadow-md`, `opacity: .96`, follows the pointer on the Y axis only.
- **Drop indicator**: a 2px `#1E66C9` line spanning the sibling group's width, with a 6px dot at its left end — the same construction as the port markers on the edge rails. Never a "ghost gap".
- Only valid drop positions are indicated. Hovering outside the sibling group shows nothing and releasing there is a no-op (not a revert animation — a no-op).
- Auto-scroll the layout column when within 40px of its edge.
- Release → `moveAt`. Announce (§6.4).

### 6.3 Keyboard

Two paths, both required:

1. **From the handle** (`role="button"`, `aria-label="Move {name}"`, `aria-describedby` → *"Use the up and down arrow keys to move this."*): `↑`/`↓` move by one and keep focus on the handle. `Home`/`End` move to first/last. `Escape` blurs.
2. **From anywhere in the row**: `Alt+↑` / `Alt+↓`. Not bare arrows — those must keep moving focus between rows.

### 6.4 Announcement

A single `aria-live="polite"` region owned by the layout column:

> `quantity moved to position 2 of 5.`

At the boundary: `quantity is already first.` — never silence.

### 6.5 CSV: making the reorder mean something

In CSV the reorder *is* the column order, so make the causality visible: on move, the corresponding column in the preview pane gets a 1.2s `#1E66C9` 2px underline. Under `prefers-reduced-motion: reduce`, the columns reorder with **no** highlight and no transition. The `Show line endings` and column-index affordances carry the meaning instead.

### 6.6 Touch (390px)

Press-and-hold drag inside a scrolling column is unreliable and I will not pretend otherwise. At <860px the handle is a **button that opens a sheet**:

> **Move quantity**
> `Move up` · `Move down` · `Move to first` · `Move to last`

Four 48px rows. Drag remains pointer-only, and that is stated in the spec rather than hidden.

---

## 7. Value: binding, combining, transforming, typing (designs #6 and #7)

The Value section of the inspector, top to bottom. This is where the biggest capability gap closes.

### 7.1 Comes from

The existing `OutputSourcePicker`, non-compact. Footer gains a third action, so the picker's footer becomes:

`Combine fields…` | `= Fixed value…` | `Write an expression…`

Also fix `CANONICAL_HEADER_FIELDS` / `CANONICAL_LINE_FIELDS` (5 + 8) to expose everything the row bag actually carries (10 + 11 — `SubTotal`, `TaxTotal`, `GrandTotal`, `PaymentTerms`, `RequestedDeliveryDate`, `LineAmount`, `TaxRate`, `DeliveryDate`) and group them: `Order` · `Totals` · `Dates` · `This line`. That is WP-14's frontend half and the picker is unusable without it — you cannot put a delivery date on a purchase order today.

### 7.2 Combine fields — the answer to "join these two fields with a dash"

Built on `OutputFieldRule.Expression`, which already exists. **Zero backend work.**

```
Combine
  [ Currency            ▾ ]
  joined by  [ -  (dash)  ▾ ]
  [ Supplier item code  ▾ ]
  [+ Add another field]

Result for this order:  EUR-CTS-8891
```

Writes `Expression = "{{ order.Currency }}-{{ line.SupplierItemCode }}"`. Separator options: `- (dash)` · `_ (underscore)` · `/ (slash)` · `. (dot)` · `space` · `nothing` · `Custom…`.

Round-trips: an `Expression` matching `^(\{\{ *[\w.]+ *\}\}[^{}]*)+$` parses back into the builder; anything else opens the raw editor with the expression shown and the note *"This is a custom expression, so it's shown as written."* **Never silently rewrite an expression the builder can't model.**

Binding pill reads `Currency-SupplierItemCode` with the resolved value after the arrow.

**Blocking prerequisite:** add `expression?: string | null` to the frontend `OutputFieldRule`, and make `setBinding` / `setFormatPreset` **spread the existing rule** instead of rebuilding it from five keys. Today they destroy `Expression` on any edit. That is a data-loss bug independent of this feature and should land first.

### 7.3 Change the value — the transform stack

Empty state: *"The value is sent exactly as it arrives."* `+ Add a step`

Populated, showing the value flowing through — a chain is incomprehensible without a value trace:

```
Change the value

  Starts as    "  acm-bolt-001 "
  ┌──────────────────────────────────────────────┐
  │ ⠿ 1  Remove extra spaces              [✕]    │
  │      → "acm-bolt-001"                        │
  ├──────────────────────────────────────────────┤
  │ ⠿ 2  Find and replace                 [✕]    │
  │      Find [ -    ]  Replace with [ _    ]    │
  │      → "acm_bolt_001"                        │
  └──────────────────────────────────────────────┘
  Sent as      "acm_bolt_001"

  [+ Add a step]
```

Steps are reorderable with the same `⠿` handle and the same keyboard contract. The per-step preview needs a server round trip; debounce 350ms like the main preview, show `…` per step while in flight, and never block typing on it.

All eight manipulators, renamed to coordinator words with the raw type as a mono subtitle for the power user:

| `MANIPULATOR_TYPES.type` | Label | Params, labelled |
|---|---|---|
| `Trim` | Remove extra spaces | — |
| `Replace` | Find and replace | `Find` · `Replace with` |
| `DateFormat` | Change date format | (the 3 date presets from `FORMAT_PRESETS`, then `Custom…` → `From` · `To`) |
| `Concat` | Add text after | `Text to add` |
| `Fallback` | Use a default if empty | `Use this instead` |
| `Split` | Take part of the value | `Split on` · `Take part number` |
| `Multiply` | Multiply by | `Multiply by` |
| `Divide` | Divide by | `Divide by` |

The 8 existing `FORMAT_PRESETS` stay as the fast path — a preset selection just writes the corresponding `DateFormat`/`NumberFormat` step into the stack, and the pill on the row keeps reading `Date · EU`. Preserve `currentPreset()`'s matching so an existing preset still displays as a preset. **Any non-format step already on a node now renders** — closing fact #6.

`Split`'s "part number" is 1-based in the label and passed through as the engine expects; verify against `ManipulatorRegistry` before shipping and adjust the label, not the value.

### 7.4 Send as — typed JSON values (design #7)

JSON only. Hidden entirely for CSV and XML, where everything is text on the wire — a type control there is pure noise and I am deliberately not shipping one.

**Do not put a type dropdown on every row.** Default `Text` for every leaf, which preserves today's bytes exactly. The product *proposes* when the resolved value warrants it, in the same propose-then-confirm posture as the deterministic inference:

```
Send as
  This looks like a number.
  Send  10  instead of  "10" ?          [Send as number]  [Keep as text]
```

Once set, a `#` pill appears on the row. The control becomes a 3-way segmented `Text` / `Number` / `True or false`, plus:

```
If the value is empty, send
  (•) null      ( ) nothing (leave the field out)      ( ) 0
```

Default `null`. `nothing` changes the document's shape per order, so it is offered but not defaulted.

**Backend requirement:** `OutputNode.ValueType` (`string` default | `number` | `boolean`) and `EmptyValue` (`null` default | `omit` | `zero`), honoured in `WriteJsonValue`'s default case (`OutputTemplateEmitter.cs:130-133`) and in the Object case for `omit`. Absent → `WriteStringValue`, byte-identical. A non-numeric value under `number` is a **Tier-1 design-time problem** (§8), not a runtime coercion.

### 7.5 Advanced → Write an expression

Collapsed. Mono input, `--border-control` boundary.

> Runs before the steps above. Use `{{ order.PoNumber }}` or `{{ line.Quantity * line.UnitPrice }}`.
> `Check against this order`

`Check against this order` reuses `buildExpressionTestDraft` from `OutputMappingEditor.tsx:113-123` verbatim — it already nulls `outputTree` so the tree cannot hijack the render, and it already surfaces the backend's error string rather than swallowing it. Extract it to a shared module rather than copying it.

Result states, all three already handled correctly by `ExpressionTester` — reuse its exact behaviour, including the honest empty:
- `→ EUR-CTS-8891`
- *"Rendered **empty** for this order — an unknown field name renders blank rather than failing. Names are case-sensitive."*
- the backend error verbatim.

---

## 8. Conditionals (design #3)

### 8.1 The builder

```
Include when
  ( ) Always include
  (•) Only include when

    [ Quantity          ▾ ]  [ is more than  ▾ ]  [ 0        ]

    Rule:  line.Quantity > 0                    Write it as an expression
    [ Check against this order ]
```

Operator → predicate. The generated predicate is shown in mono, muted, always — the builder teaches the raw form, which is how a power user graduates without documentation.

| Operator | Predicate | Offered for |
|---|---|---|
| `is` | `{scope}.{Field} == "{v}"` | text |
| `is not` | `{scope}.{Field} != "{v}"` | text |
| `is empty` | `{scope}.{Field} == ""` | all |
| `is not empty` | `{scope}.{Field} != ""` | all |
| `is more than` | `{scope}.{Field} > {v}` | **numeric only** |
| `is less than` | `{scope}.{Field} < {v}` | **numeric only** |

Numeric fields — `Quantity`, `UnitPrice`, `LineTotal`, `LineAmount`, `LineNumber`, `TaxRate`, `SubTotal`, `TaxTotal`, `GrandTotal` — are real numbers in the row bag (`OutputNode.cs:77-78`), so their values emit **unquoted**. Everything else quotes. Restricting `>`/`<` to numeric fields is what stops a coordinator writing the silent `"10" > "9"` string-comparison bug — and they would never find it, because it produces a plausible file.

Scope is stated, not implied: the field dropdown is headed `Fields from this line` inside a repeating list, `Fields from the order` outside it. `{scope}` is `line` or `order` accordingly, matching `ScribanFieldEvaluator`'s two scopes.

### 8.2 Round-trip

Parse an existing `includeWhen` with a strict anchored regex per operator. Parses → structured. Doesn't parse → open raw mode with the expression intact and the note *"This condition was written as an expression, so it's shown as written."* Never rewrite.

### 8.3 Test

`Check against this order` → the same expression-test path, wrapping the predicate as the emitter does (`"{{ (" + pred + ") }}"`, `OutputTemplateEmitter.cs:348`) so what is tested is what runs.

Results:
- header scope: `Included.` / `Left out.`
- line scope: `Included for 4 of 5 lines.` — run per line; this is the answer a coordinator actually wants.
- unevaluable: `This condition couldn't be worked out, so the value would be included anyway.` — amber. This is the **fail-open behaviour at `OutputTemplateEmitter.cs:353-363` finally becoming visible**; today it only reaches a log.

### 8.4 CSV honesty

A CSV column cannot vary per row (`OutputTemplateEmitter.cs:306-308`). So in CSV mode the `Include when` section on a **value** node is present but disabled, with the reason inline — never just greyed:

> A CSV file has the same columns on every row, so a column can't be switched on and off. You can leave whole lines out instead. `Set that on the repeating list`

And the **repeating-list** node's condition is relabelled `Only include lines when…`, because that is exactly what it does.

---

## 9. The hard constraint: cXML / UBL / X12 (the part you asked judgement on)

### 9.1 The diagnosis

The current design commits three errors at once:

1. It **omits** cXML/UBL/X12 from the format control. Omission is read as absence: the coordinator concludes ProcuLink cannot do cXML. It can — `CxmlTransformService` is 26KB of working code.
2. It **silently rewrites** a cXML tree to XML on open, and saves the rewrite (`:44-51` + `:146-148` + `:239-249`). Data loss with no consent.
3. It leaves a **crash** reachable: a persisted `cXml` tree makes `OutputTemplateEmitter.Emit` throw at delivery (`:69-74`).

And the framing is wrong. "Pick an output format" invites a choice from one list, so anything not in the list reads as unsupported. But the real world has **two different kinds of answer** to "what does this supplier need?", and the boundary between them is not arbitrary — it is the difference between *a file we draw* and *a document with an envelope the receiver validates before it looks at your data*.

### 9.2 The move: turn the omission into a fork

Reframe the control from a format list into a question with two answer classes.

```
What does Contoso need?
[ CSV ][ XML ][ JSON ]        A standard document (cXML, UBL, EDI)…
```

Three segmented options, plus a **fourth affordance that is a link-button, never a disabled radio**. A disabled option in a picker says "no". A link says "over here". That single styling decision carries most of the information design.

Sub-label under the fork, per selection:
- CSV — *"A spreadsheet-style file. One row per order line."*
- XML — *"A tagged file you build to their spec."*
- JSON — *"A structured file, usually for an API."*

### 9.3 The standard-documents panel

Clicking the fourth affordance **replaces the Layout column** (`← Back to the layout`). Not a modal — a modal on top of a sheet is where users get lost, and this is a place they arrive uncertain.

> ### Standard documents are already built for you
>
> cXML, UBL and EDI aren't layouts you draw. Each one carries a fixed envelope — sender and receiver identifiers, a version, a document type — that the receiving system checks **before** it looks at your order. A file that got the envelope wrong would look perfect here and still be rejected on arrival.
>
> So ProcuLink builds these formats itself, and asks you only for the identifiers your supplier gave you.

Then exactly three rows — the three that have an order transform:

| | |
|---|---|
| **cXML 1.2** — Ariba, Coupa and most procurement networks. | `Set up cXML for Contoso` |
| **UBL 2.1** — the OASIS order standard, common across the Nordics and Benelux. | `Set up UBL for Contoso` |
| **EDI X12 850** — the North American purchase order. | `Set up EDI for Contoso` |

Each deep-links to `/library/suppliers/{id}?tab=delivery` (verified: that tab renders `DeliveryGuidedSetup` + `DeliveryConfigEditor`, which is where the output format lives). **Do not list Peppol BIS or EDIFACT** — there is no order transform for either. Listing them would be the same overclaim this panel exists to prevent.

And then the escape hatch, which is the half most people miss — many "we need cXML" requests are actually bespoke XML:

> **Not quite a standard?** If Contoso just wants their own XML file that happens to look similar, build it as XML here — you get their exact tag names and nesting. `Build it as XML`

### 9.4 The capability map

A `Which file types can ProcuLink send? ▸` disclosure, present in the empty state (§12) and in this panel. Six rows, honest about how each is set up:

| Format | How | Where |
|---|---|---|
| CSV | You design the columns | Here |
| XML | You design the tags | Here |
| JSON | You design the structure | Here |
| cXML 1.2 | We build it; you give us the identifiers | {S} → Delivery |
| UBL 2.1 | We build it; you give us the identifiers | {S} → Delivery |
| EDI X12 850 | We build it; you give us the identifiers | {S} → Delivery |

This turns a wall into a map. It is the cheapest thing on this spec and the highest-leverage: it lets a coordinator self-answer "can you send to my supplier?" without a support ticket, and it makes the boundary feel like architecture rather than a limitation.

### 9.5 Ending the silent rewrite

Never coerce on open. When a loaded tree's `format` is `cXml`/`ubl`/`x12`, keep it and render a bar above the layout, blocking save (Tier 1):

> **This layout is saved as cXML, which can't be built from a layout.**
> Contoso's cXML is produced by ProcuLink's own cXML builder — this layout isn't being used. Choose what should happen:
> `Set up cXML properly` · `Turn this into a plain XML layout` · `Remove this layout`

`Turn this into a plain XML layout` is the *consented* version of today's `designerFormat()` behaviour, and it warns first:

> This will keep your tags and nesting but produce plain XML, without the cXML envelope. Contoso may reject it. Continue?

Keep `designerFormat()` for the **display** of an unknown/miscased value (it is a good defensive function) but strip it from the seed and save paths. `xml` must stay `xml`; `cXml` must stay `cXml` until a human decides.

---

## 10. XML namespaces (design #4)

### 10.1 The explanation

One sentence, no jargon beyond "tag":

> **A namespace is a label — usually a web address — that tells your supplier's software which standard each tag comes from, so their `ID` and yours are never confused.**

Then one helper line:

> Your supplier's spec lists the ones they expect. If it doesn't mention namespaces, leave this off.

### 10.2 The presets

Document-level control in Region B (`XML namespaces: None ▾`), opening a panel:

- **`None`** (default) — *"Plain tags with no namespace. Right unless your supplier's spec says otherwise."*
- **`UBL 2.1 (cbc:, cac:)`** — *"Adds the two standard UBL labels: `cbc:` for plain values, `cac:` for groups."* Writes the root map `cbc` → `urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2`, `cac` → `…CommonAggregateComponents-2`.
- **`Custom…`** — today's prefix/URI rows, kept.

**I am deliberately not shipping the "cXML 1.2" and "Peppol BIS 3" presets the brief asks for, and this is the one place I am pushing back:**
- **cXML has no namespaces.** It is DTD-based. A "cXML namespace preset" would teach a coordinator something false about the format they are trying to satisfy. cXML belongs in the §9.3 fork.
- **Peppol BIS 3.0** uses the UBL namespaces plus three *mandatory element values* (`UBLVersionID`, `CustomizationID`, `ProfileID`). Seeding those as fixed-value nodes would produce a document that looks like Peppol and is rejected by the network — the exact failure mode `OutputTemplateEmitter.cs:64-74` exists to prevent. Peppol belongs in the fork too, and there is no Peppol *order* transform yet, so today it belongs on a roadmap and nowhere in this UI.

Add instead, under the preset list:

> Looking for cXML, UBL or Peppol as a whole standard document? Those are set up on Contoso's Delivery tab. `Show me`

### 10.3 The mode collision, made actionable

Today: an amber warning at **4.39:1 (fails AA)** telling the user to "clear the per-element namespaces", with no control to do it (`:376-379`).

Replace with `--amber-text` `#8A5310` on `--amber-soft` `#FAF1DD` (**5.62:1**) and two buttons:

> This layout puts namespaces on individual elements, so the document-level list isn't used. Pick one place for them.
> `Move them to the top` · `Keep them on each element`

`Move them to the top` walks the tree, collects the distinct prefix→URI pairs (the logic already exists server-side as `CollectPrefixedNamespaces`, `OutputTemplateEmitter.cs:200-211` — mirror it client-side), writes them to `template.namespaces`, and clears every per-node pair. `Keep them on each element` clears `template.namespaces`. Either way the mutually-exclusive invariant the emitter enforces at `:151-153` is satisfied, and the user was never asked to understand why.

### 10.4 Per-node

Stays in the inspector's `XML` section: `Prefix` (a dropdown of prefixes already declared on the document, plus `Custom…`) and `Namespace`. The dropdown is the fix — today both are free text and a typo produces a `Prefix`-without-`Namespace` half-state that throws at `:154-157`. Keep `setNodeNamespace`'s guard (`outputNamespaceModel.ts:44-52`) and keep `NamespaceEditorRow`'s local draft so a half-typed prefix survives.

Hidden entirely for JSON and CSV, and for `array` nodes (as today, `:515`).

---

## 11. CSV dialect (design #5)

CSV only. Opens from Region B's `[Change]`. Five controls — **not** behind an Advanced disclosure, because a wrong line ending silently breaks a supplier and these are facts a coordinator often has in hand.

```
File format

Column separator   [ Semicolon  ;  ▾ ]   Comma , · Semicolon ; · Tab · Pipe | · Custom…
Quotes             ( ) Only when needed   ( ) Always   ( ) Never
Line endings       (•) Windows (CRLF)     ( ) Unix (LF)
                   Most supplier systems expect Windows endings. Pick Unix only if
                   their spec says so.
Encoding           [ UTF-8  ▾ ]  UTF-8 · UTF-8 with BOM (opens cleanly in Excel) ·
                                 Windows-1252
First row          (•) Column names       ( ) Data only
```

Inline warnings, live against this order's real values:

- Quotes = `Never`: **"A value containing a `;` will break the file. 2 values in this order contain one."** (amber, with `Show me` jumping to the offending column)
- Encoding = `Windows-1252`: **"3 characters in this order can't be written in Windows-1252 and will be replaced with `?`: ö, ä, €."** (amber)
- Separator = `Custom…`: reject multi-character and reject `"`, `\r`, `\n` with **"A separator must be a single character, and can't be a quote or a line break."**

**The line-ending migration.** Today it is `Environment.NewLine` → LF on the container. Changing the default changes the bytes every existing CSV supplier receives, so:
- **New** layouts default to `CRLF` (RFC 4180, and what supplier ERPs expect).
- **Existing** layouts with no recorded dialect keep LF and show a one-time nudge in Region B:
  > This layout was saved before line endings could be set, so it currently sends Unix (LF). `Set them to match Contoso's spec`

That preserves byte-parity — which WP-12's whole AC depends on — while fixing the default going forward.

**Backend requirement:** `OutputNodeTemplate.CsvDialect { Delimiter, QuotePolicy, LineEnding, Encoding, WriteHeaderRow }`, all nullable, threaded into `EmitCsv` (`OutputTemplateEmitter.cs:281-315`) and a parameterised `Escape`. `null` on every field must reproduce today's bytes exactly. `sb.AppendLine` must become an explicit `sb.Append(value).Append(lineEnding)` — `AppendLine` cannot be made deterministic.

---

## 12. The empty state (design #8)

A coordinator opening this with no supplier sample. Today: a textarea, `Build from a sample`, `Start blank`. That asks someone who has never seen a node tree to build one from nothing.

The first-run screen takes the **whole body** — both columns collapse. A dark, empty preview rectangle beside a paste box is dead weight and reads as broken.

```
                        Build the file Contoso needs

     Every supplier wants their order file a little differently. Set it up once
     here and every future order comes out the same way.

  ┌── 1 · Start from their file ────────────────────── recommended ──────────┐
  │  Paste the example file Contoso sent, or drop it here. We'll match its  │
  │  structure — column names, order and nesting.                            │
  │  ┌───────────────────────────────────────────────────────────────────┐   │
  │  │  Paste a CSV, XML or JSON file…                                   │   │
  │  │                                                          (drop)   │   │
  │  └───────────────────────────────────────────────────────────────────┘   │
  │  CSV, XML, JSON or plain text. Read on your device — never uploaded.     │
  │                          [Choose a file…]    [ Match this structure ]    │
  └─────────────────────────────────────────────────────────────────────────┘

  ┌── 2 · Start from a common shape ────────────────────────────────────────┐
  │  Spreadsheet (CSV)          One row per order line                   ›  │
  │  Order and lines (JSON)     A header block and a list of lines       ›  │
  │  Tagged file (XML)          An order element with a lines block      ›  │
  └─────────────────────────────────────────────────────────────────────────┘

  ┌── 3 · Copy from another supplier ───────────────────────────────────────┐
  │  Start from a layout you already set up.        [ Choose a supplier… ]  │
  └─────────────────────────────────────────────────────────────────────────┘

  No example file? Start from a common shape and rename the columns to match
  their spec — the preview shows exactly what they'll receive.

  Which file types can ProcuLink send? ▸
```

Four changes from today that matter:

1. **`Choose a file…`** — a real `<input type="file" accept=".csv,.json,.xml,.txt">`, read with `FileReader` and fed to the same `inferOutputStructure` call. Coordinators have a file, not a clipboard. The no-egress property is preserved and stated: *"Read on your device — never uploaded."*
2. **Three named starting shapes** replace `Start blank`. Each is a `defaultTree` variant with the right format preselected. `Start blank` survives only as a small text link at the bottom of route 2.
3. **`Copy from another supplier`** — the single highest-value route once an org has five suppliers, because suppliers cluster hard. Requires WP-12. **Hidden, not disabled**, when there is nothing to copy — a disabled route in an empty state teaches only that the product is incomplete.
4. **The capability disclosure** lives here, because first-run is precisely when someone asks "can it do cXML?"

**Post-inference confirmation.** After a successful match, do not just swap the tree in silently (today: `setTree` + `setShowInfer(false)`, `:193-197`). Show, above the tree, for one interaction:

> ✓ **Matched their file.** 11 columns, repeating once per order line.
> 6 columns were matched to your order's fields. **5 still need a field** — they're marked below. `Show me the first one`

That last sentence is the honest consequence of the inferrer's deliberate `FixedValue = null` for unmapped columns (`OutputNodeTemplateInferrer.cs:209-215`). Without it, a user saves an inferred layout believing it is finished and ships five permanently-empty columns.

**Inference failure** keeps the sample in the box and says what to do:

> We couldn't read that as CSV, XML or JSON. Check that you pasted the whole file — or start from a common shape below and rename the columns to match.

---

## 13. The error state (design #9)

> *"A design that would produce an invalid document must fail HERE, at design time, not silently at delivery."*

### 13.1 Three tiers, because they are three different problems

The current design shows every failure as red text in the preview pane (`:394`). That means an order with unresolved lines — which is not a layout problem at all — reads as "your layout is broken". Separate them:

| Tier | Meaning | Blocks | Colour |
|---|---|---|---|
| **1 · Problem** | The layout itself cannot produce a valid file | **Save** and delivery | `--danger` `#B43838` |
| **2 · Not ready** | The layout is fine; this order isn't ready | Delivery only | `--amber` |
| **3 · Warning** | It will deliver, and it is probably not what you meant | Nothing | `--amber` |

### 13.2 Tier 1 — computed client-side, from the emitter's own throw sites

Each one maps to a line in `OutputTemplateEmitter.cs` that throws or silently drops:

| Check | Emitter evidence |
|---|---|
| A node has a prefix and no namespace URI | `:154-157` throws |
| Both root-map and per-node namespaces are set | `:151-153` throws |
| Format is `cXml` or `Ubl` (or `X12`) | `:69-78` throws |
| An element name is not a valid XML Name | `XmlWriter` throws |
| Two CSV columns share a name | silent data corruption |
| Zero CSV columns | emits a header-only file |
| A repeating list has no item template | `:119-127` / `:244` emit nothing — silent data loss |
| Duplicate JSON property names in one object | last-wins, silent |
| `Send as number` on a non-numeric resolved value | would throw once §7.4 lands |

### 13.3 Tier 2 — from the server

`GuardResolved` (`:320-329`) plus, once WP-16 routes the tree path through it, `OutputFieldValidator.CollectEntityProblems` — the checks the tree path currently skips while all six fixed transforms run them.

### 13.4 Tier 3 — warnings

- An unbound value node — *will always be sent empty* (the inferrer's deliberate TODO state)
- A condition that failed to evaluate — **requires the backend to return the fail-open warning** the emitter currently only logs (`:353-363`). Add it to `MappingOverridePreview.warning`; without that, this warning cannot exist.
- A value containing the chosen delimiter with quoting off
- Characters unrepresentable in the chosen encoding
- This order diverging from the supplier layout (§4.4)

### 13.5 Presentation — the Problems strip

Region F. Persistent, never a toast, never a modal. Collapsed to one line when clean:

> ✓ This layout will produce a valid file.

When not clean:

> ⚠ **2 problems · 1 warning** `Show`

Expanded, each row is a plain sentence and a jump. `Fix` scrolls the node into view, selects it, opens the right inspector section, and focuses the offending control. **That jump is what makes design-time validation usable in a 40-node tree** — a list of complaints you have to hunt for is worse than no list.

Exact copy, all Tier 1:

- `"cbc" has a prefix but no web address. A prefix needs an address to point at.`
- `Namespaces are set both at the top and on individual elements. Pick one place.`
- `Two columns are both called "qty". Column names must be different.`
- `"2nd item" isn't a valid tag name. Start it with a letter and use no spaces.`
- `The repeating list "lines" is empty, so no order lines would be sent.`
- `"quantity" is set to send a number, but this order's value is "10 pcs".`
- `This layout has no columns yet.`
- `cXML can't be built from a layout. Choose CSV, XML or JSON, or set up cXML on Contoso's Delivery tab.`

Tier 2:

- `3 order lines don't have a supplier item code yet, so this order can't be built. The layout itself is fine.` → `Go to the lines`
- `Line 4's quantity is 0. Orders with a zero quantity are held for review.` → `Go to line 4`

Tier 3:

- `"vat_code" has no field, so it will always be sent empty.` → `Pick a field`
- `The condition on "discount" couldn't be worked out, so the value was included anyway.` → `Fix the condition`
- `"description" contains a ";" and quotes are turned off, which will break the file.` → `Turn quotes on`

### 13.6 In the tree and in the preview

The row's status bar turns red (Tier 1) or amber (Tier 3), so the tree shows *where* while the strip says *what*. The preview pane header, when Tier 1 exists:

> **This layout can't produce a file yet.** `Fix 2 problems`

and when only Tier 2:

> **The layout is fine — this order isn't ready to build yet.** `Go to the lines`

Amber, not red. That distinction is the whole point of the tier model.

### 13.7 The save button

Disabled **with a reason on the label**, never a bare grey button: `Fix 2 problems to save`, `aria-describedby` the problems strip. Tier 2 and Tier 3 never block saving — a layout can be correct for an order that is not ready.

---

## 14. What stays visual, what becomes a property editor, where raw belongs

**Visual — the tree, and only shape.** Nesting, repetition, order, presence, and one glanceable answer to "is this filled and from where". Anything that is a *value* of a property belongs in the inspector. The failure mode to avoid is the tree becoming a property grid with indentation, which is what happens when each new capability adds another inline control to the row. That is exactly the trajectory the current file is on — four inline editors already fight over one `editing` slot at `:459`, and `+ format`, `+ condition`, `+ namespace`, `Advanced ▸` and `✕` all compete for the row's right edge.

**Structured property editor — the inspector.** One node, all its properties, one at a time, stable position, scrollable, no popover collisions. Everything in §7 and §8 lives here. This is also what makes 1024 and 390 tractable: the same sections become an overlay and then a sheet with no redesign.

**Raw / advanced — exactly three places, each labelled, each with a tester:**

1. **Per-node expression** (`OutputFieldRule.Expression`) — inspector → Advanced. Tester: `Check against this order`.
2. **Per-node raw condition** (`IncludeWhen`) — inside the condition builder, as `Write it as an expression`. Tester: the same.
3. **Whole-file template** (Scriban) — stays in `OutputMappingEditor`'s template mode, reached from the designer's overflow menu as `Write the whole file as a template…`. Keep its existing honest precedence banner (`OutputMappingEditor.tsx:788-819`), which already tells the user a designed layout wins.

Plus one **read-only** raw view: `View this layout as JSON` in the overflow, copyable, for support and for bug reports. **Not editable.** An editable raw AST is a corruption vector with no validation story and it would let a user author the exact states §13.2 exists to prevent.

---

## 15. How AI assists without taking control

The inference is already deterministic, propose-then-confirm, and no-egress. Extend that posture; do not dilute it.

**Three permitted jobs:**

1. **Raise recall on unbound leaves.** After inference, `GuessCanonical` is pure name matching, so `art_nr`, `Bestellnr`, `Menge`, `EAN` come back unbound. AI proposes a field for **unbound leaves only**, rendered as a violet dashed pill on the row: `AI · Quantity?` with `✓` and `✕`. Plus one explicit `Accept all 6`. Never applied on save. Accepting turns the row green and the violet disappears — provenance is a moment, not decoration.
2. **Explain an error in plain language.** Take the emitter's or Scriban's error string and restate it under the verbatim message, tagged `AI`. Read-only, mutates nothing. This is where AI earns the most trust for the least risk.
3. **Propose a starting tree from pasted spec *prose*** (a coordinator often has a PDF spec, not a sample). Lands as an unsaved draft with **every leaf unbound**, headed `Proposed from your notes — check every field before saving.` This is the only feature here that sends supplier text to a model, so it must be opt-in per workspace and must say so.

**Forbidden, with reasons:**

| AI must not | Why |
|---|---|
| Author a conditional | A wrong predicate silently drops the supplier's data, and the emitter fails open, so it looks fine in the preview |
| Choose a namespace or URI | A plausible-looking URI is indistinguishable from a correct one in the preview and fails only at the receiver |
| Choose a CSV dialect | These are contract facts from the supplier's spec, unverifiable from the order |
| Set a value type | A guessed `number` on a text field is a delivery-time failure, and the sample that justified it may be unrepresentative |
| Decide reuse scope | Committing a layout to every future order is a human decision |
| Apply anything on save | The one rule that keeps all of the above true |

Violet `--ai` `#6F4FCE` / `--ai-soft` `#F0EAFB` on AI-origin chrome only (6.45:1 with `#5E3DB0` text — passes AA).

---

## 16. Token map

Every colour here is an existing token in `src/app/globals.css` unless marked **NEW**.

| Use | Token | Hex |
|---|---|---|
| Header / page chrome | `--navy` | `#0B1A2F` |
| Work area | `--bg` | `#F6F7FA` |
| Panels, rows | `--surface` | `#FFFFFF` |
| Container rows, sub-panels | *(existing literal)* | `#FBFCFE` / `#F7F9FC` |
| Output pane | *(existing literal)* | `#0B1626` |
| Body text | `--ink` | `#0B1A2F` |
| Labels, helper | `--ink-muted` | `#5E6779` |
| Faint / samples | `--ink-faint` | `#667085` |
| Buyer / incoming / focus | `--brand-blue` | `#1E66C9` |
| Set-condition pill text | `--brand-blue-deep` | `#0F4FA8` |
| Supplier / bound / done | `--brand-green` | `#2E8E3A` |
| Green text and borders | `--brand-green-deep` | `#1E6D29` |
| Primary button fill | *(existing `GREEN_BTN`)* | `#297F34` |
| Warning text | `--amber-text` | `#8A5310` |
| Warning fill | `--amber-soft` | `#FAF1DD` |
| Warning stroke, dots | `--amber` | `#B36D14` |
| Problem text / stroke | `--danger` | `#B43838` |
| Problem fill | `--danger-soft` | `#FAE6E6` |
| AI chrome | `--ai` / `--ai-soft` | `#6F4FCE` / `#F0EAFB` |
| Fixed-value status bar | `--ai` | `#6F4FCE` |
| Card / panel border | `--border` | `#E5E8EE` |
| **Control boundary (input, select, segmented)** | **NEW `--border-control`** | **`#7D8797`** |
| Left edge signature | `--gradient-bridge-deck` | blue→green |
| Radii | `--radius` 6 / `--radius-md` 8 / `--radius-xl` 12 | |
| Elevation | `--shadow-card`, `--shadow-md` (drag lift), `--shadow-xl` (sheet) | |
| Tap floor | `--tap-min` | `44px` |
| Motion | `--duration-fast` 150ms, `--ease-out` | |

**Why `--border-control` is new.** WCAG 1.4.11 requires **3:1** for the boundary of a control that has no other visual affordance. Computed: `--border-strong` `#CBD0DA` = **1.55:1** on white. `#D8DEE9` (the current ghost/dashed borders) = **1.35:1**. `#ECEFF4` (row borders) = **1.15:1**. All fail. `#7D8797` = **3.63:1** on `#FFFFFF`, **3.44:1** on `#F7F9FC`, **3.54:1** on `#FBFCFE`, **3.39:1** on `#F6F7FA` — passes everywhere it is used.

Row and card borders may stay at `--border`: a card is not a control, and the row's 3px status bar carries its state at ≥3:1. Only **inputs, selects, segmented controls, dashed add-buttons and drop targets** need `--border-control`.

Fonts: Inter for all UI; **JetBrains Mono** for node names, field names, generated predicates, expressions, sample values and the preview; Bricolage Grotesque **only** for the column/problem counts (`Columns · 11`, `2 problems`) — nothing else in a working surface.

---

## 17. Accessibility — computed, not asserted

### 17.1 Text contrast (all ratios computed from the hexes above)

| Foreground | Background | Ratio | AA |
|---|---|---|---|
| `#0B1A2F` | `#FFFFFF` | **17.46** | ✓ |
| `#0B1A2F` | `#F7F9FC` | **16.55** | ✓ |
| `#5E6779` | `#FFFFFF` | **5.69** | ✓ |
| `#5E6779` | `#F7F9FC` | **5.39** | ✓ |
| `#667085` | `#FFFFFF` | **4.97** | ✓ |
| `#667085` | `#F7F9FC` | **4.72** | ✓ |
| `#1E66C9` | `#FFFFFF` | **5.53** | ✓ |
| `#0F4FA8` | `#EEF3FB` (condition pill) | **6.97** | ✓ |
| `#1E6D29` | `#F1F8F2` (bound pill) | **5.94** | ✓ |
| `#1E6D29` | `#EAF6EC` (format pill) | **5.77** | ✓ |
| `#FFFFFF` | `#297F34` (primary) | **5.02** | ✓ |
| `#FFFFFF` | `#0B1A2F` (header) | **17.46** | ✓ |
| `#8A5310` | `#FAF1DD` (warning) | **5.62** | ✓ |
| `#B43838` | `#FFFFFF` | **5.89** | ✓ |
| `#B43838` | `#FAE6E6` | **4.92** | ✓ |
| `#5E3DB0` | `#F0EAFB` (AI) | **6.45** | ✓ |
| `#D7E2F2` | `#0B1626` (preview body) | **13.87** | ✓ |
| `#8FA3BF` | `#0B1626` (preview label) | **7.05** | ✓ |
| `#FF9B9B` | `#0B1626` (preview error) | **9.00** | ✓ |
| `#3A4A60` | `#EEF1F6` (`[ ]` pill) | **7.97** | ✓ |
| `#5E6779` | `#EEF1F6` (`val` pill) | **5.02** | ✓ |

**One existing failure, must be fixed:** `#9A6B1E` on `#FFF7E8` = **4.39** ✗ (`OutputStructureDesigner.tsx:376`). Replace with `#8A5310` on `#FAF1DD` = **5.62** ✓.

**One near-miss to watch:** `#FFFFFF` on `--brand-green` `#2E8E3A` = **4.16** ✗. That is exactly why `GREEN_BTN` `#297F34` exists (`:60`). Never put white text on `#2E8E3A`. The comment at `:59-60` already says so — honour it.

### 17.2 Non-text contrast (1.4.11, 3:1)

- Control boundaries → `--border-control` `#7D8797` (§16).
- Row status bars: green `#2E8E3A`/white = 4.16 ✓; amber `#B36D14` = 3.65 ✓; danger `#B43838` = 5.89 ✓; violet `#6F4FCE` = 5.44 ✓; grey containers use `#7D8797` = 3.63 ✓.
- Drop indicator `#1E66C9`/white = 5.53 ✓.
- Focus ring: the global `:focus-visible` (2px `--brand-blue` + 4px halo, `globals.css:127-133`) is inherited — do not re-implement per component. On the navy header, add the `on-navy` class so the `#6BA5F0` ring applies (`:140-145`).
- State never encoded by colour alone: bound = green bar **and** a named field in the pill; unbound = amber bar **and** the text `Needs a field`; problem = red bar **and** a Problems row.

### 17.3 Targets and inputs

- Every interactive element ≥44×44 at <860px via `min-height: var(--tap-min)`, including the drag handle (24px visual, 44px hit via padding), the pill `✕` clears, and the `Fix` links.
- **Every text input ≥16px font at <860px.** Today the designer is 12px throughout (`:345, 548, 665, 736, 805, 867`) which triggers iOS zoom on focus and then leaves the viewport scrolled. Desktop may stay at 12.5–13px.
- Inputs ≥40px tall at <860px (currently 26–28px).

### 17.4 The dialog contract — currently absent, all four required

The order mount is a modal sheet. It must:
1. `role="dialog"` **`aria-modal="true"`** + `aria-labelledby` pointing at the visible title (today: `aria-label` only, `:267`).
2. **Trap focus.** Wrap in the same Radix primitive that `useConfirm` uses (`components/ui/confirm.tsx`) rather than hand-rolling, so the trap, the scroll lock and the inert background come for free.
3. **Close on Escape** — routed through the existing `requestClose` so the dirty-check confirm still fires (`:253-264`).
4. **Restore focus** to the toolbar button that opened it.

The supplier mount is a page, so none of this applies there — a further argument for §2.

### 17.5 Motion

Under `prefers-reduced-motion: reduce`: no drag lift, no reorder highlight, no preview fade, no pill transitions. The drop indicator stays (it is information, not decoration). Reorder still animates position 0ms — instant, correct, silent.

### 17.6 Screen-reader structure

- The tree is `role="tree"` / `role="treeitem"` with `aria-level`, `aria-expanded` on containers, `aria-posinset` / `aria-setsize` on every row — `aria-posinset` is what makes reorder comprehensible without sight.
- Each row's accessible name: `"{name}, {type}, from {field}, position {n} of {m}"`.
- The inspector is `aria-labelledby` the selected node's name and gets focus moved to its first control on selection **only** when selection came from the keyboard, never from a mouse click.
- One `aria-live="polite"` region for reorder announcements; one `role="status"` for preview freshness; `role="alert"` reserved for Tier 1 problems appearing.

---

## 18. 1024px

Two columns plus an overlay inspector. Total usable ≈ 980px.

- Layout **58%** / Output **42%**. Both keep their identity; neither collapses.
- **Details overlays Output** when a node is selected, with `← Back to the output` in its header. Deselecting (Escape, or clicking the layout background) returns to the preview automatically. This is the compromise that protects the byte-identical preview — the product's best feature — from being permanently displaced by an inspector.
- Region B's `File format` and `XML namespaces` open as the same overlay, so the layout column never narrows.
- Rows stop wrapping: the binding pill's resolved value truncates to 14ch, and the third state pill collapses into `+1`.
- Footer stays one row; the helper line drops (it is already conditional at `:402`).
- At **860–1024** the inspector overlay becomes full-width over both columns. The existing 860px `matchMedia` breakpoint (`:120`) stays as the single-column boundary; add 1024 and 1280 as the other two.

---

## 19. 390px — the honest reduced surface

Today: one scrolling column with the dark preview below the entire tree, so verifying your work means scrolling past everything you just built. That is not a small screen, it is a broken one.

**One view at a time, switched explicitly.**

```
┌─────────────────────────────────┐
│ ▌ Output layout            ✕    │  56px navy
│ ▌ ● Every Contoso order        │
├─────────────────────────────────┤
│ [ Layout ][ Output ][ ⚠ 2 ]     │  48px segmented, pinned
├─────────────────────────────────┤
│ CSV ▾   File format ›           │  44px
├─────────────────────────────────┤
│ ┌─────────────────────────────┐ │
│ │ ⠿  1  val   quantity     ✕  │ │  two-line card,
│ │    ← Quantity → 10          │ │  72px, 16px text
│ │    ⟦only when⟧      Edit ›  │ │
│ └─────────────────────────────┘ │
│ ┌─────────────────────────────┐ │
│ │ ⠿  2  val   price        ✕  │ │
│ │    Needs a field       Edit ›│ │  amber bar
│ └─────────────────────────────┘ │
│                                 │
│ [+ Value] [+ Group] [+ List]    │  48px each
├─────────────────────────────────┤
│ ⚠ 2 problems              Show ›│  48px
├─────────────────────────────────┤
│ [ Use for every order ]         │  48px, full width
│ [ Just this order ]             │  48px
└─────────────────────────────────┘
```

- **Segmented view switch** `Layout | Output | ⚠ n`. The problems tab only appears when there are problems, and its badge is the count.
- **Rows are two-line cards**, 72px. Line 1: handle, index, type pill, name, delete. Line 2: binding + value, state pills, `Edit ›`. `Edit ›` opens the inspector as a **full-screen sheet** with `‹ Back`.
- **Reorder is the four-option sheet** from §6.6. No touch drag.
- **`File format`, `XML namespaces`, `Problems`, `Details`** are all full-screen sheets with `‹ Back`. One navigation model, learned once.
- **Output view** gets the meta line, `Copy`, `Show line endings`, and horizontal scroll **inside its own `overflow-x: auto` container** — the page body must never scroll sideways.
- **First run** at 390: the paste box, `Choose a file…` (the native picker reaches Files/iCloud/Drive), then the three shapes as full-width 56px rows. Route 3 and the capability disclosure sit below the fold, which is correct.
- **Footer** stacks two full-width 48px buttons, primary on top. `Cancel` becomes the header `✕`.
- Full-screen, no inset, no radius (already the behaviour at `:271-272`).

What honestly does not fit at 390 and is not attempted: side-by-side layout and output, drag reordering, the transform stack's per-step value trace (it becomes a collapsed `3 steps ›` opening a sheet), and the divergence diff.

---

## 20. Backend work this spec requires

Naming it here so no implementer discovers it mid-build. All five are additive and all five must be byte-identical when absent.

1. **`OutputNodeTemplate.CsvDialect`** — `Delimiter`, `QuotePolicy`, `LineEnding`, `Encoding`, `WriteHeaderRow`, all nullable. Threaded into `EmitCsv` (`OutputTemplateEmitter.cs:281-315`) with a parameterised `Escape`. `sb.AppendLine` → explicit append. (WP-15)
2. **`OutputNode.ValueType` + `OutputNode.EmptyValue`** — honoured in `WriteJsonValue`'s default case and the Object case. Absent → `WriteStringValue`. (WP-15)
3. **Route the tree path through `OutputFieldValidator`** — `OutputTemplateEmitter.Emit` currently calls only `GuardResolved`; all six fixed transforms call `ValidateEntity`. (WP-16)
4. **Return the fail-open condition warning** — `ShouldEmit` logs and moves on (`:353-363`). Surface it on `MappingOverridePreview.warning` so Tier 3 can exist at all. Also return `byteLength`.
5. **`OutputTree` through promotion** — WP-12 as already planned. Without it, every reuse surface in §4 renders a plan-honest empty state, and `Copy from another supplier` stays hidden.

**And the one the JTBD needs that is not in WP-15's scope:** a **CSV trailer row**. `EmitCsv` writes a header row plus one row per line, full stop — there is no footer concept, and a CSV `IncludeWhen` can only drop a whole line (`:306-308`). So *"a total line only when the order is over 5000 euro"* — the coordinator's own words in this brief — is **not expressible**. Minimum honest addition: a `trailer` object node under root, emitted once after the lines, honouring `IncludeWhen` at header scope, its value nodes becoming that row's cells. In the designer it is a single affordance under the tree: `+ Total row` (CSV only), producing a `⟦only when⟧`-capable group. Until that exists, the designer must not imply it can — and this spec deliberately does not draw it.

---

## 21. What I deliberately left out, and why

| Left out | Why |
|---|---|
| **Cross-parent drag** | Moving a node between the order scope and a line scope silently invalidates its binding (`line.*` vs `order.*`) and its condition. Sibling-only reorder covers the real jobs — column order and element order — with none of that risk. Revisit only with a scope-change confirmation. |
| **Multi-select / bulk node edit** | Trees this size (11–40 nodes) do not need it, and it doubles the state machine. |
| **A second collection** | `OutputNode.Collection` supports only `"lines"` (`OutputNode.cs:54-56`). Offering a picker with one option teaches a capability that does not exist. |
| **Editable raw AST JSON** | A corruption vector with no validation path, and it lets a user author the exact invalid states §13.2 exists to catch. Read-only `View as JSON` only. |
| **cXML / UBL / X12 in the tree** | The emitter's own reasoning (`:64-74`) is correct and this spec's job is to make it *legible*, not to route around it. |
| **Peppol BIS and EDIFACT anywhere** | There is no order transform for either. Listing them would be exactly the overclaim §9.3 exists to prevent. |
| **XSD / schema validation of namespaced XML** | Well-formedness is checkable here; conformance is `ConformanceService`'s job and belongs on the standards surface. Half-validating would be worse than not. |
| **Layout version history / diff UI** | `SupplierConnectionRevision` exists server-side. Surfacing it here adds *revision* to the vocabulary — explicitly banned — and doubles the concept count for a rare need. The order-vs-supplier divergence warning (§4.4) covers the one case that actually bites. |
| **Full undo/redo stack** | Out of proportion. But **single-level undo is in scope** for the three destructive actions — delete a node, reorder, and paste-infer replacing the whole tree — because losing a subtree with no recourse is the cruellest thing this screen can do. Toast-anchored `Undo`, 8s. |
| **Per-node notes / comments** | Real need (*"Contoso asked for this in March"*), wrong surface. Belongs on the supplier, not on a node. |
| **A shared layout library across workspaces** | Requires a trust and moderation story that does not exist. `Copy from another supplier` within a workspace is 90% of the value. |
| **Live collaboration** | No. |
| **A third "source" column in the designer** | Tempting, given the Order Workshop's triptych. But the binding pill's resolved sample value (§3.3, item 5) answers the same question in 40px instead of 380px, and the picker already shows samples. Spending a column on it would squeeze the two panes that matter. |

---

## 22. Implementation order

Six slices, each independently shippable and each leaving the screen better than it found it.

1. **Truth and safety.** Add `expression` to the frontend `OutputFieldRule` and make every write path spread rather than rebuild the rule (stops silent `Expression` loss). Fix the 4.39:1 warning. Add `--border-control`. Wrap the dialog in the Radix primitive for `aria-modal` + focus trap + Escape + focus restore. 16px inputs and 44px targets below 860px. **No new features — this is the floor.**
2. **Reorder.** `moveAt` in `outputNamespaceModel.ts` with tests; pointer drag; `Alt+↑/↓` and handle arrows; live region; CSV column indices; the 390px move sheet; single-level undo.
3. **The fork and the problems strip.** §9 in full (fork, standards panel, capability map, end the silent rewrite) and §13 Tier 1 + Tier 2 separation. These ship together because the cXML case is both a fork and a Tier-1 problem.
4. **The inspector.** Extract the four inline editors into the Details region; add the transform stack with the value trace; `Combine fields`; the structured conditional builder with the tester; CSV condition honesty.
5. **Dialect, namespaces, typed values.** Region B, the CSV panel with the CRLF migration, the namespace presets and the actionable mode collision, `Send as` with the propose-then-confirm chip. Each needs its backend counterpart from §20.
6. **Reuse.** The supplier Output tab and route, the scope chip and its four states, the footer commitment, the success state, divergence, `Copy from another supplier`. Gated on WP-12; ships last because it is the only slice that cannot be faked.

