# UBL 2.1 / Peppol BIS Order 3 — input parser (2026-05-28)

Adds a hand-rolled UBL Order parser (`System.Xml.Linq`, no third-party lib) that also
covers Peppol BIS Order 3.x — the EU procurement standard mandated for public
sector buyers in EE, NO, NL, and increasingly required by private B2B SME networks
through 2027–2028.

## UBL elements parsed → canonical fields

| UBL element | `ParsedOrder` / `ParsedOrderLine` field |
|---|---|
| `Order/cbc:ID` (direct child) | `PoNumber` |
| `Order/cbc:IssueDate` | `OrderDate` (ISO-8601 + tolerant fallbacks) |
| `Order/cbc:DocumentCurrencyCode` | `Currency` (uppercased; falls back to first `PriceAmount@currencyID`) |
| `cac:BuyerCustomerParty/Party/PartyName/Name` (+ `PartyLegalEntity/RegistrationName` fallback) | `BuyerName` |
| `cac:SellerSupplierParty/...` | parsed but *not* mapped (no canonical home; reserved for future supplier-resolution) |
| `cac:OrderLine/cac:LineItem/cbc:ID` | `LineNumber` |
| `cac:LineItem/cbc:Quantity` + `@unitCode` | `Quantity`, `Unit` |
| `cac:LineItem/cac:Price/cbc:PriceAmount` + `@currencyID` | `UnitPrice`, currency-fallback |
| `cac:Item/cac:BuyersItemIdentification/cbc:ID` (priority 1) | `BuyerItemCode` |
| `cac:Item/cac:SellersItemIdentification/cbc:ID` (priority 2) | `BuyerItemCode` (fallback) |
| `cac:Item/cbc:Name` | `Description` + priority-3 `BuyerItemCode` fallback |

Traversal is namespace-tolerant (local-name match) so prefixed (`cbc:`/`cac:`),
default-namespaced, and bare elements all parse uniformly — same approach as
`CxmlOrderParser`.

## Peppol BIS Order 3.x compliance

Peppol BIS 3 is profile-restricted UBL 2.1. The parser handles the BIS 3 customization
identifier (`urn:fdc:peppol.eu:poacc:trns:order:3`) — read but no behavioural branch
because BIS 3 is structurally compatible with UBL 2.1 for the fields we read. Peppol's
mandatory `SellersItemIdentification/ID` is the priority-2 source for `BuyerItemCode`,
and `PartyLegalEntity/RegistrationName` is honored as the BIS-3-common buyer-name source
when `PartyName/Name` is absent. A `customizationId` capture hook is in place (commented)
for future BIS-only validations such as mandatory `EndpointID`.

## File-detection logic vs cXML

Both parsers claim `.xml`. The interface contract `CanParse(string fileExtension)`
cannot disambiguate by content. Disambiguation is provided by a new public static
helper `UblOrderParser.IsUblOrderDocument(Stream)` that peeks the root element +
namespace, restores stream position, and never throws — safe for the factory to call
as a probe. Until `OrderParserFactory` is upgraded to call it, registration order in
`Program.cs` is the disambiguator and both parsers will independently throw their own
`*ParseException` if asked to parse the other format.

## Test coverage

17 tests in `ProcuLink.Transform.Tests/Parsing/UblOrderParserTests.cs`. UBL-only run:
`dotnet test ... --filter "FullyQualifiedName~UblOrderParserTests"` → **17 passed**.
Full suite: `dotnet test ProcuLink.Transform.Tests/...` → **90 passed, 0 failed**
(73 prior + 17 UBL). Coverage: CanParse, UBL 2.1 happy-path (3 lines), Peppol BIS 3
variant, BuyerCustomerParty extraction, SellerSupplierParty non-clobber, LineItem
(buyer-id priority + seller-id fallback), currency (header + PriceAmount fallback),
malformed XML, non-Order root, wrong-namespace Order root, missing header ID, no
OrderLine, and three `IsUblOrderDocument` peek tests (UBL true, cXML false,
wrong-namespace false).

## Registration one-liner for `OrderParserFactory`

Add to `ProcuLink.Api/Program.cs` next to the existing parser registrations
(line ~177, immediately after `CxmlOrderParser`):

```csharp
builder.Services.AddSingleton<IPurchaseOrderParser, UblOrderParser>();
```

**Optional factory upgrade** (recommended for full `.xml` mutual exclusivity):
add a stream-aware overload to `OrderParserFactory` —
`GetParser(string ext, Stream peek)` — that calls
`UblOrderParser.IsUblOrderDocument(peek)` for `.xml` uploads and falls back to
`CxmlOrderParser` when false. The peek helper resets stream position, so the chosen
parser can re-read the stream cleanly.

## Known limitations + next-step recommendations

- **Stream-aware factory not built.** Mutual-exclusivity probe (`IsUblOrderDocument`)
  is present, but the factory still selects by extension only. Founder applies the
  factory upgrade and `Program.cs` registration.
- **No `PostalAddress` / `Contact` parsing.** Party identity is name-only today;
  enough for display + supplier-resolution heuristics, but full Peppol BIS 3
  participant-id matching (`EndpointID`, `PartyIdentification`) is deferred.
- **No `AllowanceCharge`, `TaxTotal`, `Delivery` line modifiers.** UBL allows these
  per-line; we ignore for now. ParsedOrderLine carries only `UnitPrice`, not
  net/gross variants. Add when supplier-side acknowledgement (UBL OrderResponse,
  Group K/L) lands.
- **Next builds (per `docs/format-channel-roadmap.md` priority #3 onward):**
  1. **UBL 2.1 Order *output* transformer** — `UblTransformService` emitting
     a buyer-side `Order` document. ~3 days. Required for "send to supplier via
     Peppol partner" flows.
  2. **UBL Invoice (`urn:oasis:names:specification:ubl:schema:xsd:Invoice-2`)** —
     same parsing pattern, larger canonical-model delta (tax breakdown, payment
     terms). Q4 2026 per roadmap.
  3. **UBL Despatch Advice (`...:DespatchAdvice-2`)** — different shape
     (package / pallet / SSCC hierarchy). Q1 2027 per roadmap.
  4. **Peppol access via Storecove partner** — once UBL output ships, integrate
     Storecove REST so customers can be Peppol-enabled without becoming an
     access point themselves.

## Files added (additive only; no other parsers, factory, DI, or csproj touched)

- `ProcuLink.Transform/Parsing/UblOrderParser.cs`
- `ProcuLink.Transform/Parsing/UblParseException.cs`
- `ProcuLink.Transform.Tests/Parsing/UblOrderParserTests.cs`
