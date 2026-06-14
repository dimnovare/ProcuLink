# Phase 1 — Lossless capture + widened extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the inbound pipeline dropping every field the canonical model has no slot for — widen LLM + email extraction to capture parties / VAT / contact / manufacturer-PN / Incoterms, persist the FULL source-token set for structured formats, and store everything in first-class columns + an `OrderParty` table + a lossless `SourceCapture` table, verified on real Postgres.

**Architecture:** The PDF path is `OpenAiPdfOrderExtractor → ExtractedOrder (Core) → MapExtractedToParsed → ParsedOrder (Transform) → PurchaseOrderEntity/Lines (ingest)`. We widen each layer additively (defaulted params, nullable columns) so nothing existing breaks, add an `OrderParty` child table for the addresses and a `SourceCapture` table for the raw bag, and wire both at the two ingest sites (sync `CreateStubFromParsedOrderAsync`, async `ParseStoredFileAsync`). Phases 2-4 (engine / extensible canonical / UI) are out of scope.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + Testcontainers.PostgreSql, OpenAI structured outputs. No commercial EDI licences. Spec: `docs/superpowers/specs/2026-06-13-flexible-mapping-design.md` (Phase 1).

---

## File structure

**Create:**
- `ProcuLink.Transform/Parsing/ParsedParty.cs` — value record for an address/role party on a parsed order.
- `ProcuLink.Core/Entities/OrderParty.cs` — EF entity, `order_parties` table.
- `ProcuLink.Core/Entities/SourceCapture.cs` — EF entity, `source_captures` table (the lossless raw bag).
- `ProcuLink.Infrastructure.Tests/Ai/PdfExtractionWideningTests.cs` — ValidateAndMap unit tests for the new fields + raw_fields.
- `ProcuLink.Api.Tests/Integration/LosslessCapturePersistencePostgresTests.cs` — real-Postgres round-trip for parties + line columns + SourceCapture.
- `ProcuLink.Api.Tests/Integration/SourceTokenPersistencePostgresTests.cs` — real-Postgres round-trip for the full structured-format token set.

**Modify:**
- `ProcuLink.Transform/Parsing/ParsedOrder.cs` / `ParsedOrderLine.cs` — additive fields.
- `ProcuLink.Core/Services/IOrderService.cs` — widen `ExtractedOrder` / `ExtractedOrderLine` (+ new `ExtractedParty`).
- `ProcuLink.Infrastructure/Services/Ai/OpenAiPdfOrderExtractor.cs` — schema, SystemPrompt, DTOs, ValidateAndMap.
- `ProcuLink.Infrastructure/Services/Ai/OpenAiEmailBodyOrderExtractor.cs` — schema, DTOs, map (parity subset).
- `ProcuLink.Core/Entities/PurchaseOrderEntity.cs` / `PurchaseOrderLineEntity.cs` — new nullable columns + `List<OrderParty>` nav.
- `ProcuLink.Core/Services/ParsedFileOutput.cs` — add `IReadOnlyList<SourceToken>? Tokens`.
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — config for new columns/entities + `DbSet`s.
- `ProcuLink.Api/Services/Orders/OrderIngestionService.cs` — `MapExtractedToParsed`, the two header sites, `BuildLineEntitiesAsync`, party + SourceCapture inserts.
- InMemory test contexts that bulk-`Ignore` entities (e.g. `AiCalibrationControllerTests.cs`) — add `Ignore<OrderParty>()` + `Ignore<SourceCapture>()`.

---

### Task 1: Widen the parsed + extracted model records (additive)

**Files:**
- Create: `ProcuLink.Transform/Parsing/ParsedParty.cs`
- Modify: `ProcuLink.Transform/Parsing/ParsedOrder.cs:7-24`, `ProcuLink.Transform/Parsing/ParsedOrderLine.cs:8-31`
- Modify: `ProcuLink.Core/Services/IOrderService.cs` (the `ExtractedOrder` / `ExtractedOrderLine` records)

- [ ] **Step 1: Create the ParsedParty record**

```csharp
namespace ProcuLink.Transform.Parsing;

/// <summary>
/// A named party on a parsed order (ship-to, bill-to, remit-to, buyer, supplier).
/// Additive Phase-1 lossless capture: addresses + tax/EDI ids that the fixed canonical
/// header never had a slot for. Role is a lowercase tag: "shipTo" | "billTo" | "remitTo"
/// | "buyer" | "supplier". Every field is nullable — a document may carry only some.
/// </summary>
public record ParsedParty(
    string Role,
    string? Name = null,
    string? Street = null,
    string? City = null,
    string? PostalCode = null,
    string? Country = null,
    string? Vat = null,
    string? RegNr = null,
    string? EdiCode = null,
    string? Reference = null,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null);
```

- [ ] **Step 2: Add additive fields to ParsedOrder**

Append these parameters to the `ParsedOrder` record (after `RequestedDeliveryDate`, all defaulted so every existing `new ParsedOrder(...)` call keeps compiling):

```csharp
    // Phase 1 lossless capture (additive, defaulted). Parties carry addresses/VAT/EDI ids;
    // contact + header terms had no canonical slot before and were dropped at the door.
    IReadOnlyList<ParsedParty>? Parties = null,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Incoterms = null,
    string? ShippingMethod = null,
    string? BuyerOrderRef = null
```

- [ ] **Step 3: Add additive fields to ParsedOrderLine**

Append to the `ParsedOrderLine` record (after `ReviewReason`, all defaulted):

```csharp
    // Phase 1 lossless capture (additive, defaulted). ManufacturerPartNumber is the real
    // product identifier on every vendor AND the catalog lookup key (Phase 2).
    string? ManufacturerPartNumber = null,
    string? CustomerPartNumber = null,
    decimal? DiscountPercent = null,
    string? Unspsc = null,
    string? Recipient = null,
    string? ContractNumber = null,
    decimal? NetAmount = null
```

- [ ] **Step 4: Mirror the additive fields on ExtractedOrder / ExtractedOrderLine**

In `ProcuLink.Core/Services/IOrderService.cs`, add an `ExtractedParty` record next to `ExtractedOrder`:

```csharp
/// <summary>Phase 1: a named party (address/role) on an LLM-extracted order. Mirrors
/// <c>ParsedParty</c> in the Transform layer (Core cannot reference Transform).</summary>
public sealed record ExtractedParty(
    string Role,
    string? Name = null,
    string? Street = null,
    string? City = null,
    string? PostalCode = null,
    string? Country = null,
    string? Vat = null,
    string? RegNr = null,
    string? EdiCode = null,
    string? Reference = null,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null);
```

Append to `ExtractedOrder` (after its existing last parameter — keep all defaulted):

