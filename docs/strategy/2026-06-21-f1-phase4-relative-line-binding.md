# F-1 Phase 4 — Relative line-column binding (bind a repeating column for ALL lines)

> Status: **DESIGN.** Follow-on to F-1 (shipped `a24d2d9`/`7e5bde5`, live-proven 2026-06-21).
> Grounded on `main`. Closes the gap found in the live proof: a line-scoped source token is an
> ABSOLUTE id (`cell:r2c2`), so binding it gives line 1 row-2's value and lines 2/3 **empty**. A user
> wants "bind THIS column for every line" with one rule — that needs a RELATIVE address.

## The gap (proven live)
F-1 injects each line's matched line tokens into that line's bag under `src::{absoluteId}`
(`MappedTransformService.cs:439`). A rule bound to `cell:r2c2` resolves only in line 1's bag (the
indexer maps `r2`→line 1); lines 2/3 lack that key → empty. Header/order-level fields work for every
line (header bag); per-line REPEATING columns (a non-canonical "warehouse"/"customs code"/per-line
note) cannot be bound once. This phase adds that.

## Design — a relative alias key, same across lines
When `BuildLineRow` injects line N's matched tokens, ALSO write a RELATIVE alias whose key is
identical across lines (the ordinal stripped). One rule bound to the relative key then resolves to
**each line's own** value. Additive + byte-safe (extra inert keys, same invariant as F-1).

```csharp
// MappedTransformService.cs ~:439 — inside the existing matched-line-token loop, ADD:
row[$"src::{t.Id}"] = t.Value ?? string.Empty;                 // absolute (today)
var rel = SourceTokenLineIndexer.RelativeLineKey(t.Id);        // NEW
if (rel is not null) row[$"src::{rel}"] = t.Value ?? string.Empty;  // relative alias (this line's value)
```

## The relative-id format (the shared BE↔FE contract — ONE source of truth = the helper)
`SourceTokenLineIndexer.RelativeLineKey(string id) → string?` (pure; null for non-line / no-ordinal):
- **CSV / XLSX** `cell:r{n}c{c}` → `cell:c{c}` (strip the row, keep the column).
- **XML / cXML / UBL / IDoc** XPath — strip the DEEPEST `[n]` positional predicate only:
  `/Order/Lines/Line[2]/Qty` → `/Order/Lines/Line/Qty`. (Only the line predicate; leave other predicates.)
- **EDIFACT / X12** `seg:{TAG}[{n}].el…` → `seg:{TAG}.el…` (strip the occurrence `[n]`).
- **JSON** `json:/…/{i}/…` → replace the FIRST array index with `*`: `json:/lines/0/sku` → `json:/lines/*/sku`.
- Non-line / no resolvable ordinal → `null` (no relative alias; absolute still works).

The relative key cannot collide with an absolute id (absolutes always carry the ordinal/row) nor a
canonical/custom key (the guarded `::` namespace).

## Expose it to the picker
`GET /api/orders/{id}/source-tokens` adds `relativeId` (string?) to each token DTO, computed via the
SAME `RelativeLineKey` helper. The FE output picker offers a line column as a **"per line"** binding
(writes `sourceToken = token.relativeId`) AS THE DEFAULT for line-group tokens, with the absolute
per-row cells available under an "exact cell" advanced toggle. Header tokens are unaffected (no
relativeId). One `sourceToken` field carries either form — the BE resolves `row["src::"+sourceToken]`
identically for both.

## Byte-safety & tests (same rigor as F-1)
1. **Parity oracle** — extend `OutputTemplateEmitterByteParityTests`: inject relative aliases too, with
   NO rule → byte-identical (the relative keys are inert unless named).
2. **Per-line correctness** — a rule bound to the relative key emits EACH line's own column value:
   line 1 → row-2's value, line 2 → row-3's value (the live gap, now fixed). Golden per format
   (CSV `cell:c2`, XML `…/Line/Qty`, EDI `seg:LIN.el1`, JSON `json:/lines/*/sku`).
2b. **`RelativeLineKey` unit tests** — per-format strip, and null for header/no-ordinal/`raw:` ids.
3. **No regression** — absolute-id binding still works (specific line); existing rules (no sourceToken)
   byte-identical; not-found relative key falls through.

## Phasing
- **4a (BE)** — `RelativeLineKey` helper + the alias injection + `relativeId` on the source-tokens DTO
  + tests. No migration.
- **4b (FE)** — picker offers the "per line" (relative) binding as default for line tokens + an "exact
  cell" toggle; RTL test.
- **4c (defer)** — promote a bound source field to the reusable supplier `PoMappingConfig` via the
  existing `PromoteMappingService` (no new concept; reuses revision pinning). Separate follow-up.

## Acceptance
- Binding a repeating non-canonical column once emits **each line's own** value across JSON/XML/CSV,
  preview == delivery.
- An unbound order is byte-identical (parity oracle green).
- Absolute per-cell binding still works; header/order-level binding unchanged.
