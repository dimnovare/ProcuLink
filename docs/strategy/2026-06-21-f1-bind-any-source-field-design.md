# F-1 — Bind ANY Incoming Source Field (Implementation-Ready Design)

> Status: **DESIGN. No product code written.** Grounded against the live tree on `main`
> (2026-06-21). Every claim cites `file:line`.
> Parent: the redesign analysis [`docs/strategy/2026-06-19-po-tool-redesign-analysis.md`](2026-06-19-po-tool-redesign-analysis.md),
> root cause **RC-D** and Output-Designer-Design point **4** ("Binding dropdown exposes the REAL source").
> Scope here is strictly F-1: let the output binding picker offer **every** source field, not just
> the ~13 canonical names — reusing the SourceCapture token universe that already exists.

---

## 1. The problem, stated against the code

`ParsedOrder` is a 12-arg record — 5 "real" header fields + enrichment
(`ProcuLink.Transform/Parsing/ParsedOrder.cs:7`): `PoNumber`, `OrderDate`, `BuyerName`,
`Currency`, then `SupplierName`, `SubTotal`, `TaxTotal`, `GrandTotal`, `PaymentTerms`,
`DocumentType`, `RequestedDeliveryDate`, plus the additive `Parties`, contact fields,
`Incoterms`, `ShippingMethod`, `BuyerOrderRef`, `RawFields`.
`ParsedOrderLine` (`ParsedOrderLine.cs:8`) carries `LineNumber`, `BuyerItemCode`,
`Description`, `Quantity`, `Unit`, `UnitPrice` + enrichment.

But the output **binding universe** is NOT `ParsedOrder` — it is a flat
`Dictionary<string,string>` row bag built by
`MappedTransformService.BuildHeaderRow` (`MappedTransformService.cs:267`) and
`BuildLineRow` (`MappedTransformService.cs:343`). That bag contains **exactly** these keys
and nothing else:

- header: `PoNumber`, `OrderDate`, `BuyerName`, `Currency`, `SupplierName`, `SubTotal`,
  `TaxTotal`, `GrandTotal`, `PaymentTerms`, `RequestedDeliveryDate` (`MappedTransformService.cs:301-316`)
- line (adds, on top of the header bag): `LineNumber`, `BuyerItemCode`, `SupplierItemCode`,
  `Description`, `Quantity`, `Unit`, `UnitPrice`, `LineTotal`, `LineAmount`, `TaxRate`,
  `DeliveryDate` (`MappedTransformService.cs:350-368`)
- plus any `OrderMappingOverride.CustomFields` keyed by `cf.Key` (`MappedTransformService.cs:318-323`, `:370-378`)
- plus reserved catalog keys `__catalog_*` when a catalog row matches (`MappedTransformService.cs:412-417`)

**Every** binding path reads from this one bag:

| Path | Where | How it reads the bag |
|---|---|---|
| Output node leaf (`OutputFieldRule.CanonicalField`) | `MappedTransformService.ResolveRule` → `ResolveExpressionOrField` `:253` | `row.TryGetValue(fallbackCanonicalField, …)` |
| AST emitter (JSON/XML/CSV) | `OutputTemplateEmitter.cs:130, :256, :299, :309` | calls `MappedTransformService.ResolveRule(node.Rule, row, …)` |
| Scriban `order.*` / `line.*` | `ScribanFieldEvaluator.BuildScopeObject` `:161` | iterates the SAME `row` dict into the scope object |
| SourceMap re-derive | `SourceMapReDerive.ApplyRule` `:108` | only fires for a fixed list of canonical field names (`:63`, `:89`) |

So an arbitrary incoming field (a party VAT id, an EAN, a free-text note) is **unreachable**
from the output designer today even though the engine could emit it — there is simply no key
for it in the row bag, and the frontend picker only lists the 13 names
(`project-proculink/src/lib/api/types.ts:299-301`):

```ts
export const CANONICAL_HEADER_FIELDS = ["PoNumber","OrderDate","BuyerName","Currency","SupplierName"] as const;
export const CANONICAL_LINE_FIELDS  = ["LineNumber","BuyerItemCode","SupplierItemCode","Description","Quantity","Unit","UnitPrice","LineTotal"] as const;
```

**The choke-point is the row bag, not the canonical record.** Widen the bag and every
downstream reader — picker, `CanonicalField`, Scriban, the AST emitter — gains the new fields
for free.

---

## 2. The ~70% that already exists (REUSE — do not rebuild)

