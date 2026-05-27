# ProcuLink — Standards Matrix (Group K)

_Last updated: 2026-05-27. This file is the authoritative reference for input/output format support levels,
plan gates, and implementation priorities._

---

## Format Support Table

| Format | Direction | Support Level | Parser / Transform Class | Validation Depth | Fixture File | Plan Gate | Notes |
|---|---|---|---|---|---|---|---|
| **cXML 1.2** | Input | supported | `CxmlOrderParser` | Required fields: `orderID`, `deploymentMode`, ≥1 `ItemOut` with `SupplierPartID`/`Quantity`/`UnitPrice/Money` | `ProcuLink.Transform.Tests/Fixtures/sample-order.cxml` | Integration | Added Group K |
| **cXML 1.2** | Output | supported | `CxmlTransformService` | Validates no unresolved lines; emits cXML 1.2.024 with payloadID + timestamp | `ProcuLink.Transform.Tests/Fixtures/expected-output.cxml` | Integration | Added Group K |
| **UBL 2.1 / Peppol BIS Order 3** | Input | planned | — | — | — | Integration | Requires `NuGet: UBL.NET` or hand-rolled XSD; namespace `urn:oasis:names:specification:ubl:schema:xsd:Order-2` |
| **UBL 2.1 / Peppol BIS Order 3** | Output | planned | — | — | — | Integration | Emit `Order` document; mandatory `ID`, `IssueDate`, `OrderLine/LineItem` |
| **CSV (buyer template)** | Input | supported | `CsvOrderParser` | Column alias matching; delimiter auto-detection (`,`/`;`) | `ProcuLink.Transform.Tests/Fixtures/` (inline in tests) | Growth | Comma and semicolon delimited |
| **XLSX (buyer template)** | Input | supported | `XlsxOrderParser` | Header-row matching; empty-row skip; multi-alias columns | `ProcuLink.Transform.Tests/Fixtures/` (inline in tests) | Growth | ClosedXML; first worksheet only |
| **Supplier CSV output template** | Output | supported | `CsvTransformService` | Validates no unresolved lines; RFC 4180 escaping | — | Growth | Columns: SupplierItemCode, Description, Quantity, Unit, UnitPrice, LineTotal |
| **Supplier XML output template** | Output | supported | `XmlTransformService` | Validates no unresolved lines; generic `PurchaseOrder` schema | — | Operations | Not cXML — plain `<PurchaseOrder>` envelope |
| **JSON / API payload** | Input | partial | `OrderService` (inline) | Header + line fields from canonical JSON in `CanonicalJson`; no dedicated parser class | — | Growth | Parsed via `System.Text.Json` in `OrderService`; no standalone `IPurchaseOrderParser` |
| **JSON / API payload** | Output | planned | — | — | — | Growth | Planned: emit canonical JSON as API response artifact |
| **EDI X12 850** | Input | planned/deferred | — | — | — | Integration | Requires `EdiFabric` or `EdiWeave` commercial lib or hand-rolled tokenizer; high effort |
| **EDI X12 850** | Output | planned/deferred | — | — | — | Integration | Same library requirement; deferred post-Integration tier launch |
| **EDIFACT ORDERS D96A** | Input | planned/deferred | — | — | — | Integration | Similar to X12; segment delimiter `'`; `UNA`+`UNB`+`ORDERS`; deferred |
| **EDIFACT ORDERS D96A** | Output | planned/deferred | — | — | — | Integration | Deferred |
| **Text-based PDF** | Input | supported | `PdfOrderParser` | Regex header + line extraction via PdfPig; scanned/OCR PDFs explicitly out of scope | `ProcuLink.Transform.Tests/Parsing/PdfOrderParserTests.cs` (inline) | Operations | PdfPig 0.1.14; conservative parsing |
| **Scanned PDF / OCR** | Input | deferred | — | — | — | Integration | Requires Tesseract or Azure Document Intelligence; high integration cost |

---

## Canonical PO Model Fields

These are the fields that all parsers must populate into `ParsedOrder` / `ParsedOrderLine`,
which are then persisted to the EF entities `PurchaseOrderEntity` / `PurchaseOrderLineEntity`.

### Header fields (`ParsedOrder`)

