# Draggable wires in the order-review triptych — design

**Status:** DESIGN (founder chose "design both directions first", 2026-06-08). No code yet.
**Surface:** `project-proculink/src/components/bridge/SpineReview.tsx` (triptych) + `SpineConnectors.tsx` (the SVG wires) + backend `OrderMappingOverride` (`ProcuLink.Core/Services/Mapping/`).

## Goal (founder, 2026-06-08)
> "the wires should be drag-and-droppable (IN ORDER VIEW) from source → canonical and from canonical → output … currently the wires just show what is connected but I want to be able to *rearrange* them."

The flow stays **parse → canonical → output** (unchanged). What we add is the ability to **re-wire** two of the three hops by dragging a wire endpoint onto a different field.

## Current state (what the wires are today)
- `SpineConnectors` draws two bezier sets over a CSS grid, anchored to **real DOM rects** (source-section → canonical-node → output-line), measured each layout change. It is **`aria-hidden`, `pointerEvents:none` — purely decorative** (provenance visualisation). It cannot be interacted with.
- The canonical model is **fixed** (`PoNumber/OrderDate/BuyerName/Currency/SupplierName` + line fields). The parser populates it; the user can already (a) resolve a line's supplier code (ManualCodeRow), and (b) override canonical→output mapping via the **"Edit mapping" panel** (`OutputMappingEditor` → `OrderMappingOverride.Output`, CSV/JSON, persisted in `purchase_orders.canonical_json`).

## The two hops are NOT symmetric

### Hop B — canonical → output (RIGHT wires) — FEASIBLE NOW (Phase 1)
Re-wiring "which canonical field feeds which delivered field" is **exactly** what `OrderMappingOverride.Output` (`OutputMappingConfig`) already models and what `MappedTransformService` already executes (CSV+JSON). So the drag is a **visual front-end over an engine that already exists** — low backend risk.

**Interaction:** each output line gets a left-edge **drop target**; each canonical card gets a right-edge **drag handle** (a real, `pointerEvents:auto` SVG/HTML node layered above the decorative wire `<g>`, which stays `none`). Drag a canonical card → drop on an output line → set that output field's `OutputFieldRule.canonicalField`. Live-preview + Save reuse the existing `/mapping-override` endpoints. A dropped connection renders as a solid wire immediately (optimistic) and is confirmed on Save.

**Data model:** none new — write `OutputMappingConfig.{header|lines}[outputField].canonicalField`. Default (no override) unchanged.

**Effort/risk:** M. The fiddly part is SVG endpoint hit-testing + keeping the decorative measurement and the interactive handles in sync (the same re-measure machinery we just hardened against the refetch-null flicker). Keep the `OutputMappingEditor` form as the **keyboard/a11y fallback** (drag is an enhancement, never the only path).

### Hop A — source → canonical (LEFT wires) — NEEDS BACKEND + SOURCE DISCRETISATION (Phase 2)
Re-wiring "which *source value* populates which canonical field" has **two missing prerequisites**:

1. **The source side is not discrete.** The left column renders a *reconstructed document preview* (text), not a set of addressable fields. To drag *from* a source value you must first **discretise the source into labelled tokens/regions** (e.g. per-cell for CSV/XLSX, per-leaf for XML/EDI, per-extracted-span for PDF). The parsers already know field provenance internally (`srcRef` zones exist for the coarse header/parties/lines/totals bands) but not at the value level. This is real parser/UX work.
2. **No backend remap exists.** Canonical fields are filled by the parser; nothing lets a user say "the value the parser put in `BuyerName` should instead populate `SupplierName`" or "this unmapped source cell → `Currency`". This needs a new override concept — e.g. `OrderMappingOverride.SourceMap: { canonicalField → {sourceToken | fixedValue | manipulators} }` — applied **between parse and the typed columns** (re-deriving the canonical view without re-parsing), with the same NeedsReview guard so a remap can never deliver an unresolved order. This overlaps the previously-deferred-as-risky "field reassignment (E)" because remapping the supplier identity re-routes delivery.

**Effort/risk:** L–XL, and it touches the proven parse→canonical path. Must be specced + reviewed on its own before any code.

## Recommended phasing
1. **Phase 1 — canonical→output drag** on the RIGHT wires (this doc's low-risk half). Ship behind the existing override engine; `OutputMappingEditor` stays as the a11y fallback. Verify live on prod (a real needs-review order).
2. **Phase 2 — source→canonical** only after a dedicated design: (a) value-level source discretisation per parser, (b) the `SourceMap` override + re-derive step + guard, (c) explicit handling that a supplier-identity remap re-routes delivery (route via the supplier picker, not a wire). Security/tenancy review required (it mutates what gets delivered).

## Cross-cutting
- **Tech:** keep the CSS-grid + SVG overlay. Do NOT adopt React Flow / dnd-kit (≈50 KB, would force rebuilding DocumentAnatomy/inline-edit/AI-cards; fights the readable triptych). Use native pointer events + the hardened `scheduleMeasure`/ResizeObserver already in `SpineConnectors`.
- **Mobile/tablet:** the triptych + wires are `xl:` only; mobile keeps the accordion + the `OutputMappingEditor` form. Drag is a desktop enhancement.
- **A11y:** every drag action must have a keyboard/dropdown equivalent (the editor already provides it). The wire endpoints get `role`/`aria` + keyboard "connect mode".
- **Don't:** offer drag targets for PO# / supplier identity inline (backend 400s + offer⇔works + delivery re-route); let a drag persist locally only (breaks the server NeedsReview send-guard).

## Open questions for the founder before Phase 2
- For source→canonical, is the real need "fix the occasional mis-parse" (→ a lightweight per-field "pick the right source cell" picker may beat full drag), or "design arbitrary source→canonical maps" (→ the full SourceMap engine)?
- Should a saved re-wire be **per-order** (like the current override) or **promoted to the supplier mapping** (learned, reused next time)?