```csharp
    , IReadOnlyList<ExtractedParty>? Parties = null
    , string? ContactName = null
    , string? ContactEmail = null
    , string? ContactPhone = null
    , string? Incoterms = null
    , string? ShippingMethod = null
    , string? BuyerOrderRef = null
```

Append to `ExtractedOrderLine` (after its existing last parameter):

```csharp
    , string? ManufacturerPartNumber = null
    , string? CustomerPartNumber = null
    , decimal? DiscountPercent = null
    , string? Unspsc = null
    , string? Recipient = null
    , string? ContractNumber = null
    , decimal? NetAmount = null
```

- [ ] **Step 5: Build to verify additive change compiles**

Run: `dotnet build ProcuLink.slnx --no-restore`
Expected: PASS (no existing call site breaks — every new field is defaulted).

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Transform/Parsing/ParsedParty.cs ProcuLink.Transform/Parsing/ParsedOrder.cs ProcuLink.Transform/Parsing/ParsedOrderLine.cs ProcuLink.Core/Services/IOrderService.cs
git commit -m "feat(canonical): additive parties/contact/line fields on parsed+extracted models"
```

---

### Task 2: Propagate the new fields through MapExtractedToParsed

**Files:**
- Modify: `ProcuLink.Api/Services/Orders/OrderIngestionService.cs:895-918`
- Test: `ProcuLink.Infrastructure.Tests/Ai/PdfExtractionWideningTests.cs` (created here)

- [ ] **Step 1: Write the failing test**

```csharp
using ProcuLink.Core.Services;            // ExtractedOrder / ExtractedParty
using ProcuLink.Api.Services.Orders;      // OrderIngestionService (MapExtractedToParsed is private — test via the public mapping seam below)
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Ai;

public class MapExtractedToParsedTests
{
    [Fact]
    public void Map_propagates_parties_contact_and_line_mpn()
    {
        var extracted = new ExtractedOrder(
            "PO1", new DateTime(2026, 6, 12), "Acme Buyer", "EUR",
            new[] { new ExtractedOrderLine(1, "B1", "Widget", 2m, "PC", 5m, 10m, 0m, null,
                ManufacturerPartNumber: "MPN-9", Recipient: "redacted@example.invalid") },
            Parties: new[] { new ExtractedParty("shipTo", Name: "Acme DC", City: "Linz", Vat: "ATU1") },
            ContactEmail: "redacted@example.invalid", Incoterms: "DDP");

        var parsed = OrderIngestionService.MapExtractedToParsedForTest(extracted);

        Assert.Equal("DDP", parsed.Incoterms);
        Assert.Equal("redacted@example.invalid", parsed.ContactEmail);
        Assert.Single(parsed.Parties!);
        Assert.Equal("ATU1", parsed.Parties![0].Vat);
        Assert.Equal("MPN-9", parsed.Lines[0].ManufacturerPartNumber);
        Assert.Equal("redacted@example.invalid", parsed.Lines[0].Recipient);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~MapExtractedToParsedTests"`
Expected: FAIL — `MapExtractedToParsedForTest` does not exist; parsed fields are null.

- [ ] **Step 3: Widen MapExtractedToParsed and expose a test seam**

Replace `OrderIngestionService.cs:895-918` with (adds party + contact + header + line propagation; the new `internal static` wrapper exposes the private mapper to tests):

```csharp
    internal static ParsedOrder MapExtractedToParsedForTest(ExtractedOrder o) => MapExtractedToParsed(o);

    private static ParsedOrder MapExtractedToParsed(ExtractedOrder o) =>
        new(
            o.PoNumber,
            o.OrderDate,
            o.BuyerName,
            o.Currency,
            o.Lines.Select(l => new ParsedOrderLine(
                l.LineNumber,
                l.BuyerItemCode,
                l.Description,
                l.Quantity,
                l.Unit,
                l.UnitPrice,
                LineAmount: l.LineAmount,
                TaxRate: l.TaxRate,
                DeliveryDate: l.DeliveryDate,
                ManufacturerPartNumber: l.ManufacturerPartNumber,
                CustomerPartNumber: l.CustomerPartNumber,
                DiscountPercent: l.DiscountPercent,
                Unspsc: l.Unspsc,
                Recipient: l.Recipient,
                ContractNumber: l.ContractNumber,
                NetAmount: l.NetAmount)).ToList(),
            SupplierName: o.SupplierName,
            SubTotal: o.SubTotal,
            TaxTotal: o.TaxTotal,
            GrandTotal: o.GrandTotal,
            PaymentTerms: o.PaymentTerms,
            DocumentType: o.DocumentType,
            RequestedDeliveryDate: o.RequestedDeliveryDate,
            // Phase 1 lossless capture.
            Parties: o.Parties?.Select(p => new ParsedParty(
                p.Role, p.Name, p.Street, p.City, p.PostalCode, p.Country, p.Vat,
                p.RegNr, p.EdiCode, p.Reference, p.ContactName, p.Email, p.Phone)).ToList(),
            ContactName: o.ContactName,
            ContactEmail: o.ContactEmail,
            ContactPhone: o.ContactPhone,
            Incoterms: o.Incoterms,
            ShippingMethod: o.ShippingMethod,
            BuyerOrderRef: o.BuyerOrderRef);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~MapExtractedToParsedTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Services/Orders/OrderIngestionService.cs ProcuLink.Infrastructure.Tests/Ai/PdfExtractionWideningTests.cs
git commit -m "feat(ingest): propagate parties/contact/line fields through MapExtractedToParsed"
```

---

### Task 3: Widen the PDF extraction schema, DTOs, SystemPrompt, and ValidateAndMap

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/Ai/OpenAiPdfOrderExtractor.cs` (schema 54-92, prompt 96-120, DTOs 754-777, ValidateAndMap 425-536)
- Test: `ProcuLink.Infrastructure.Tests/Ai/PdfExtractionWideningTests.cs`

- [ ] **Step 1: Write the failing test (ValidateAndMap surfaces the new fields + raw_fields)**

Add to `PdfExtractionWideningTests.cs`:

```csharp
using ProcuLink.Infrastructure.Services.Ai;   // OpenAiPdfOrderExtractor (ValidateAndMap, ExtractionDto are internal — see InternalsVisibleTo note)

public class ValidateAndMapWideningTests
{
    [Fact]
    public void ValidateAndMap_emits_parties_contact_and_line_mpn_and_raw_fields()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.95,
            PoNumber: "4730154181",
            OrderDate: "2026-06-12",
            Currency: "EUR",
            BuyerName: "REDACTED-PARTY",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "00010", Description: "Panasonic",
                    Quantity: 1, Unit: "ST", UnitPrice: 306.28, LineAmount: 306.28,
                    ManufacturerPartNumber: "SCPMX94EGK", Recipient: "redacted@example.invalid")
            },
            SupplierName: "REDACTED-PARTY",
            Parties: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionPartyDto(
                    Role: "shipTo", Name: "REDACTED-PARTY", City: "Linz", Vat: "REDACTED-TAXID")
            },
            ContactEmail: "redacted@example.invalid",
            Incoterms: "DDP",
            RawFields: new[] { new OpenAiPdfOrderExtractor.RawFieldDto("EDI id", "REDACTED-TAXID") });

        // sourceText contains every emitted number so anti-hallucination passes.
        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "REDACTED-DOCNO");

        Assert.True(result.Success);
        Assert.Equal("DDP", result.Order!.Incoterms);
        Assert.Equal("redacted@example.invalid", result.Order.ContactEmail);
        Assert.Equal("REDACTED-TAXID", result.Order.Parties!.Single(p => p.Role == "shipTo").Vat);
        Assert.Equal("SCPMX94EGK", result.Order.Lines[0].ManufacturerPartNumber);
        Assert.Contains(result.Order.RawFields!, f => f.Label == "EDI id" && f.Value == "REDACTED-TAXID");
    }
}
```

> If `ExtractionDto`/`ValidateAndMap` are `internal`, ensure `ProcuLink.Infrastructure.csproj` has
> `<InternalsVisibleTo Include="ProcuLink.Infrastructure.Tests" />` (grep the csproj; it is already present for the existing internal-extractor tests — confirm before adding).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidateAndMapWideningTests"`
Expected: FAIL — `ExtractionPartyDto` / `RawFieldDto` / the new DTO params do not exist.

