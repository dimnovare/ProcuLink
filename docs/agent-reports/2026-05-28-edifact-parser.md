# EDIFACT ORDERS parser — agent report (2026-05-28)

## Library decision

**Rolled our own.** Evaluated `indice.Edi` (indice-co/edi.net, MIT, NuGet
`indice.Edi`) and `EdiFabric` (commercial). Both rejected for D-Day-One:

- `indice.Edi` is attribute/POCO-driven. Cleanly consuming an ORDERS D96A
  message requires a full message-dictionary POCO hierarchy (~600 LOC).
  We'd write more glue than parser logic.
- `EdiFabric` is commercial (~€1,500/yr). Premature until ORDRSP / DESADV /
  INVOIC are on the roadmap (already flagged in `docs/format-channel-roadmap.md`).
- EDIFACT segment grammar is stable. Our minimal tokenizer handles UNA
  delimiter customization, the release-character escape, and D96A / D01B
  variance (the segments we care about are structurally identical across the
  two versions).

Reasoning is captured in a header comment at the top of `EdifactOrderParser.cs`.

## Segment coverage

Header: `UNA` (delimiters), `UNB` (interchange — read but unused), `UNH`
(validates `ORDERS` message type), `BGM` (PO number from element 2), `DTM`
(qualifier `137` → `OrderDate`), `NAD` (qualifier `BY` → `BuyerName`,
multi-line C080 party-name composite), `CUX` (qualifier `2` → `Currency`).

Lines: `LIN` (line number + buyer item code from element 3 component 0),
`IMD` (description from element 3 components 3-4), `QTY` (qualifier `21` or
`1` → quantity + unit code at component 2 — preserves `PCE`, `KGM`, etc.),
`PRI` (qualifier `AAA`/`AAB` → unit price).

Trailers (`UNS`, `UNT`, `UNZ`) flush the in-flight line and are otherwise
ignored. Other segments (`PIA`, `RFF`, `ALC`, `MOA`, `TAX`, address C058
lines on `NAD`) are intentionally skipped — they are not part of the
canonical PO model today.

D96A is the primary; D01B is tolerated because the segments we read are
positionally identical between the two versions.

## Test counts

13 new tests in `ProcuLink.Transform.Tests/Parsing/EdifactOrderParserTests.cs`:
extension dispatch (1), content-sniffing helpers (2), happy path with 3 lines
(1), mixed `PCE`/`KGM` units (1), DTM 137 extraction (1), NAD BY ordering (1),
custom UNA delimiters (1), and 5 error paths (empty, whitespace, missing
UNH, non-ORDERS message type, missing BGM).

Full Transform suite: **73 passed, 0 failed** (60 existing + 13 new). Full
solution `dotnet build ProcuLink.slnx --no-restore`: clean (0 warnings).

## NuGet package needed

**None.** The parser is pure `System.Text` + `System.Globalization` — no new
package reference required for `ProcuLink.Transform.csproj`.

## `OrderParserFactory` registration

One-liner for `Program.cs` (Api) DI block, alongside the other parsers:

```csharp
services.AddSingleton<IPurchaseOrderParser, EdifactOrderParser>();
```

Worker DI (if it composes parsers independently): same line.

## Known limitations and next steps

1. **Factory dispatch is extension-only.** `CanParse` returns true for `.edi`.
   `.txt` content-sniffing and `Content-Type: application/edifact` /
   `application/edi-x12` dispatch are stubbed via two public statics
   (`EdifactOrderParser.LooksLikeEdifact(string head)`,
   `EdifactOrderParser.IsEdifactContentType(string? ct)`) but not wired —
   that requires upgrading `OrderParserFactory` to take both extension and
   content-type / payload-head, which is out of scope per the brief.
2. **No address lines / GLN extraction.** `NAD` party name is captured;
   address C058, country, and GLN identifiers are dropped. Add when canonical
   PO grows a `BuyerAddress` field.
3. **No multi-message interchange.** First (and only) ORDERS message in the
   interchange is parsed. Splitting on multiple `UNH/UNT` envelopes is a
   future addition once batching is on the roadmap.
4. **Description coverage limited to IMD F+ANM.** Other free-text qualifiers
   (`IMD+B`, `IMD+E`) are not concatenated.
5. **Currency only from `CUX+2`.** No fallback to line-level pricing
   currency. Add if a customer hits this.
6. **Upload accept-list and front-end:** `OrdersController.Upload` and
   `FileUploadZone` still cap at `.csv/.xlsx/.pdf/.xml/.cxml` — `.edi` needs
   adding when the parser is registered.