| Already built | Evidence | What it gives us |
|---|---|---|
| **The full source-token universe, captured & persisted.** `SourceTokenizer` emits every cell / XML leaf+attr / EDI element / X12 element / JSON leaf as `SourceToken(Id,Label,Value,Group)` | `ProcuLink.Transform/Tokenizing/SourceTokenizer.cs:61-913`; `SourceToken.cs:38` | the candidate field list, **with sample values**, per format |
| **Persisted lossless capture** — one `source_captures` row per order holding the full token set as jsonb, immutable, survives blob purge | `ProcuLink.Core/Entities/SourceCapture.cs:14-30` (`TokensJson`); written at ingest by `OrderIngestionService` (`UpsertSourceCaptureAsync`) | the universe is available at transform/preview time without an R2 round-trip |
| **Token rehydration** from the persisted capture back to `IReadOnlyList<SourceToken>` (incl. synthesised `raw:{label}` ids for PDF/email `raw_fields`) | `ProcuLink.Transform/Output/SourceTokenSerialization.cs:28-53` | turn the stored jsonb back into addressable tokens |
| **An API that already returns the universe** with id/label/value/group, preferring the persisted capture | `GET /api/orders/{id}/source-tokens` — `OrdersController.cs:1165-1221` | the picker's data source already exists |
| **Token → value binding at emit, end-to-end.** `SourceFieldRule.SourceToken` resolves a token id to its value, then runs manipulators; wired through preview AND delivery so they re-derive identically | `OrderMappingOverride.cs:126` (`SourceFieldRule.SourceToken`); `SourceMapReDerive.cs:140-146`; preview passes `previewTokens` at `OrdersController.cs:965-972`; transform passes the same | a token already flows to the output **when** it is bound to a named canonical field |
| **Frontend SourcePicker** that lists tokens grouped by header/line/parties/raw with typeahead on label+value, AI-suggestion pre-fill | `project-proculink/.../mapper/SourcePickerChip.tsx:37-272`; consumed in `OutgoingPane.tsx` | the exact UI we need — today wired only to the **input** (source→canonical) side |
| **OutputNode AST + format-aware emitter** (nesting/arrays/attrs/namespaces/`IncludeWhen`), byte-identical leaf resolution to the flat builder | `OutputNode.cs:39-100`; `OutputTemplateEmitter.cs:33-380` | the output model that will carry the new binding |
| **`source-tokens` client + types** on the FE | `api-client.ts` `getSourceTokens`; `SourceToken` type `types.ts:246-255` | no new fetch plumbing |

