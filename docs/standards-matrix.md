# ProcuLink — Standards Matrix

_Last updated 2026-05-28. Authoritative reference for which procurement
standards ProcuLink supports today, at what conformance level, over which
transport, and what is on the roadmap. Aligned with the Phase 6
international-standard direction
(`docs/superpowers/plans/2026-05-28-phase-6-international-standard-roadmap.md`)._

---

## How to read this file

The headline matrix below is the per-standard view: one row per
international or de-facto standard, with current ProcuLink coverage and a
reference link. Sections after the matrix break out format-by-format
implementation detail (parser class, transformer class, fixture, plan
gate) and the shared canonical PO model that all parsers populate.

Status values used throughout:

- **supported** — parser and/or transformer implemented and tested.
- **partial** — code path exists but not a dedicated parser / transformer
  class; field coverage may be limited.
- **planned (Horizon N)** — scheduled in the international-standard
  roadmap.
- **deferred** — known requirement; not yet on a Horizon.

---

## Headline standards matrix

| Standard | Version | ProcuLink: parse | ProcuLink: transform out | Typical transport | Conformance level today | Reference |
|---|---|---|---|---|---|---|
| **cXML** | 1.2.024 | supported (`CxmlOrderParser`) | supported (`CxmlTransformService`) | HTTPS POST (OrderRequest envelope), supplier portals | Header + line fidelity for `OrderRequest`; envelope round-trip; payloadID + timestamp emitted | [cxml.org](http://cxml.org/) · [Reference DTD](http://xml.cxml.org/current/cXML.dtd) |
| **UBL Order** | 2.1 (OASIS) | planned (Horizon 2 — Group M) | planned (Horizon 2 — Group M) | HTTPS / Peppol Access Point / SMTP attachment | — | [UBL 2.1 Order](http://docs.oasis-open.org/ubl/os-UBL-2.1/UBL-2.1.html) |
| **Peppol BIS Order** | 3.0 | planned (Horizon 2 — Group M, pairs with UBL) | planned (Horizon 2 — Group M) | Peppol Access Point (AS4) | — | [Peppol BIS Order 3.0 spec](https://docs.peppol.eu/poacc/upgrade-3/profiles/3-order/) |
| **EDIFACT ORDERS** | UN D.96A (and D.01B by request) | stub today; planned real parser (Horizon 2 — Group M) | planned (Horizon 2 — Group M) | AS2 (partner-wrap), SFTP, VAN | EdiFabric vs open-source library decision pending — see `docs/superpowers/specs/2026-Q4-edifact-library-evaluation.md` (to be written) | [UN/EDIFACT ORDERS D.96A](https://service.unece.org/trade/untdid/d96a/trmd/orders_c.htm) |
| **ANSI X12** | 850 (versions 004010, 005010) | supported (`X12OrderParser`) | supported (`X12TransformService`) | AS2 (partner-wrap), VAN, SFTP | Hand-rolled flat-segment parser/transformer (no commercial EDI library); ISA/GS/ST/BEG/CUR/N1/PO1/PID/CTT envelope; positional delimiter discovery from fixed-width ISA; transform↔parse round-trip tested | [X12 850 Purchase Order](https://x12.org/codes/transaction-sets) |
| **OpenPEPPOL transport (AS4)** | Peppol AS4 profile | n/a (transport, not document) | planned (Horizon 2 — Group N, partner-wrapped via Pagero / Tradeshift) | AS4 between Access Points | — | [Peppol AS4 profile](https://docs.peppol.eu/edelivery/as4/specification/) |
| **AS2 / AS4 (drummond)** | RFC 4130 (AS2), AS4 profile | n/a (transport) | planned (Horizon 2 — Group N, partner-wrap via mendelson / DragonAS2 first) | AS2 / AS4 | — | [RFC 4130](https://datatracker.ietf.org/doc/html/rfc4130) |
| **ISO 20022 — purchase-side reference** | 2013+ | reference-only (Horizon 2 — Group M) | reference-only | n/a | Mapping documented from canonical PO model to ISO 20022 procurement-relevant concepts. No transport in scope. | [ISO 20022](https://www.iso20022.org/) |
| **Internal canonical PO** | n/a | supported (`ParsedOrder` / `ParsedOrderLine`) | supported (CSV / XML / cXML / JSON-partial) | n/a (in-memory) | All implemented parsers populate this; all implemented transformers emit from it. | This repo — `ProcuLink.Core/Models/ParsedOrder.cs`, `docs/canonical-po-model.md` |
| **Supplier CSV (buyer-defined template)** | n/a | supported (`CsvOrderParser`) | supported (`CsvTransformService`) | HTTPS upload, SFTP, email attachment | Column alias matching; delimiter auto-detection (`,`/`;`); RFC 4180 escaping on output | n/a |
| **Supplier XLSX (buyer-defined template)** | n/a | supported (`XlsxOrderParser`) | n/a | HTTPS upload | ClosedXML; first worksheet only; header-row matching with multi-alias columns | n/a |
| **Supplier XML (generic envelope)** | n/a | n/a | supported (`XmlTransformService`) | HTTPS POST, SFTP, email attachment | Generic `<PurchaseOrder>` envelope — not cXML | n/a |
| **JSON / REST PO payload** | n/a | partial (inline in `OrderService` via `System.Text.Json`) | planned (Horizon 2 — Group M, dedicated `JsonTransformService`) | HTTPS POST (webhook), API ingress | Header + line via canonical JSON stored in `CanonicalJson`. No standalone `IPurchaseOrderParser` yet. | n/a |
| **Text-based PDF (PO layout)** | n/a | supported (`PdfOrderParser` via PdfPig 0.1.14) | n/a | HTTPS upload, email attachment | Regex header + line extraction; conservative parsing; non-scanned only | [PdfPig](https://github.com/UglyToad/PdfPig) |
| **Scanned PDF / OCR** | n/a | deferred | n/a | HTTPS upload, email attachment | Azure Document Intelligence config-gated stub exists (`AzureDocumentIntelligenceOcrService`); falls back to `NoOpOcrService` when key absent | [Azure Document Intelligence](https://learn.microsoft.com/azure/ai-services/document-intelligence/) |

### Headline reading guide

- "Supported" rows are the wedge today: cXML, the internal canonical model,
  CSV/XLSX input, CSV/XML output, text-PDF input.
- UBL / Peppol BIS Order / EDIFACT real parser / X12 850 are the Horizon 2
  Group M deliverables that turn ProcuLink from "good cXML tool" into
  "international standard router".
- AS2 / AS4 / PEPPOL transports are Horizon 2 Group N, partner-wrapped
  first.
- ISO 20022 is reference-only for Horizon 2 — documentation alignment, no
  transport.

---

## Format-by-format implementation detail

This is the implementation-level view, kept for engineers extending parsers
or transformers. Where the headline matrix lists "supported", the rows below
name the class and the test fixture.

| Format | Direction | Implementation class | Validation depth | Fixture | Plan gate | Notes |
|---|---|---|---|---|---|---|
| **cXML 1.2** | Input | `ProcuLink.Transform.Parsing.CxmlOrderParser` | Required fields: `orderID`, `deploymentMode`, ≥1 `ItemOut` with `SupplierPartID`/`Quantity`/`UnitPrice/Money` | `ProcuLink.Transform.Tests/Fixtures/sample-order.cxml` | Integration | Added Group K |
| **cXML 1.2** | Output | `ProcuLink.Transform.Output.CxmlTransformService` | No unresolved lines; emits cXML 1.2.024 with payloadID + timestamp | `ProcuLink.Transform.Tests/Fixtures/expected-output.cxml` | Integration | Added Group K |
| **UBL 2.1 / Peppol BIS Order 3** | Input | planned (`UblOrderParser` per `c395b6c` wires a real parser; coverage to be expanded in Horizon 2 Group M) | — | — | Integration | Namespace `urn:oasis:names:specification:ubl:schema:xsd:Order-2` |
| **UBL 2.1 / Peppol BIS Order 3** | Output | planned (Horizon 2 — Group M) | — | — | Integration | Emit `Order` document; mandatory `ID`, `IssueDate`, `OrderLine/LineItem` |
| **EDIFACT ORDERS D.96A** | Input | stub (`EdifactOrderParser` — real parsing per `2bd4ecd`; full ORDERS coverage in Horizon 2 Group M after library decision) | — | — | Integration | Library decision: EdiFabric (commercial) vs open-source. Segment delimiter `'`; `UNA`+`UNB`+`ORDERS`. |
| **EDIFACT ORDERS D.96A** | Output | planned (Horizon 2 — Group M) | — | — | Integration | Same library decision as input |
| **ANSI X12 850** | Input | `ProcuLink.Transform.Parsing.X12OrderParser` | Requires ISA envelope + `ST*850` + `BEG`; PO1 item-id qualifier pairs (`BP`/`IN` buyer, `VP`/`VN` vendor); optional/unknown segments skipped (never throws) | inline in `ProcuLink.Transform.Tests/Parsing/X12OrderParserTests.cs` | Integration | Hand-rolled; delimiters discovered positionally from fixed-width ISA, `*`/`>`/`~` fallback |
| **ANSI X12 850** | Output | `ProcuLink.Transform.Output.X12TransformService` | No unresolved lines; emits 004010 ISA…IEA with balanced control numbers + computed SE/CTT counts | inline in `ProcuLink.Transform.Tests/Output/X12TransformServiceTests.cs` | Integration | ContentType `application/edi-x12`, extension `.x12`; round-trips through `X12OrderParser` |
| **CSV (buyer template)** | Input | `ProcuLink.Transform.Parsing.CsvOrderParser` | Column alias matching; delimiter auto-detection (`,`/`;`) | inline in `ProcuLink.Transform.Tests/Parsing/` | Growth | Comma and semicolon delimited |
| **XLSX (buyer template)** | Input | `ProcuLink.Transform.Parsing.XlsxOrderParser` | Header-row matching; empty-row skip; multi-alias columns | inline in `ProcuLink.Transform.Tests/Parsing/` | Growth | ClosedXML; first worksheet only |
| **Supplier CSV output template** | Output | `ProcuLink.Transform.Output.CsvTransformService` | No unresolved lines; RFC 4180 escaping | — | Growth | Columns: SupplierItemCode, Description, Quantity, Unit, UnitPrice, LineTotal |
| **Supplier XML output template** | Output | `ProcuLink.Transform.Output.XmlTransformService` | No unresolved lines; generic `<PurchaseOrder>` envelope | — | Operations | Not cXML — plain `<PurchaseOrder>` |
| **JSON / API payload** | Input | partial (`OrderService` inline `System.Text.Json`) | Header + line fields from canonical JSON in `CanonicalJson`; no dedicated parser class | — | Growth | No standalone `IPurchaseOrderParser` |
| **JSON / API payload** | Output | planned (Horizon 2 — Group M, `JsonTransformService` + `OutputFormat.Json`) | — | — | Growth | Emit canonical JSON as API response artifact |
| **Text-based PDF** | Input | `ProcuLink.Transform.Parsing.PdfOrderParser` (PdfPig 0.1.14) | Regex header + line extraction; non-scanned only | `ProcuLink.Transform.Tests/Parsing/PdfOrderParserTests.cs` (inline) | Operations | Conservative parsing |
| **Scanned PDF / OCR** | Input | config-gated stub (`AzureDocumentIntelligenceOcrService` → `NoOpOcrService` fallback) | — | — | Integration | Real OCR enabled when `Ocr:Azure:Endpoint` set |

### Invoice + ASN (Wave 3, parallel to PO standards)

| Format | Direction | Implementation class | Status | Notes |
|---|---|---|---|---|
| **UBL Invoice 2.1** | Input | `UblInvoiceParser` | supported | Full UBL 2.1 invoice parse; `IsUblInvoiceDocument` peek helper |
| **EDIFACT INVOIC** | Input | `EdifactInvoiceParser` | stub | EdiFabric licence required; drop-in ready |
| **EDIFACT DESADV** | Input | `EdifactDesadvParser` | stub | Same library dependency |
| **CSV / XML / JSON invoice** | Output | `CsvInvoiceTransformService` / `XmlInvoiceTransformService` / `JsonInvoiceTransformService` | supported | Reuses canonical invoice model |
| **UBL Invoice 2.1 output** | Output | planned (Horizon 3 — Group S) | — | Required for P2P loop closure |
| **Peppol BIS Invoice 3.0** | Output | planned (Horizon 3 — Group S) | — | Same dependency as UBL Order output |

---

## Canonical PO Model fields

All parsers populate `ParsedOrder` / `ParsedOrderLine`, which then persist to
`PurchaseOrderEntity` / `PurchaseOrderLineEntity`. This is the field-level
contract every standards parser must satisfy.

### Header (`ParsedOrder`)

| Field | C# Type | Required | Business Rule | UBL ref | EDIFACT ref | X12 850 ref | cXML ref |
|---|---|---|---|---|---|---|---|
| `PoNumber` | `string?` | recommended | Max 50 chars; null allowed | `cbc:ID` | `BGM 1004` | `BEG03` | `OrderRequestHeader/@orderID` |
| `OrderDate` | `DateTime?` | recommended | Parsers attempt ISO 8601, `dd/MM/yyyy`, `MM/dd/yyyy`, `d.M.yyyy` | `cbc:IssueDate` | `DTM C507/2380` | `BEG05` | `OrderRequestHeader/@orderDate` |
| `BuyerName` | `string?` | optional | Free text; display + audit | `cac:BuyerCustomerParty/cac:Party/cac:PartyName/cbc:Name` | `NAD BY` | `N1*BY` | `OrderRequestHeader/Contact[@role='buyer']/Name` |
| `Currency` | `string?` | recommended | ISO 4217; uppercased on persist | `cbc:DocumentCurrencyCode` | `CUX C504/6347` | `CUR02` | `OrderRequestHeader/Total/Money/@currency` |
| `Lines` | `IReadOnlyList<ParsedOrderLine>` | yes | At least one line expected | `cac:OrderLine` | `LIN` | `PO1` | `ItemOut` |

### Line (`ParsedOrderLine`)

| Field | C# Type | Required | Business Rule | UBL ref | EDIFACT ref | X12 850 ref | cXML ref |
|---|---|---|---|---|---|---|---|
| `LineNumber` | `int` | yes | Auto-increment from 1 if not present | `cbc:ID` | `LIN 1082` | `PO101` | `ItemOut/@lineNumber` |
| `BuyerItemCode` | `string` | yes | Buyer's own code; lookup key in `item_mappings`; never overwritten by supplier code | `cac:Item/cac:BuyersItemIdentification/cbc:ID` | `LIN C212 (IN)` | `PO107/PO109` (buyer-qualified) | `ItemOut/ItemID/BuyerPartID` |
| `Description` | `string?` | optional | Free text | `cac:Item/cbc:Description` | `IMD C273/7008` | `PID05` | `ItemOut/ItemDetail/Description` |
| `Quantity` | `decimal` | yes | Defaults to `0` if unparseable; culture-invariant | `cbc:Quantity` | `QTY C186/6060` | `PO102` | `ItemOut/@quantity` |
| `Unit` | `string?` | optional | `EA`, `PCS`, `KG`, … | `cbc:Quantity/@unitCode` | `QTY C186/6411` | `PO103` | `ItemOut/UnitOfMeasure` |
| `UnitPrice` | `decimal?` | recommended | Required for CSV / XML / cXML transforms | `cac:Price/cbc:PriceAmount` | `PRI C509/5118` | `PO104` | `ItemOut/UnitPrice/Money` |

### EF entity extensions (resolved after mapping)

| Field | C# Type | Required | Business Rule |
|---|---|---|---|
| `SupplierItemCode` | `string?` | yes (before transform) | Resolved from `item_mappings`; null triggers `NeedsReview = true` |
| `NeedsReview` | `bool` | yes | True when supplier code could not be resolved deterministically |
| `Confidence` | `float` | yes | 1.0 = certain (deterministic lookup); lower = AI suggestion |
| `AiSuggestedSupplierItemCode` | `string?` | optional | Set when AI suggests a mapping; cleared on manual resolution |
| `AiSuggestionConfidence` | `float?` | optional | 0.0–1.0 |
| `AiSuggestionReason` | `string?` | optional | Human-readable reason from AI provider |
| `AiSuggestionProvenance` | `string?` | optional | Model ID / provider name |

The standards-reference columns above are the contract for the
"Standards-visibility rule" in `CLAUDE.md` — every field in a transform
or mapping context must be able to surface these labels on demand
(info popover, per-screen disclosure, or Command Palette entry). Not
gated behind a user-mode toggle.

---

## Plan gates

| Plan | Included formats |
|---|---|
| **Pilot** (internal/free, 14 days) | CSV input, XLSX input, Supplier CSV output |
| **Growth** (€149/mo) | + PDF input, Supplier XML output, JSON/API output (when shipped) |
| **Operations** (€399/mo) | All Growth formats |
| **Integration** (€999/mo) | + cXML input/output, UBL/Peppol (when implemented), EDIFACT (when implemented), X12 850 (when implemented), OCR/scanned PDF (when implemented) |
| **Enterprise** | All formats + custom supplier rules and ERP connectors |

Gate enforcement: `BillingFeature.Cxml` is defined in
`ProcuLink.Core/Constants/BillingFeature.cs`. UBL/Peppol, EDIFACT, X12, and
OCR gates will use new `BillingFeature` values when those parsers ship.

---

## Implementation priorities (linked to Horizons)

Horizon 1 (now): no new standards work — focus is reliability + onboarding.

Horizon 2 — Group M priorities, in order:

1. **JSON / REST PO output** — `JsonTransformService` + `OutputFormat.Json`.
   Low effort; unblocks webhook delivery of canonical JSON to suppliers
   running their own ERP webhook receivers.
2. **UBL 2.1 / Peppol BIS Order 3 input** — high demand in EU procurement.
   Expand `UblOrderParser` to full BIS 3.0 conformance.
3. **UBL 2.1 / Peppol BIS Order 3 output** — pairs with UBL input; required
   for Peppol network delivery (via partner-wrapped Access Point in
   Group N).
4. **EDIFACT library decision** — write
   `docs/superpowers/specs/2026-Q4-edifact-library-evaluation.md`; pick
   EdiFabric vs open-source; then build the real parser + transformer.
5. **ANSI X12 850 input + output** — likely shares the EDIFACT library
   decision.
6. **In-app standards comparison screen** (`/standards`) — expert-mode
   view of "this canonical field in UBL / EDIFACT / X12 / cXML / Peppol
   BIS" with a live example pulled from a real order.

Horizon 3 — Group S priorities (P2P loop closure):

7. **UBL Invoice 2.1 output + Peppol BIS Invoice 3.0** — natural follow-on
   from the Wave 3 inbound invoice model.
8. **OCR / scanned PDF** — Azure Document Intelligence behind
   `BillingFeature.Ocr`; required for customers without structured PO files.

Order changes if pilot demand reorders them.

---

## References

- Phase 6 roadmap:
  `docs/superpowers/plans/2026-05-28-phase-6-international-standard-roadmap.md`
- Positioning:
  `docs/strategy/international-standard-thesis.md`
- Canonical PO model:
  `docs/canonical-po-model.md`
- Format / channel ground truth:
  `docs/format-channel-roadmap.md`
