# Phase B — OutputNode AST + format-aware emitters (WS-1 / WS-2 / WS-12)

**Parent:** [`docs/strategy/2026-06-15-output-layer-restructuring-masterplan.md`](../../strategy/2026-06-15-output-layer-restructuring-masterplan.md) §M.2, P2.C.
**Goal:** replace the flat, six-mode, hardcoded-tree output layer with ONE recursive `OutputNode` AST rendered by format-aware emitters — so a user can shape **arbitrary output structure** (nesting, arrays, attributes, wrapper/root names, column order, footers, EDI envelope identity), not just remap leaf values onto a frozen skeleton.

This is the **structural cut**. It must land behind the existing engine with a **byte-parity gate** (existing live suppliers deliver byte-identical output) before anything is cut over.

---

## The model (WS-1)

A recursive node tree. Each node is one of four kinds:

```
OutputNode
  Name      : string                      // element / json property / csv column / segment id
  NodeType  : Object | Array | Field | Attribute
  Children  : List<OutputNode>            // Object/Array only
  Rule      : OutputFieldRule?            // Field/Attribute only — REUSES the existing rule
                                          // (source token / fixed value / canonical field / Scriban
                                          //  Expression / manipulator chain). No new value logic.
```

- **Object** — a wrapper/element with named children (a JSON object, an XML element, a UBL group).
- **Array** — a repeating group bound to a collection (the order's lines). Its single child template is rendered once per line; field rules inside resolve against the *current line* context.
- **Field** — a leaf value: resolves one `OutputFieldRule` against the current context (order header or current line). Emits a JSON value / XML element text / CSV cell.
- **Attribute** — an XML attribute (`name="value"`); ignored by JSON/CSV emitters.

```
OutputTemplate
  Format    : OutputFormat                // csv | json | xml | cxml | ubl | x12
  Root      : OutputNode                  // the document root
  Envelope  : EnvelopeConfig?             // EDI/cXML identity (WS-12)
```

```
EnvelopeConfig                            // WS-12 — identity as DATA, not constants
  X12   : { isaSenderQualifier, isaSenderId, isaReceiverQualifier, isaReceiverId,
            version, usageIndicator (T|P), elementSep, segmentSep, componentSep }
  Cxml  : { fromDomain, fromIdentity, toDomain, toIdentity,
            senderDomain, senderIdentity, senderSharedSecret(ref) }
```

**Why it reuses `OutputFieldRule`:** the *value* of every leaf is already fully expressible by the existing rule (fixed / canonical / source-token / Scriban expression / manipulator chain). The AST only adds **structure**; value resolution is unchanged → the manipulator/Scriban machinery is reused verbatim, and per-field flexibility that already exists in the backend becomes reachable.

---

## The emitters (WS-2)

One tree-walking emitter per **serialization family**, all consuming the same `OutputNode` tree:

- **StructuredEmitter** → JSON (`Utf8JsonWriter`) and the XML family (XML / cXML / UBL via `XmlWriter`/`XElement`). Honors Object nesting, Array repetition, Attribute-vs-element, custom root/wrapper names, namespaces.
- **DelimitedEmitter** → CSV. Flattens the tree to columns; an Array node becomes the row dimension (one row per line); Object nodes prefix/group columns; supports column order, header modes, footer rows, delimiter.
- **X12Emitter** → the 850 segment stream, driven by the tree + `EnvelopeConfig` for ISA/GS identity.

Each `Field`/`Attribute` node resolves its value through the **same** code path `MappedTransformService` uses today (see grounding `ground:native-resolve`) — extracted into a shared `FieldValueResolver` so there is exactly one value-resolution implementation.

---

## Byte-parity gate (non-negotiable)

The current hardcoded transformers become the **default `OutputTemplate`** for each format. Rendering the default template MUST produce **byte-identical** output to the current transformer for the real corpus (`~/Downloads/PO`, 24 POs) and the existing transform test fixtures. Until parity holds, the new path stays opt-in and the old path remains the default.

Flat `OutputMappingConfig` (today's overrides) → a **converter** lifts it into an equivalent `OutputNode` tree (header object + a `lines` array of field nodes), so existing supplier/revision overrides render identically through the new emitter.

---

## Build order (within Phase B)

1. **B1 — Model.** `OutputNode` + `OutputTemplate` + `EnvelopeConfig` in `ProcuLink.Core` (or `ProcuLink.Transform`). Pure types + validation. Unit tests for shape.
2. **B2 — FieldValueResolver.** Extract the existing leaf-value resolution (manipulator chain + fixed/canonical/expression precedence) from `MappedTransformService` into a shared resolver used by both the legacy builder and the new emitters. Characterization tests proving identical values.
3. **B3 — StructuredEmitter (JSON + XML).** Render `OutputNode` → JSON and XML. Tests prove arbitrary nesting / arrays / attributes / wrapper rename — the capability that is impossible today. THIS is the proof-of-concept of "design the output".
4. **B4 — Default templates + byte-parity.** Author the default `OutputTemplate` per format = today's hardcoded tree; assert byte-identical vs the current transformer across the test corpus.
5. **B5 — Flat→tree converter.** `OutputMappingConfig` → `OutputNode`; existing overrides render identically.
6. **B6 — Wire the new render mode** in `OrderTransformService` (opt-in behind a flag/precedence), preview == delivery via the shared path.
7. **B7 — Delete the dead `IParsedOrderTransform` stack** (WS-11).
8. **B12 — EnvelopeConfig** per-connection persistence + X12Emitter/cXML identity wiring (independently shippable).

**This turn targets B1 + B3** (model + structured emitter proving arbitrary structure, TDD) — the contained, high-value core. B2/B4–B12 follow with the byte-parity gate before any cutover.

---

## Constraints
- No commercial EDI licences (hand-rolled X12/UBL). Reuse existing transformers' logic as the default templates.
- Reuse `ManipulatorRegistry` / `ScribanFieldEvaluator` verbatim — no new value semantics.
- Postgres, not InMemory, for anything touching persistence; full Api.Tests after any `ValidateOrderAsync`/transform change.
- Nothing cut over until byte-parity holds on the real corpus.

*(Exact reuse signatures filled in from grounding workflow `w0yzc4w12`.)*