- [ ] **Step 3: Widen the strict JSON schema**

Replace the `ExtractionJsonSchema` body (`OpenAiPdfOrderExtractor.cs:54-92`) with the version below. Strict mode requires every property listed in `required`; new objects/arrays are required with empty/0 when the document is silent (the prompt instructs this). Add, at header level: `contact`, `parties`, `incoterms`, `shipping_method`, `buyer_order_ref`, `raw_fields`; per line: `manufacturer_part_number`, `customer_part_number`, `discount_percent`, `unspsc`, `recipient`, `contract_number`, `net_amount`.

```csharp
    private static readonly BinaryData ExtractionJsonSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "confidence":    { "type": "number" },
            "document_type": { "type": "string", "enum": ["purchase_order", "invoice", "other"] },
            "po_number":     { "type": "string" },
            "order_date":    { "type": "string" },
            "currency":      { "type": "string" },
            "buyer_name":    { "type": "string", "description": "The organisation that ISSUED/PLACED the order. On an invoice instead the bill-to customer. Assign from document labels, never from which name is familiar." },
            "supplier_name": { "type": "string", "description": "The party the order is ADDRESSED TO that will fulfil it. On an invoice instead the issuing seller. Must differ from buyer_name." },
            "payment_terms": { "type": "string" },
            "incoterms":     { "type": "string", "description": "Delivery/freight terms, e.g. DDP, EXW, DAP, FCA. Empty if none stated." },
            "shipping_method": { "type": "string" },
            "buyer_order_ref": { "type": "string", "description": "The buyer's own requisition / internal order reference, distinct from po_number. Empty if none." },
            "contact": {
              "type": "object",
              "properties": {
                "name":  { "type": "string" },
                "email": { "type": "string" },
                "phone": { "type": "string" }
              },
              "required": ["name", "email", "phone"],
              "additionalProperties": false
            },
            "parties": {
              "type": "array",
              "description": "Every named party with an address or tax id: ship-to, bill-to, remit-to. Empty array if none. Do NOT duplicate buyer_name/supplier_name unless they carry an address/VAT here.",
              "items": {
                "type": "object",
                "properties": {
                  "role":        { "type": "string", "enum": ["shipTo", "billTo", "remitTo"] },
                  "name":        { "type": "string" },
                  "street":      { "type": "string" },
                  "city":        { "type": "string" },
                  "postal_code": { "type": "string" },
                  "country":     { "type": "string" },
                  "vat":         { "type": "string" },
                  "reference":   { "type": "string" }
                },
                "required": ["role", "name", "street", "city", "postal_code", "country", "vat", "reference"],
                "additionalProperties": false
              }
            },
            "raw_fields": {
              "type": "array",
              "description": "Any other labelled field on the document NOT captured above (e.g. supplier number, EDI id, contract no, cost centre). Each is a label+value pair exactly as printed. Empty array if none.",
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "value": { "type": "string" }
                },
                "required": ["label", "value"],
                "additionalProperties": false
              }
            },
            "sub_total":     { "type": "number" },
            "tax_total":     { "type": "number" },
            "grand_total":   { "type": "number" },
            "lines": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "line_number":              { "type": "integer" },
                  "buyer_item_code":          { "type": "string" },
                  "manufacturer_part_number": { "type": "string", "description": "The manufacturer/vendor product number (e.g. 'Ihre Materialnr', ManufPN). Empty if none." },
                  "customer_part_number":     { "type": "string" },
                  "description":              { "type": "string" },
                  "quantity":                 { "type": "number" },
                  "unit":                     { "type": "string" },
                  "unit_price":               { "type": "number" },
                  "discount_percent":         { "type": "number" },
                  "line_amount":              { "type": "number" },
                  "net_amount":               { "type": "number" },
                  "tax_rate":                 { "type": "number" },
                  "unspsc":                   { "type": "string" },
                  "recipient":                { "type": "string" },
                  "contract_number":          { "type": "string" },
                  "delivery_date":            { "type": "string" }
                },
                "required": ["line_number", "buyer_item_code", "manufacturer_part_number", "customer_part_number", "description", "quantity", "unit", "unit_price", "discount_percent", "line_amount", "net_amount", "tax_rate", "unspsc", "recipient", "contract_number", "delivery_date"],
                "additionalProperties": false
              }
            }
          },
          "required": ["confidence", "document_type", "po_number", "order_date", "currency", "buyer_name", "supplier_name", "payment_terms", "incoterms", "shipping_method", "buyer_order_ref", "contact", "parties", "raw_fields", "sub_total", "tax_total", "grand_total", "lines"],
          "additionalProperties": false
        }
        """u8.ToArray());
```

- [ ] **Step 4: Extend the SystemPrompt**

Append these sentences to the `SystemPrompt` string (`OpenAiPdfOrderExtractor.cs:96-120`), before the final `confidence` sentence:

```csharp
        "Capture every named address as a party in 'parties' with its role (shipTo / billTo / " +
        "remitTo), street, city, postal_code, country and VAT/tax id when printed. Capture the " +
        "ordering contact (name/email/phone) in 'contact'. Capture incoterms / delivery terms, " +
        "shipping_method and the buyer's own order reference when stated. For each line capture the " +
        "manufacturer/vendor part number, any customer part number, discount %, UNSPSC, per-line " +
        "recipient, contract number and net amount when printed. Put ANY other labelled value you see " +
        "but cannot place into a field into 'raw_fields' as a label+value pair, copied verbatim — " +
        "never invent or omit. Leave a string empty and a number 0 when the document does not state it. " +
```