**What is missing is small and specific:** a way to bind an output node *directly* to a token
id (today `OutputFieldRule` has `canonicalField | fixedValue | expression`, **no `sourceToken`**),
and exposing those tokens to the **output** picker (today the picker only powers the input side,
and the output editor's dropdown is the hard-coded 13).

---

## 3. The seam to extend (minimal-change first)

There are two viable seams. We pick **Seam A** as the primary; Seam B is the explicit,
type-safe finisher that builds on it.

### Seam A (Phase 2 core) — inject tokens into the row bag under a reserved namespace

Add the source tokens to the SAME `Dictionary<string,string>` row bag, under reserved keys that
cannot collide with a canonical name or a user custom-field key. Then **nothing downstream
changes**: `CanonicalField`, Scriban `order.*`/`line.*`, and the AST emitter all already read
the bag.

Reserved key shape: `src::{tokenId}` (the `::` separator is impossible in a canonical name and
we will reject it in custom-field keys). Header tokens (`Group=="header"` or `null`) go into the
header bag; line tokens (`Group=="line"`) go into each line bag, addressed by the token id —
and, for repeating structured tokens (XML `…/Line[2]/Qty`, EDI `LIN[2]`), mapped to the
**matching line ordinal** (see §6, repeating-line resolution).

```csharp
// MappedTransformService.BuildHeaderRow — append, after the canonical keys + custom fields:
foreach (var t in headerTokens)            // tokens with Group != "line"
    row[$"src::{t.Id}"] = t.Value;         // verbatim value, never numeric-parsed here
```

Why this is the minimal change:
- `OutputFieldRule.CanonicalField = "src::cell:r1c7"` resolves through the **unchanged**
  `ResolveExpressionOrField` (`MappedTransformService.cs:253`) — `row.TryGetValue` hits the new key.
- `order["src::…"]` / `line["src::…"]` become reachable in Scriban because
  `BuildScopeObject` (`ScribanFieldEvaluator.cs:161`) copies the whole bag (a `src::` key is a
  legal `ScriptObject` member accessed as `order["src::cell:r1c7"]`).
- The AST emitter is untouched — it already calls `ResolveRule(node.Rule, row, …)`.
- **Byte-safety:** when no rule references a `src::` key, output is byte-identical — extra keys
  in the bag are never emitted (the fixed transforms don't read the bag at all;
  `MappedTransformService.cs:264-265` notes "Adding a key to the row bag cannot change
  fixed-transform output").

The token list to inject is **already threaded** into both `BuildHeaderRow`/`BuildLineRow`
call sites via the `sourceTokens` parameter that `MappedTransformService.Build`,
`OutputTemplateEmitter.Emit`, and `SourceMapReDerive` all already receive
(`MappedTransformService.cs:82`, `OutputTemplateEmitter.cs:37`). The only plumbing change is to
pass that same list one level deeper into `BuildHeaderRow`/`BuildLineRow` so they can append the
`src::` keys (today the list reaches `SourceMapReDerive` but not the row builders).

### Seam B (Phase 3 finisher) — first-class `OutputFieldRule.SourceToken`

Add a typed `SourceToken` property to `OutputFieldRule` (mirroring `SourceFieldRule.SourceToken`)
so a node states its binding explicitly rather than through a `src::`-prefixed `CanonicalField`
string. Resolution precedence in `ResolveExpressionOrField` becomes
`Expression → SourceToken → FixedValue → CanonicalField`. This is the clean, discoverable,
serialization-friendly shape; it reuses the Seam-A row injection for the actual value lookup
(the property just selects `row["src::"+rule.SourceToken]`), so it is additive and back-compat.

We ship **A first** (smallest diff, proves the whole flow with the picker writing a
`canonicalField: "src::…"`), then **B** as the type-safe surface the picker writes once A is
proven.

---

## 4. Data flow (already-built vs new)

```
 source file ──parse──▶ SourceTokenizer ──▶ SourceCapture.TokensJson (jsonb)        [BUILT]
                                              (immutable, per order, survives purge)

 ┌─ picker options ──────────────────────────────────────────────────────────────┐
 │  GET /api/orders/{id}/source-tokens ──▶ SourceToken[] {id,label,value,group}     [BUILT]
 │     OutputMappingEditor / OutputStructureDesigner show these as bindable options [NEW: wire to output picker]
 └──────────────────────────────────────────────────────────────────────────────┘
                                   │ user picks a token for an output node
                                   ▼
 OutputFieldRule  { sourceToken: "cell:r1c7" }  (Seam B)  OR  { canonicalField: "src::cell:r1c7" } (Seam A) [NEW]
                                   │  PUT /api/orders/{id}/mapping-override                            [BUILT route]
                                   ▼
 transform / preview:
   tokens = SourceTokenSerialization.FromTokensJson(SourceCapture.TokensJson)        [BUILT]
   BuildHeaderRow/BuildLineRow(order, override, tokens) ── inject row["src::{id}"]    [NEW: 1 param + 1 loop each]
   OutputTemplateEmitter / MappedTransformService.ResolveRule(node.Rule, row)         [BUILT — unchanged]
                                   ▼
                            delivered bytes  (preview == delivery, same code path)    [BUILT]
```

Already built: capture, persistence, rehydration, the `source-tokens` API, the picker UI
component, the AST + emitter, the preview==delivery token plumbing.
New: (1) inject tokens into the row bag (Seam A); (2) `OutputFieldRule.SourceToken` (Seam B);
(3) point the **output** picker at `source-tokens` and write the binding.

---

## 5. Byte-safety & back-compat invariants

1. **No reference → no change.** A `src::` key in the row bag is inert unless a rule names it.
   The fixed structured transforms never read the bag at all
   (`MappedTransformService.cs:264-265`), and the flat/AST builders only emit nodes that exist
   in the override tree. An order with no `src::` binding produces **byte-identical** output.
   Pin this with an `OutputTemplateEmitterByteParityTests`-style oracle
   (`ProcuLink.Transform.Tests/Output/OutputTemplateEmitterByteParityTests.cs` already exists).
2. **Reserved namespace can't collide.** `src::` (double colon) is not a legal canonical name and
   not a legal custom-field key. Add a guard: `CanonicalFieldsController.Create` and the
   custom-field path reject a key containing `::` (defensive; the `::` is already
   un-typeable through the existing key inputs).
3. **Verbatim values.** Inject `token.Value` raw, exactly as `SourceTokenSerialization` carries it
   (`SourceTokenSerialization.cs:23-24` — "never numeric-parsed here"). EU locale prices
   (`1.234,56`) survive; numeric coercion stays downstream where arithmetic happens. A `src::`
   token bound to a numeric output is the author's choice + manipulator chain, identical to how a
   `SourceFieldRule.SourceToken` already behaves.
4. **Unresolved-line guard is untouched.** `SupplierItemCode` is still produced by the canonical
   path; the `src::` keys are additive and never feed the
   `NeedsReview || string.IsNullOrWhiteSpace(SupplierItemCode)` guard
   (`MappedTransformService.cs:432-433`, `OutputTemplateEmitter.cs:321-325`,
   `SourceMapReDerive.cs:100-101`). A source-field binding can never bypass review.
5. **Preview == delivery by construction.** Both paths already build tokens from
   `SourceCapture.TokensJson` (`OrdersController.cs:965-966` for preview; the transform job for
   delivery) and call the same `ResolveRule`. Injecting in the shared `BuildHeaderRow/BuildLineRow`
   keeps them equal automatically.
6. **Fail-open Scriban unchanged.** `order["src::missing"]` renders empty via relaxed member
   access (`ScribanFieldEvaluator.cs:105`), same contract as a missing canonical key.

---

## 6. Known risks & how the design handles them

- **Repeating-line token resolution.** A line token id is global (`…/Line[2]/Qty`, `LIN[2]`,
  CSV `cell:r3c4`), but the row bag is built **per line**. Injecting *all* line tokens into
  *every* line bag would let line 1 read line 2's value. **Resolution:** when building a given
  line's bag, inject only the `Group=="line"` tokens whose addressed ordinal matches that line's
  position (XPath `[n]` predicate, EDI/X12 `[n]` occurrence, CSV/XLSX `r{n}` → the n-th data row).
  Tokens whose ordinal can't be matched to a line are injected with their global id into the
  **header** bag only (still bindable, header-scope). Add a `SourceTokenLineIndexer` helper +
  golden tests per format. This is the one genuinely new piece of logic; keep it pure and tested.
  *(For Phase 2 we MAY ship header-token binding first — the most-requested fields, VAT/EAN at
  header/party level — and gate line-token binding behind the indexer in a follow-up step, since
  header tokens have no ordinal ambiguity.)*
- **Picker noise.** A wide XML/UBL file yields hundreds of tokens. The picker already groups +
  typeaheads on label **and** value (`SourcePickerChip.tsx`); reuse that. Default the output
  picker to show canonical fields first, then a "More source fields…" disclosure listing the raw
  tokens — progressive disclosure per the redesign's "avoid overwhelm" rule.
- **PDF/email orders.** They have no structured tokenizer output, but `SourceCapture` stores the
  LLM `raw_fields`, and `SourceTokenSerialization` synthesises `raw:{label}` ids
  (`SourceTokenSerialization.cs:46-48`). Those bind through the identical `src::raw:{label}` path —
  free coverage for the formats that drop the most fields today.
- **No migration.** The override still lives in `canonical_json.mappingOverride`
  (`OrderMappingOverrideReader.cs:14`). Adding `OutputFieldRule.SourceToken` is an additive record
  property → no schema change, no EF migration (jsonb). *(The redesign's separate
  `order_output_overrides` table — RC-B — is out of F-1 scope; F-1 must not block on it.)*
- **Two-of-everything caution.** Do **not** add a third binding concept. `SourceToken` on
  `OutputFieldRule` is the direct twin of the existing `SourceFieldRule.SourceToken`
  (`OrderMappingOverride.cs:126`); same name, same semantics, same `src::` lookup — one mental model.

---

## 7. Phasing (small, shippable, each with a test)

**Phase 1 — Expose the universe to the output picker (frontend-only, zero engine change).**
- Wire the **output** editor's source picker to `getSourceTokens(orderId)` instead of the 13-name
  array. Files: `OutputMappingEditor.tsx` (replace the `<select>` `sources` list at
  `:143-158`), `OutputStructureDesigner.tsx` (the `newField()` bind control `:82-101`), reusing
  `SourcePickerChip.tsx`. The picked token still writes a *canonical* binding for now if it maps
  to a canonical field, otherwise it is disabled with "binding coming in next release" — OR ship
  Phase 1 together with Phase 2 so the picked token is immediately bindable. (Recommended: ship
  1+2 together; Phase 1 alone has no engine to honor a raw token.)
- Test: FE unit/RTL test that the output picker renders tokens from a mocked `source-tokens`
  response, grouped, with sample values.

**Phase 2 — Seam A: inject tokens into the row bag (the core engine change).**
- `MappedTransformService.BuildHeaderRow(order, override, headerTokens)` and
  `BuildLineRow(order, override, line, catalogLookup, lineTokens)` — add the token param + the
  `row["src::{id}"]=value` loop (`MappedTransformService.cs:267`, `:343`). Thread the already-present
  `sourceTokens` list from `Build` (`:95-96`), `OutputTemplateEmitter.Emit` (`:46-51`), and the
  `EffectiveEntityResolver.Resolve` calls (`EffectiveEntityResolver.cs:77`, `:99`) into those builders.
- Add `SourceTokenLineIndexer` (pure) to map line tokens → line ordinal; header/unmatched tokens →
  header bag.
- Tests:
  - `ProcuLink.Transform.Tests/Output/MappedTransformServiceTests.cs` — a CSV/JSON override whose
    `OutputFieldRule.CanonicalField == "src::cell:r2c5"` emits that cell's verbatim value.
  - `OutputTemplateEmitterTests` — an AST field bound to a `src::` key emits it for JSON/XML/CSV.
  - `OutputTemplateEmitterByteParityTests` — adding `src::` keys to the bag with **no** rule
    referencing them changes nothing (parity oracle).
  - `ScribanExpressionMappingTests` — `{{ order["src::/Order/Header/VatId"] }}` resolves.
  - New `SourceTokenLineIndexerTests` — per-format golden: line[2]'s bag sees only line[2]'s tokens.

**Phase 3 — Seam B: first-class `OutputFieldRule.SourceToken` + picker writes it.**
- Add `string? SourceToken { get; init; }` to `OutputFieldRule` (`OrderMappingOverride.cs:187`),
  precedence `Expression → SourceToken → FixedValue → CanonicalField` in `ResolveExpressionOrField`
  (`MappedTransformService.cs:227-254`) — when set, look up `row["src::"+SourceToken]`.
- Add `sourceToken?: string | null` to the FE `OutputFieldRule` type (`types.ts:48-57`); the
  output picker writes it directly.
- Tests: `OutputFieldValidatorTests` / `MappedTransformServiceTests` — a rule with `SourceToken`
  set (and `canonicalField` null) emits the token value; precedence over `FixedValue`; a
  not-found token id falls through to `FixedValue`/`CanonicalField` (no crash).

**Phase 4 (optional, defer) — promote a bound source field to a reusable supplier template.**
- Same `src::`/`SourceToken` shape promotes from per-order override to the supplier `PoMappingConfig`
  via the existing `PromoteMappingService` — no new concept, reuses revision pinning.

---

## 8. Explicit "reuse vs build" ledger

| Already DONE — reuse as-is | To BUILD (F-1) |
|---|---|
| `SourceTokenizer` (all 8 formats) — `SourceTokenizer.cs` | Inject `src::{id}` keys into the row bag — `BuildHeaderRow`/`BuildLineRow` (Phase 2) |
| `SourceCapture` persistence + `TokensJson` — `SourceCapture.cs` | `SourceTokenLineIndexer` (line-token → line ordinal) (Phase 2) |
| `SourceTokenSerialization.FromTokensJson` (incl. `raw:` ids) | Thread the existing `sourceTokens` list one level into the row builders (Phase 2) |
| `GET /api/orders/{id}/source-tokens` — `OrdersController.cs:1165` | Point the **output** picker at `source-tokens` (Phase 1) |
| `SourceFieldRule.SourceToken` resolution — `SourceMapReDerive.cs:140` | `OutputFieldRule.SourceToken` (typed twin) (Phase 3) |
| `SourcePickerChip` grouped/typeahead picker — `SourcePickerChip.tsx` | Use it in `OutputMappingEditor`/`OutputStructureDesigner` (Phase 1) |
| OutputNode AST + `OutputTemplateEmitter` + preview==delivery | — (untouched; emitter reads the widened bag for free) |
| Override store (`canonical_json.mappingOverride`) + `mapping-override` PUT/preview routes | — (no migration; additive jsonb property) |

---

## 9. Acceptance criteria

- In the output designer, the binding picker lists **every** source field for the order — CSV
  cells, XML elements + attributes, EDI/X12 elements, JSON leaves, PDF/email `raw_fields` — each
  with its label and a **sample value**, not just the 13 canonical names.
- Binding an output node to an arbitrary source field (e.g. a party VAT id / an EAN) emits that
  field's verbatim value in JSON, XML, and CSV, and the **preview shows the same bytes** that
  delivery sends.
- An order with no source-field binding produces **byte-identical** output to today (parity oracle
  green on the real corpus).
- A line-scoped source token resolves to the **correct line's** value (line[2] never reads line[1]).
- A no-egress / PDF order with only `raw_fields` can still bind those fields.
- No EF migration; no new persisted override mode; one binding concept (`SourceToken`), shared
  name/semantics with the input side.