| Field | C# Type | Required | Business Rule |
|---|---|---|---|
| `PoNumber` | `string?` | recommended | Max 50 chars; null allowed from parsers that cannot extract it |
| `OrderDate` | `DateTime?` | recommended | Parsers attempt multiple date formats (ISO 8601, `dd/MM/yyyy`, `MM/dd/yyyy`, `d.M.yyyy`) |
| `BuyerName` | `string?` | optional | Free text; used for display and audit only |
| `Currency` | `string?` | recommended | ISO 4217 three-letter code (e.g. `EUR`, `USD`); uppercased on persist |
| `Lines` | `IReadOnlyList<ParsedOrderLine>` | yes | At least one line expected; empty list allowed but triggers warning |

### Line fields (`ParsedOrderLine`)

| Field | C# Type | Required | Business Rule |
|---|---|---|---|
| `LineNumber` | `int` | yes | Auto-incremented from 1 if not present in source file |
| `BuyerItemCode` | `string` | yes | The buyer's own item code; used as lookup key in `item_mappings`; never overwritten by supplier code |
| `Description` | `string?` | optional | Free text description from the source file |
| `Quantity` | `decimal` | yes | Defaults to `0` if unparseable; culture-invariant parsing |
| `Unit` | `string?` | optional | Unit of measure (e.g. `EA`, `PCS`, `KG`) |
| `UnitPrice` | `decimal?` | recommended | Null if not present in source file; required for `CsvTransformService`/`XmlTransformService`/`CxmlTransformService` output |

### EF entity extensions (`PurchaseOrderLineEntity`, resolved after mapping)

| Field | C# Type | Required | Business Rule |
|---|---|---|---|
| `SupplierItemCode` | `string?` | yes (before transform) | Resolved from `item_mappings`; null triggers `NeedsReview = true` |
| `NeedsReview` | `bool` | yes | True when supplier code could not be resolved deterministically |
| `Confidence` | `float` | yes | 1.0 = certain (deterministic lookup); lower = AI suggestion |
| `AiSuggestedSupplierItemCode` | `string?` | optional | Set when AI suggests a mapping; cleared on manual resolution |
| `AiSuggestionConfidence` | `float?` | optional | 0.0–1.0 |
| `AiSuggestionReason` | `string?` | optional | Human-readable reason from AI provider |
| `AiSuggestionProvenance` | `string?` | optional | Model ID / provider name |

---

## Plan Gates

| Plan | Included formats |
|---|---|
| **Pilot** (internal/free, 14 days) | CSV input, XLSX input, Supplier CSV output |
| **Growth** (€149/mo) | + PDF input, Supplier XML output, JSON/API output |
| **Operations** (€399/mo) | All Growth formats |
| **Integration** (€999/mo) | + cXML input/output, UBL/Peppol (when implemented), EDI X12 850 (when implemented), EDIFACT (when implemented), OCR/scanned PDF (when implemented) |
| **Enterprise** | All formats + custom supplier rules and ERP connectors |

Gate enforcement: `BillingFeature.Cxml` is already defined in `ProcuLink.Core/Constants/BillingFeature.cs`.
UBL/Peppol, EDI, and OCR gates will use new `BillingFeature` enum values when those parsers are built.

---

## Next Implementation Priorities

1. **JSON/API payload output** — add a dedicated `JsonTransformService` and `OutputFormat.Json`; low effort; unblocks webhook delivery of canonical JSON to suppliers.
2. **UBL 2.1 / Peppol BIS Order 3 input** — high demand in EU procurement; evaluate `UBL.NET` NuGet package vs hand-rolled XSD deserialization.
3. **UBL 2.1 / Peppol BIS Order 3 output** — pairs with UBL input; required for Peppol network delivery.
4. **EDI X12 850 input** — US market; evaluate `EdiFabric` (commercial) vs open-source tokenizer.
5. **EDIFACT ORDERS input** — European EDI; can share library with X12 if `EdiFabric` is chosen.
6. **OCR / scanned PDF input** — integrate Azure Document Intelligence or Tesseract behind `BillingFeature` gate; required for customers without structured PO files.
7. **EDI output formats** — only after input is validated end-to-end with at least one production buyer.