- [ ] **Step 5: Widen the DTOs**

Add two DTO records and widen `ExtractionDto`/`ExtractionLineDto` (`OpenAiPdfOrderExtractor.cs:754-777`):

```csharp
    internal sealed record ExtractionPartyDto(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("street")] string? Street = null,
        [property: JsonPropertyName("city")] string? City = null,
        [property: JsonPropertyName("postal_code")] string? PostalCode = null,
        [property: JsonPropertyName("country")] string? Country = null,
        [property: JsonPropertyName("vat")] string? Vat = null,
        [property: JsonPropertyName("reference")] string? Reference = null);

    internal sealed record RawFieldDto(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("value")] string Value);

    internal sealed record ContactDto(
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("email")] string? Email = null,
        [property: JsonPropertyName("phone")] string? Phone = null);
```

Append to `ExtractionDto` (after `GrandTotal`):

```csharp
        , [property: JsonPropertyName("incoterms")] string? Incoterms = null
        , [property: JsonPropertyName("shipping_method")] string? ShippingMethod = null
        , [property: JsonPropertyName("buyer_order_ref")] string? BuyerOrderRef = null
        , [property: JsonPropertyName("contact")] ContactDto? Contact = null
        , [property: JsonPropertyName("parties")] IReadOnlyList<ExtractionPartyDto>? Parties = null
        , [property: JsonPropertyName("raw_fields")] IReadOnlyList<RawFieldDto>? RawFields = null
```

Append to `ExtractionLineDto` (after `DeliveryDate`):

```csharp
        , [property: JsonPropertyName("manufacturer_part_number")] string? ManufacturerPartNumber = null
        , [property: JsonPropertyName("customer_part_number")] string? CustomerPartNumber = null
        , [property: JsonPropertyName("discount_percent")] double? DiscountPercent = null
        , [property: JsonPropertyName("unspsc")] string? Unspsc = null
        , [property: JsonPropertyName("recipient")] string? Recipient = null
        , [property: JsonPropertyName("contract_number")] string? ContractNumber = null
        , [property: JsonPropertyName("net_amount")] double? NetAmount = null
```

- [ ] **Step 6: Map the new fields in ValidateAndMap**

In the per-line `lines.Add(new ExtractedOrderLine(...))` call inside `ValidateAndMap` (`OpenAiPdfOrderExtractor.cs:~510`), append after `DeliveryDate: deliveryDate`:

```csharp
                ManufacturerPartNumber: NullIfBlank(l.ManufacturerPartNumber),
                CustomerPartNumber: NullIfBlank(l.CustomerPartNumber),
                DiscountPercent: TryToDecimal(l.DiscountPercent, out var disc) ? disc : null,
                Unspsc: NullIfBlank(l.Unspsc),
                Recipient: NullIfBlank(l.Recipient),
                ContractNumber: NullIfBlank(l.ContractNumber),
                NetAmount: TryToDecimal(l.NetAmount, out var net) ? net : null,
```

In the `new ExtractedOrder(...)` call (`OpenAiPdfOrderExtractor.cs:~520`), append after `DocumentType: NormalizeDocumentType(dto.DocumentType)`:

```csharp
            , Parties: dto.Parties?.Select(p => new ExtractedParty(
                p.Role, NullIfBlank(p.Name), NullIfBlank(p.Street), NullIfBlank(p.City),
                NullIfBlank(p.PostalCode), NullIfBlank(p.Country), NullIfBlank(p.Vat),
                Reference: NullIfBlank(p.Reference))).Where(p => HasAnyValue(p)).ToList()
            , ContactName: NullIfBlank(dto.Contact?.Name)
            , ContactEmail: NullIfBlank(dto.Contact?.Email)
            , ContactPhone: NullIfBlank(dto.Contact?.Phone)
            , Incoterms: NullIfBlank(dto.Incoterms)
            , ShippingMethod: NullIfBlank(dto.ShippingMethod)
            , BuyerOrderRef: NullIfBlank(dto.BuyerOrderRef)
            , RawFields: dto.RawFields?.Where(f => !string.IsNullOrWhiteSpace(f.Value))
                .Select(f => new ExtractedRawField(f.Label?.Trim() ?? "", f.Value.Trim())).ToList()
```

Add `ExtractedRawField` to `IOrderService.cs` and an `RawFields` param on `ExtractedOrder` (mirror in `ParsedOrder` + carry through `MapExtractedToParsed` from Task 2 — add `RawFields` to both records and the mapper alongside `Parties`):

```csharp
public sealed record ExtractedRawField(string Label, string Value);
```

Add the helpers near the other static helpers in `OpenAiPdfOrderExtractor`:

```csharp
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static bool HasAnyValue(ExtractedParty p) =>
        p.Name is not null || p.Street is not null || p.City is not null || p.Vat is not null;
```

> Note: `raw_fields` and scanned-PDF parties are unverifiable (the anti-hallucination check only
> covers numbers in `sourceText`). Do NOT add them to the arithmetic/verbatim checks; they ride
> through as advisory data. The existing per-line review flagging is unchanged.

- [ ] **Step 7: Run the widening tests**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidateAndMapWideningTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Infrastructure/Services/Ai/OpenAiPdfOrderExtractor.cs ProcuLink.Core/Services/IOrderService.cs ProcuLink.Transform/Parsing/ParsedOrder.cs ProcuLink.Api/Services/Orders/OrderIngestionService.cs ProcuLink.Infrastructure.Tests/Ai/PdfExtractionWideningTests.cs
git commit -m "feat(extraction): widen PDF schema to parties/contact/MPN/incoterms + raw_fields"
```

---

### Task 4: Widen the email-body extractor to parity (parties / contact / raw_fields)

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/Ai/OpenAiEmailBodyOrderExtractor.cs` (schema 34-63, DTOs 321-335, MapToOrder 290-314)

- [ ] **Step 1: Write the failing test**

Add to `PdfExtractionWideningTests.cs`:

```csharp
public class EmailExtractorWideningTests
{
    [Fact]
    public void Email_MapToOrder_carries_parties_and_raw_fields()
    {
        var dto = new OpenAiEmailBodyOrderExtractor.ExtractionDto(
            0.9, "PO-9", "2026-06-12", "EUR", "Acme",
            new[] { new OpenAiEmailBodyOrderExtractor.ExtractionLineDto(1, "B1", "Item", 1, "PC", 9.0) },
            Parties: new[] { new OpenAiEmailBodyOrderExtractor.ExtractionPartyDto("shipTo", Name: "DC", Vat: "ATU2") },
            RawFields: new[] { new OpenAiEmailBodyOrderExtractor.RawFieldDto("PR", "PR-1") });

        var order = OpenAiEmailBodyOrderExtractor.MapToOrderForTest(dto);

        Assert.Equal("ATU2", order.Parties!.Single().Vat);
        Assert.Contains(order.RawFields!, f => f.Label == "PR");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~EmailExtractorWideningTests"`
Expected: FAIL — DTOs lack the params; `MapToOrderForTest` missing.

- [ ] **Step 3: Widen the email schema, DTOs, and MapToOrder**

In `OpenAiEmailBodyOrderExtractor.cs`: (a) add `parties` (same shape as Task 3) + `raw_fields` to the schema (34-63) and to the `required` array; (b) add `ExtractionPartyDto` + `RawFieldDto` and append `Parties`/`RawFields` to `ExtractionDto` (321-335); (c) in `MapToOrder` (290-314) build the `ExtractedOrder` with `Parties:`/`RawFields:` populated the same way as Task 3; (d) add a test seam:

```csharp
    internal static ExtractedOrder MapToOrderForTest(ExtractionDto dto) => MapToOrder(dto);
```

(Email stays a subset — no incoterms/contact object unless the body carries them; parties + raw_fields are the high-value additions.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~EmailExtractorWideningTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Infrastructure/Services/Ai/OpenAiEmailBodyOrderExtractor.cs ProcuLink.Infrastructure.Tests/Ai/PdfExtractionWideningTests.cs
git commit -m "feat(extraction): widen email-body extractor to parties + raw_fields"
```

---

### Task 5: New entities (OrderParty, SourceCapture) + new columns + DbContext config

**Files:**
- Create: `ProcuLink.Core/Entities/OrderParty.cs`, `ProcuLink.Core/Entities/SourceCapture.cs`
- Modify: `ProcuLink.Core/Entities/PurchaseOrderEntity.cs`, `PurchaseOrderLineEntity.cs`
- Modify: `ProcuLink.Infrastructure/ProcuLinkDbContext.cs`
- Modify: `ProcuLink.Core/Services/ParsedFileOutput.cs`

- [ ] **Step 1: Create the OrderParty entity**

```csharp
namespace ProcuLink.Core.Entities;

/// <summary>
/// Phase 1 lossless capture: a named party (address + tax/EDI id) on a purchase order.
/// Child of <see cref="PurchaseOrderEntity"/>; one row per ship-to / bill-to / remit-to.
/// Table <c>order_parties</c> (migration <c>AddLosslessCanonicalCapture</c>). All value
/// columns nullable — a document may carry only some.
/// </summary>
public class OrderParty
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrgId { get; set; }
    /// <summary>"shipTo" | "billTo" | "remitTo" | "buyer" | "supplier".</summary>
    public string Role { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Vat { get; set; }
    public string? RegNr { get; set; }
    public string? EdiCode { get; set; }
    public string? Reference { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    public PurchaseOrderEntity Order { get; set; } = null!;
}
```

- [ ] **Step 2: Create the SourceCapture entity**

```csharp
using System.Text.Json;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Phase 1 lossless raw bag: every field/token we saw on the inbound document that the
/// canonical model did not promote to a typed column. ONE row per order. For structured
/// formats (CSV/XLSX/XML) <see cref="TokensJson"/> holds the full <c>SourceToken</c> set;
/// for the LLM PDF/email path it holds the extractor's <c>raw_fields</c>. Immutable after
/// insert and revision-pinnable (Phase 4). Table <c>source_captures</c> (migration
/// <c>AddLosslessCanonicalCapture</c>). Kept deliberately OUT of <c>purchase_orders.canonical_json</c>
/// (already triple-overloaded) so the spine row stays lean.
/// </summary>
public class SourceCapture
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrgId { get; set; }
    /// <summary>Detected source format, e.g. "csv" | "xlsx" | "xml" | "pdf" | "email".</summary>
    public string Format { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; }
    /// <summary>jsonb: full token set or raw_fields, keyed by token id / label.</summary>
    public JsonDocument? TokensJson { get; set; }
    /// <summary>Optional extracted plain text (PDF/email), for audit/replay.</summary>
    public string? RawText { get; set; }
    /// <summary>Optional page/segment references, free-form.</summary>
    public string? PageRefs { get; set; }

    public PurchaseOrderEntity Order { get; set; } = null!;
}
```

- [ ] **Step 3: Add new columns + navs to the order entities**

In `PurchaseOrderEntity.cs`, after `RequestedDeliveryDate`, add:

```csharp
    // ── Phase 1 lossless capture (nullable; copy the RequestedDeliveryDate column pattern) ──
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Incoterms { get; set; }
    public string? ShippingMethod { get; set; }
    public string? BuyerOrderRef { get; set; }
```

In the `// Navigation` block of `PurchaseOrderEntity.cs`, add:

```csharp
    public List<OrderParty> Parties { get; set; } = new();
    public SourceCapture? SourceCapture { get; set; }
```

In `PurchaseOrderLineEntity.cs`, after `DeliveryDate`, add:

```csharp
    // ── Phase 1 lossless capture (nullable). ManufacturerPartNumber = the catalog key (Phase 2). ──
    public string? ManufacturerPartNumber { get; set; }
    public string? CustomerPartNumber { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string? Unspsc { get; set; }
    public string? Recipient { get; set; }
    public string? ContractNumber { get; set; }
    public decimal? NetAmount { get; set; }
```

- [ ] **Step 4: Configure the new columns + entities in ProcuLinkDbContext**

In the `PurchaseOrderEntity` config block (`ProcuLinkDbContext.cs:412-479`), after the `requested_delivery_date` line, add:

```csharp
    // Phase 1 lossless capture (nullable additive columns).
    b.Property(x => x.ContactName).HasColumnName("contact_name");
    b.Property(x => x.ContactEmail).HasColumnName("contact_email");
    b.Property(x => x.ContactPhone).HasColumnName("contact_phone");
    b.Property(x => x.Incoterms).HasColumnName("incoterms");
    b.Property(x => x.ShippingMethod).HasColumnName("shipping_method");
    b.Property(x => x.BuyerOrderRef).HasColumnName("buyer_order_ref");
```

In the `PurchaseOrderLineEntity` config block (`481-512`), after the `delivery_date` line, add:

```csharp
    b.Property(x => x.ManufacturerPartNumber).HasColumnName("manufacturer_part_number");
    b.Property(x => x.CustomerPartNumber).HasColumnName("customer_part_number");
    b.Property(x => x.DiscountPercent).HasColumnName("discount_percent").HasColumnType("numeric(7,4)");
    b.Property(x => x.Unspsc).HasColumnName("unspsc");
    b.Property(x => x.Recipient).HasColumnName("recipient");
    b.Property(x => x.ContractNumber).HasColumnName("contract_number");
    b.Property(x => x.NetAmount).HasColumnName("net_amount").HasColumnType("numeric(18,4)");
```

Add new entity configs after the line config block (reuse the `jsonDocConverter` already defined at `ProcuLinkDbContext.cs:195-197` for the jsonb column):

```csharp
modelBuilder.Entity<OrderParty>(b =>
{
    b.ToTable("order_parties");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.OrderId).HasColumnName("order_id");
    b.Property(x => x.OrgId).HasColumnName("org_id");
    b.Property(x => x.Role).HasColumnName("role").IsRequired();
    b.Property(x => x.Name).HasColumnName("name");
    b.Property(x => x.Street).HasColumnName("street");
    b.Property(x => x.City).HasColumnName("city");
    b.Property(x => x.PostalCode).HasColumnName("postal_code");
    b.Property(x => x.Country).HasColumnName("country");
    b.Property(x => x.Vat).HasColumnName("vat");
    b.Property(x => x.RegNr).HasColumnName("reg_nr");
    b.Property(x => x.EdiCode).HasColumnName("edi_code");
    b.Property(x => x.Reference).HasColumnName("reference");
    b.Property(x => x.ContactName).HasColumnName("contact_name");
    b.Property(x => x.Email).HasColumnName("email");
    b.Property(x => x.Phone).HasColumnName("phone");
    b.HasOne(x => x.Order).WithMany(x => x.Parties).HasForeignKey(x => x.OrderId);
    b.HasIndex(x => new { x.OrgId, x.OrderId }).HasDatabaseName("IX_order_parties_org_id_order_id");
});

modelBuilder.Entity<SourceCapture>(b =>
{
    b.ToTable("source_captures");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.OrderId).HasColumnName("order_id");
    b.Property(x => x.OrgId).HasColumnName("org_id");
    b.Property(x => x.Format).HasColumnName("format").IsRequired();
    b.Property(x => x.CapturedAt).HasColumnName("captured_at").HasColumnType("timestamptz");
    b.Property(x => x.TokensJson).HasColumnName("tokens_json").HasColumnType("jsonb").HasConversion(jsonDocConverter);
    b.Property(x => x.RawText).HasColumnName("raw_text");
    b.Property(x => x.PageRefs).HasColumnName("page_refs");
    b.HasOne(x => x.Order).WithOne(x => x.SourceCapture).HasForeignKey<SourceCapture>(x => x.OrderId);
    b.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("IX_source_captures_order_id");
});
```

Add `DbSet`s near the other `DbSet` declarations in `ProcuLinkDbContext`:

```csharp
    public DbSet<OrderParty> OrderParties => Set<OrderParty>();
    public DbSet<SourceCapture> SourceCaptures => Set<SourceCapture>();
```

- [ ] **Step 5: Thread tokens through ParsedFileOutput**

Modify `ProcuLink.Core/Services/ParsedFileOutput.cs:11-14` to add the token list (additive, defaulted):

```csharp
public sealed record ParsedFileOutput(
    PurchaseOrderEntity Entity,
    IReadOnlyList<string>? ColumnHeaders,
    string DetectedFormat,
    IReadOnlyList<ProcuLink.Transform.Tokenizing.SourceToken>? Tokens = null);
```

- [ ] **Step 6: Update InMemory test contexts' Ignore lists**

In every test `DbContext` subclass that bulk-`Ignore`s entities (grep: `modelBuilder.Ignore<PurchaseOrderEntity>`), add alongside them:

```csharp
        modelBuilder.Ignore<OrderParty>();
        modelBuilder.Ignore<SourceCapture>();
```

- [ ] **Step 7: Build**

Run: `dotnet build ProcuLink.slnx --no-restore`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Core/Entities/OrderParty.cs ProcuLink.Core/Entities/SourceCapture.cs ProcuLink.Core/Entities/PurchaseOrderEntity.cs ProcuLink.Core/Entities/PurchaseOrderLineEntity.cs ProcuLink.Infrastructure/ProcuLinkDbContext.cs ProcuLink.Core/Services/ParsedFileOutput.cs
git commit -m "feat(model): OrderParty + SourceCapture entities + lossless columns + DbContext config"
```

---

### Task 6: EF migration

**Files:**
- Create (generated): `ProcuLink.Infrastructure/Migrations/*_AddLosslessCanonicalCapture.cs`

- [ ] **Step 1: Generate the migration**

Run: `dotnet ef migrations add AddLosslessCanonicalCapture -p ProcuLink.Infrastructure -s ProcuLink.Api`
Expected: a migration file is created.

- [ ] **Step 2: Verify the generated migration**

Open the generated `*_AddLosslessCanonicalCapture.cs`. Confirm it: adds the 6 nullable columns to `purchase_orders`, the 7 nullable columns to `purchase_order_lines`, creates `order_parties` and `source_captures` tables with the indexes above, and the unique index on `source_captures.order_id`. There must be **no** `DropColumn`/`DropTable` for existing schema.

- [ ] **Step 3: Apply to the dev database**

Run: `dotnet ef database update --project ProcuLink.Infrastructure --startup-project ProcuLink.Api`
Expected: applies cleanly.

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Infrastructure/Migrations/
git commit -m "feat(db): AddLosslessCanonicalCapture migration (order_parties, source_captures, columns)"
```

---

### Task 7: Wire ingest — set new columns, insert parties + SourceCapture (LLM path)

**Files:**
- Modify: `ProcuLink.Api/Services/Orders/OrderIngestionService.cs` (399-431, 715-732, 1008-1030, plus party + SourceCapture inserts at 433 / 744)
- Test: `ProcuLink.Api.Tests/Integration/LosslessCapturePersistencePostgresTests.cs`

- [ ] **Step 1: Write the failing real-Postgres round-trip test**

```csharp
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

[Collection("postgres-container")]
public sealed class LosslessCapturePersistencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16")
            .WithDatabase($"proculink_lossless_{Guid.NewGuid():N}")
            .WithUsername("postgres").WithPassword("postgres").Build();
        await _pg.StartAsync();
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
        await using var migrate = new ProcuLinkDbContext(_options);
        await migrate.Database.MigrateAsync();
    }

    public async Task DisposeAsync() { if (_pg is not null) await _pg.DisposeAsync(); }

    [DockerRequiredFact]
    public async Task Parties_line_columns_and_source_capture_survive_reload()
    {
        var orgId = Guid.NewGuid(); var orderId = Guid.NewGuid();
        // ... seed org + supplier rows the FK requires (copy the seed helper from
        // ProvenancePersistencePostgresTests) ...

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = /* seeded */ default,
                PoNumber = "4730154181", Currency = "EUR", Status = "pending_review",
                OrderDate = new DateOnly(2026, 6, 12), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                Incoterms = "DDP", ContactEmail = "redacted@example.invalid",
                Parties = { new OrderParty { Id = Guid.NewGuid(), OrgId = orgId, Role = "shipTo", Name = "REDACTED-PARTY", City = "Linz", Vat = "REDACTED-TAXID" } },
                Lines = { new PurchaseOrderLineEntity { Id = Guid.NewGuid(), LineNumber = 1, BuyerItemCode = "00010", Quantity = 1, UnitPrice = 306.28m, ManufacturerPartNumber = "SCPMX94EGK", Recipient = "redacted@example.invalid" } },
                SourceCapture = new SourceCapture { Id = Guid.NewGuid(), OrgId = orgId, Format = "pdf", CapturedAt = DateTime.UtcNow, TokensJson = System.Text.Json.JsonDocument.Parse("""[{"label":"EDI id","value":"REDACTED-TAXID"}]""") },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var o = await db.PurchaseOrders.AsNoTracking().Include(x => x.Parties).Include(x => x.Lines).Include(x => x.SourceCapture).SingleAsync(x => x.Id == orderId);
            Assert.Equal("DDP", o.Incoterms);
            Assert.Equal("REDACTED-TAXID", o.Parties.Single().Vat);
            Assert.Equal("SCPMX94EGK", o.Lines.Single().ManufacturerPartNumber);
            Assert.Contains("REDACTED-TAXID", o.SourceCapture!.TokensJson!.RootElement.GetRawText());
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~LosslessCapturePersistence"`
Expected: FAIL (compile if columns missing, or assertion if not persisted) — confirms columns are REAL before wiring ingest.

- [ ] **Step 3: Set new header columns at the sync ingest site**

In `CreateStubFromParsedOrderAsync` (`OrderIngestionService.cs:399-431`), in the `new PurchaseOrderEntity { ... }` initializer after `RequestedDeliveryDate = order.RequestedDeliveryDate,` add:

```csharp
    ContactName    = order.ContactName,
    ContactEmail   = order.ContactEmail,
    ContactPhone   = order.ContactPhone,
    Incoterms      = order.Incoterms,
    ShippingMethod = order.ShippingMethod,
    BuyerOrderRef  = order.BuyerOrderRef,
    Parties        = (order.Parties ?? Array.Empty<ParsedParty>()).Select(p => new OrderParty
    {
        Id = Guid.NewGuid(), OrgId = organisationId, Role = p.Role,
        Name = p.Name, Street = p.Street, City = p.City, PostalCode = p.PostalCode,
        Country = p.Country, Vat = p.Vat, RegNr = p.RegNr, EdiCode = p.EdiCode,
        Reference = p.Reference, ContactName = p.ContactName, Email = p.Email, Phone = p.Phone,
    }).ToList(),
```

(`order` here is the `ParsedOrder`; confirm the local variable name at line 399 and match it.)

- [ ] **Step 4: Set new header columns at the async ingest site**

In `ParseStoredFileAsync` (`OrderIngestionService.cs:715-732`), add the locals before the `ExecuteUpdateAsync` and the `SetProperty` calls inside it:

```csharp
var newContactName    = parsedOrder.ContactName;
var newContactEmail   = parsedOrder.ContactEmail;
var newContactPhone   = parsedOrder.ContactPhone;
var newIncoterms      = parsedOrder.Incoterms;
var newShippingMethod = parsedOrder.ShippingMethod;
var newBuyerOrderRef  = parsedOrder.BuyerOrderRef;
```

```csharp
        .SetProperty(o => o.ContactName,    newContactName)
        .SetProperty(o => o.ContactEmail,   newContactEmail)
        .SetProperty(o => o.ContactPhone,   newContactPhone)
        .SetProperty(o => o.Incoterms,      newIncoterms)
        .SetProperty(o => o.ShippingMethod, newShippingMethod)
        .SetProperty(o => o.BuyerOrderRef,  newBuyerOrderRef)
```

Parties + SourceCapture are child rows (not `SetProperty`-able). After the `ExecuteUpdateAsync` block in `ParseStoredFileAsync`, replace any existing parties for this order then insert the fresh set + the SourceCapture row:

```csharp
// Phase 1: replace child party rows (idempotent across Hangfire retries) and upsert the raw bag.
await _db.OrderParties.Where(p => p.OrderId == orderId && p.OrgId == organisationId).ExecuteDeleteAsync(ct);
if (parsedOrder.Parties is { Count: > 0 })
{
    _db.OrderParties.AddRange(parsedOrder.Parties.Select(p => new OrderParty
    {
        Id = Guid.NewGuid(), OrderId = orderId, OrgId = organisationId, Role = p.Role,
        Name = p.Name, Street = p.Street, City = p.City, PostalCode = p.PostalCode,
        Country = p.Country, Vat = p.Vat, RegNr = p.RegNr, EdiCode = p.EdiCode,
        Reference = p.Reference, ContactName = p.ContactName, Email = p.Email, Phone = p.Phone,
    }));
}
await UpsertSourceCaptureAsync(orderId, organisationId, detected, parsedFileOutput?.Tokens, rawText: structuredSourceText, parsedOrder, now, ct);
await _db.SaveChangesAsync(ct);
```

- [ ] **Step 5: Set new line columns**

In `BuildLineEntitiesAsync` (`OrderIngestionService.cs:1008-1030`), in the `new PurchaseOrderLineEntity { ... }` after `DeliveryDate = line.DeliveryDate` add:

```csharp
    ManufacturerPartNumber = line.ManufacturerPartNumber,
    CustomerPartNumber     = line.CustomerPartNumber,
    DiscountPercent        = line.DiscountPercent,
    Unspsc                 = line.Unspsc,
    Recipient              = line.Recipient,
    ContractNumber         = line.ContractNumber,
    NetAmount              = line.NetAmount,
```

- [ ] **Step 6: Add the SourceCapture upsert helper**

Add to `OrderIngestionService` (idempotent — delete-then-insert so Hangfire retries don't duplicate):

```csharp
    private async Task UpsertSourceCaptureAsync(
        Guid orderId, Guid organisationId, DetectedFormat? detected,
        IReadOnlyList<SourceToken>? tokens, string? rawText, ParsedOrder parsedOrder,
        DateTime now, CancellationToken ct)
    {
        await _db.SourceCaptures.Where(s => s.OrderId == orderId && s.OrgId == organisationId).ExecuteDeleteAsync(ct);

        // Prefer the full structured token set; else fall back to the LLM raw_fields bag.
        object? bag = tokens is { Count: > 0 }
            ? tokens.Select(t => new { id = t.Id, label = t.Label, value = t.Value, group = t.Group })
            : (parsedOrder.RawFields is { Count: > 0 }
                ? parsedOrder.RawFields.Select(f => new { label = f.Label, value = f.Value })
                : null);
        if (bag is null && string.IsNullOrWhiteSpace(rawText)) return;

        _db.SourceCaptures.Add(new SourceCapture
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = organisationId,
            Format = (detected?.Format ?? "unknown").ToString().ToLowerInvariant(),
            CapturedAt = now,
            TokensJson = bag is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(bag)),
            RawText = rawText,
        });
    }
```

> Add `RawFields` (`IReadOnlyList<ExtractedRawField>?`/`ParsedRawField?`) to `ParsedOrder` and carry it through `MapExtractedToParsed` (Task 2/3) — define `ParsedRawField(string Label, string Value)` in Transform alongside `ParsedParty`. Confirm the exact local names `detected`, `parsedFileOutput`, `structuredSourceText`, `now` at the `ParseStoredFileAsync` call site (lines 520-660) and match them; if `structuredSourceText` is not in scope, pass `null`.

- [ ] **Step 7: Run the round-trip test**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~LosslessCapturePersistence"`
Expected: PASS (skips with a clear reason if Docker is unavailable).

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Api/Services/Orders/OrderIngestionService.cs ProcuLink.Api.Tests/Integration/LosslessCapturePersistencePostgresTests.cs ProcuLink.Transform/Parsing/
git commit -m "feat(ingest): persist parties + lossless line columns + SourceCapture (LLM path)"
```

---

### Task 8: Persist the full structured-format token set

**Files:**
- Modify: `ProcuLink.Api/Services/Orders/OrderIngestionService.cs` (structured parse path)
- Test: `ProcuLink.Api.Tests/Integration/SourceTokenPersistencePostgresTests.cs`

- [ ] **Step 1: Write the failing test**

A `[DockerRequiredFact]` (same fixture shape as Task 7) that ingests a 3-column CSV with extra unmapped columns, then asserts `SourceCapture.TokensJson` contains every cell — including the unmapped column's value. (Build the order through the same ingest entry point the CSV path uses; assert via a fresh-context reload.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTokenPersistence"`
Expected: FAIL — no tokens captured for the CSV path.

- [ ] **Step 3: Tokenize on the structured path and thread tokens into ParsedFileOutput**

In `ParseStoredFileAsync`, where the in-memory `buffer` already exists (`OrderIngestionService.cs:520-534`), call the existing `ISourceTokenizer` (inject it via the constructor like `_formatDetector`) for structured extensions and attach the result to `ParsedFileOutput.Tokens`:

```csharp
IReadOnlyList<SourceToken>? sourceTokens = null;
if (extension is ".csv" or ".xlsx" or ".xml" or ".cxml" or ".edi" or ".x12")
{
    try { buffer.Position = 0; sourceTokens = await _tokenizer.TokenizeAsync(buffer.ToArray(), extension, ct); }
    catch (Exception ex) { _logger.LogWarning(ex, "Source tokenization failed for order {OrderId} (non-fatal)", orderId); }
}
```

Pass `sourceTokens` into `UpsertSourceCaptureAsync` (Task 7 Step 6 already accepts `tokens`).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTokenPersistence"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Services/Orders/OrderIngestionService.cs ProcuLink.Api.Tests/Integration/SourceTokenPersistencePostgresTests.cs
git commit -m "feat(ingest): persist full source-token set for structured formats"
```

---

### Task 9: 12-PO corpus regression (deterministic CI + documented live check)

**Files:**
- Create: `ProcuLink.Infrastructure.Tests/Ai/CorpusExtractionShapeTests.cs`
- Create: `docs/superpowers/plans/2026-06-13-phase1-corpus-live-check.md`

- [ ] **Step 1: Add deterministic per-vendor ValidateAndMap fixtures**

For each of the 12 vendor shapes, hand-build the `ExtractionDto` the LLM *should* return (parties/VAT/MPN/raw_fields populated from the DocParser reference values) and assert `ValidateAndMap` surfaces them. This runs in CI with no OpenAI key — it pins the *mapping contract*, not the model. Example (REDACTED-PARTY) reuses the Task-3 assertion; add EXEMPLAR SEAFOOD (per-line recipient), LähiTapiola (EDI id in raw_fields), REDACTED-PARTY (Incoterms), DNV (date in raw vs header), Danfoss (split contact).

- [ ] **Step 2: Run**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~CorpusExtractionShape"`
Expected: PASS.

- [ ] **Step 3: Document the live extraction check (operator-run, needs OpenAI key)**

Write `2026-06-13-phase1-corpus-live-check.md`: the 12 `~/Downloads` PDFs, the command to upload each through prod/dev with a real `Ai:OpenAI:ApiKey`, and the expected widened fields per vendor (from the DocParser xlsx). This is the honest "offer⇔works" proof that the schema widening extracts on real documents — it cannot run in CI (non-deterministic LLM, needs a key), so it is a checklist, not an assertion.

- [ ] **Step 4: Full suite + build green**

Run: `dotnet build ProcuLink.slnx --no-restore && dotnet test ProcuLink.slnx --no-restore`
Expected: build PASS; all tests PASS (Postgres tests skip cleanly if Docker is absent).

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Infrastructure.Tests/Ai/CorpusExtractionShapeTests.cs docs/superpowers/plans/2026-06-13-phase1-corpus-live-check.md
git commit -m "test(extraction): 12-PO corpus mapping-shape fixtures + live-check checklist"
```

---

## Self-review

- **Spec coverage (Phase 1):** widen extraction schema + raw_fields → Tasks 3-4; persist full token set for structured → Task 8; OrderParty + Tier-1 columns set at ingest (RequestedDeliveryDate pattern) → Tasks 5,7; SourceCapture table → Tasks 5,7; real-Postgres round-trip per the EF-Ignore/ExecuteUpdateAsync trap → Tasks 7,8; 12-PO corpus → Task 9. Email parity → Task 4. All covered.
- **Type consistency:** field names match across layers — `ManufacturerPartNumber`/`ContactEmail`/`Incoterms`/`BuyerOrderRef` are identical on `ParsedOrder(Line)`, `ExtractedOrder(Line)`, and the entities; `ParsedParty`/`ExtractedParty`/`OrderParty` share the same 13 members; `RawFields`/`ExtractedRawField`/`ParsedRawField` carried through `MapExtractedToParsed` and into `SourceCapture`. The `jsonDocConverter` reused for `tokens_json` is the one already defined at `ProcuLinkDbContext.cs:195-197`.
- **Known anchors to confirm at execution (cited, not placeheld):** the `order` vs `parsedOrder` local name at `OrderIngestionService:399`/`715`; the in-scope locals `detected`/`now`/`structuredSourceText` at the async site; and the `ISourceTokenizer` injection (constructor) — each has the exact pattern to copy.
- **Out of scope (Phases 2-4):** the `catalog.*` accessor, price-variance guard, `CanonicalFieldDef`, validation rules, target schemas, and the drag-wire UI are NOT in this plan.
