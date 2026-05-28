# Wave 3 — Invoice + ASN Canonical Models Design

_Date: 2026-05-28. Approved by founder. Implementation via writing-plans → executing-plans._

---

## Summary

Extend ProcuLink's engine to support inbound and outbound **Invoice** documents (UBL 2.1 full, EDIFACT INVOIC stub) and **Advance Shipping Notice / DESADV** documents (EDIFACT stub, entity model full). Follows Option A — parallel entity hierarchy — no changes to the existing PO pipeline.

---

## 1. Parse layer

New parse-layer records in `ProcuLink.Transform/Parsing/`, parallel to `ParsedOrder` / `ParsedOrderLine`.

### Invoice

```csharp
public sealed record ParsedInvoice(
    string InvoiceNumber,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string Currency,
    string? BuyerRef,
    string? SupplierRef,
    string? PaymentTerms,
    decimal SubTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    IReadOnlyList<ParsedInvoiceLine> Lines);

public sealed record ParsedInvoiceLine(
    int LineNumber,
    string Description,
    decimal Quantity,
    string UnitCode,
    decimal UnitPrice,
    decimal TaxRate,
    decimal LineTotal,
    string? BuyerItemCode,
    string? SupplierItemCode);
```

### ASN

```csharp
public sealed record ParsedAsn(
    string ShipmentId,
    DateOnly DespatchDate,
    DateOnly? EstimatedDeliveryDate,
    string? BuyerOrderRef,
    string? SupplierRef,
    IReadOnlyList<ParsedAsnPackage> Packages);

public sealed record ParsedAsnPackage(
    string PackageId,
    string? Sscc,
    IReadOnlyList<ParsedAsnLine> Lines);

public sealed record ParsedAsnLine(
    string? BuyerItemCode,
    string? SupplierItemCode,
    decimal Quantity,
    string UnitCode);
```

### Parser interfaces

```csharp
// ProcuLink.Transform/Parsing/IInvoiceParser.cs
public interface IInvoiceParser
{
    bool CanParse(string extension, string? contentType);
    Task<ParsedInvoice> ParseAsync(Stream stream, CancellationToken ct);
}

// ProcuLink.Transform/Parsing/IDesadvParser.cs
public interface IDesadvParser
{
    bool CanParse(string extension, string? contentType);
    Task<ParsedAsn> ParseAsync(Stream stream, CancellationToken ct);
}
```

**`InvoiceParserFactory`** registered as `IEnumerable<IInvoiceParser>`, extension/content-type routing.
**`DesadvParserFactory`** registered as `IEnumerable<IDesadvParser>`.

---

## 2. Entity layer

All entities in `ProcuLink.Core/Entities/`. All EF queries scoped by `OrganisationId`.

### InvoiceEntity

Table: `invoices`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `organisation_id` | uuid FK | |
| `supplier_id` | uuid FK nullable | |
| `buyer_id` | uuid FK nullable | |
| `invoice_number` | text | |
| `issue_date` | date | |
| `due_date` | date nullable | |
| `currency` | text | default `"EUR"` |
| `payment_terms` | text nullable | |
| `sub_total` | numeric(18,4) | |
| `tax_total` | numeric(18,4) | |
| `grand_total` | numeric(18,4) | |
| `status` | text | `pending_review` / `approved` / `forwarded` |
| `source_file_name` | text nullable | |
| `source_file_key` | text nullable | R2/local key |
| `created_at` | timestamptz | |
| `updated_at` | timestamptz | |

### InvoiceLineEntity

Table: `invoice_lines`

| Column | Type |
|---|---|
| `id` | uuid PK |
| `invoice_id` | uuid FK |
| `line_number` | int |
| `description` | text |
| `quantity` | numeric(18,4) |
| `unit_code` | text |
| `unit_price` | numeric(18,4) |
| `tax_rate` | numeric(8,4) |
| `line_total` | numeric(18,4) |
| `buyer_item_code` | text nullable |
| `supplier_item_code` | text nullable |

### AdvanceShippingNoticeEntity

Table: `advance_shipping_notices`

| Column | Type |
|---|---|
| `id` | uuid PK |
| `organisation_id` | uuid FK |
| `supplier_id` | uuid FK nullable |
| `buyer_id` | uuid FK nullable |
| `shipment_id` | text |
| `despatch_date` | date |
| `estimated_delivery_date` | date nullable |
| `buyer_order_ref` | text nullable |
| `supplier_ref` | text nullable |
| `source_file_name` | text nullable |
| `source_file_key` | text nullable |
| `created_at` | timestamptz |

### AsnPackageEntity / AsnPackageLineEntity

Tables: `asn_packages`, `asn_package_lines`

Standard child/grandchild FK pattern. `sscc` nullable text on package.

### Migrations

- `AddInvoicesAndLines` — `invoices` + `invoice_lines`
- `AddAdvanceShippingNotices` — `advance_shipping_notices` + `asn_packages` + `asn_package_lines`

---

## 3. Parsers

### Full implementation

**`UblInvoiceParser : IInvoiceParser`** in `ProcuLink.Transform/Parsing/`
- `CanParse`: `.xml` + UBL Invoice namespace (`urn:oasis:names:specification:ubl:schema:xsd:Invoice-2`)
- `ParseAsync`: `System.Xml.Serialization` over UBL 2.1 Invoice XSD (same approach as `UblOrderParser`)
- Maps: `cbc:ID` → `InvoiceNumber`, `cbc:IssueDate`, `cbc:DueDate`, `cac:TaxTotal/cbc:TaxAmount`, `cac:LegalMonetaryTotal`, `cac:InvoiceLine` iteration
- Throws `UblParseException` on malformed input (reuse existing exception)

### EdiFabric stubs

**`EdifactInvoiceParser : IInvoiceParser`**
- `CanParse`: `.edi` / `application/edifact` with INVOIC message type sniff
- `ParseAsync`: `throw new NotImplementedException("EdiFabric license required — see docs/format-channel-roadmap.md §4.4")`
- Constructor: `ILogger<EdifactInvoiceParser>` (logs warning on construction that license is absent)
- Fully shaped, registered in DI — drop-in ready

**`EdifactDesadvParser : IDesadvParser`**
- Same stub pattern for DESADV message type

**`UblDesadvParser`** — deferred to Wave 5 (distinct XSD from UBL Invoice; defer until ASN model proven with real data).

### Upload endpoint extension

`POST /api/invoices/upload` accepts `.xml` (UBL Invoice). The existing orders upload is unchanged.

---

## 4. Output transformers

Interface in `ProcuLink.Core`:

```csharp
public interface IInvoiceTransformService
{
    string Format { get; }  // "csv" | "xml" | "json"
    Task<byte[]> TransformAsync(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, CancellationToken ct);
}
```

### Full implementations in `ProcuLink.Transform/Output/`

**`CsvInvoiceTransformService`** — header row with invoice metadata + one row per line. CsvHelper. Format key: `"csv"`.

**`XmlInvoiceTransformService`** — generic `<Invoice><Lines>` envelope. `System.Xml.Serialization`. Format key: `"xml"`.

**`JsonInvoiceTransformService`** — serialized `InvoiceDto`. `System.Text.Json`. Format key: `"json"`.

All three registered as `IEnumerable<IInvoiceTransformService>` in DI.

### ASN output stubs

```csharp
public interface IDesadvTransformService
{
    string Format { get; }
    Task<byte[]> TransformAsync(AdvanceShippingNoticeEntity asn, IReadOnlyList<AsnPackageEntity> packages, CancellationToken ct);
}
```

`CsvDesadvTransformService` skeleton — throws `NotImplementedException`. Placeholder until package/SSCC hierarchy decisions are made with real customer data.

---

## 5. Service + API layer

### Service contracts (ProcuLink.Core)

```csharp
public interface IInvoiceService
{
    Task<InvoiceEntity> CreateStubAsync(Guid orgId, Guid? supplierId, Stream stream, string fileName, string contentType, CancellationToken ct);
    Task<InvoiceEntity?> GetAsync(Guid orgId, Guid invoiceId, CancellationToken ct);
    Task<IReadOnlyList<InvoiceEntity>> ListAsync(Guid orgId, CancellationToken ct);
    Task ApproveAsync(Guid orgId, Guid invoiceId, CancellationToken ct);
    Task<byte[]> ForwardAsync(Guid orgId, Guid invoiceId, string outputFormat, CancellationToken ct);
}
```

`IDesadvService` — same shape, stubbed bodies returning `Task.CompletedTask` / `Task.FromResult(Array.Empty<...>())`.

### InvoiceService (ProcuLink.Infrastructure)

- `CreateStubAsync`: stores source file to R2/local, creates `InvoiceEntity` with `status = pending_review`, org-scoped
- `ForwardAsync`: loads entity + lines, resolves `IInvoiceTransformService` by format key, creates `OutboundArtifact` (reuses existing entity), returns bytes

### Hangfire job (ProcuLink.Worker)

**`ParseInvoiceJob`** — mirrors `ParseOrderJob`:
1. Load `InvoiceEntity` stub by id
2. Load source file stream from storage
3. `InvoiceParserFactory.GetParser(ext, contentType)`
4. `ParseAsync` → `ParsedInvoice`
5. Map to `InvoiceLineEntity` rows, upsert via `IInvoiceService`
6. Update status to `pending_review` (or `parse_failed` on exception)

### API endpoints (ProcuLink.Api — InvoiceController)

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/invoices/upload` | Clerk | Multipart, idempotency-keyed, enqueues `ParseInvoiceJob` |
| `GET` | `/api/invoices` | Clerk | Paginated org-scoped list |
| `GET` | `/api/invoices/{id}` | Clerk | Detail with lines |
| `POST` | `/api/invoices/{id}/approve` | Clerk | Status → `approved` |
| `GET` | `/api/invoices/{id}/download` | Clerk | `?format=csv\|xml\|json` |

**`DesadvController`** — same endpoint signatures, all return `501 Not Implemented` with `{ message: "ASN/DESADV support requires EdiFabric license" }` body until license arrives.

---

## 6. Tests

- `ParsedInvoice` + `UblInvoiceParser`: parse a minimal UBL 2.1 Invoice XML fixture → assert all fields
- `EdifactInvoiceParser`: `CanParse` returns true for `.edi`; `ParseAsync` throws `NotImplementedException`
- `CsvInvoiceTransformService`: given a known entity + lines → assert CSV header + row count
- `XmlInvoiceTransformService`: assert valid XML, root element, line count
- `JsonInvoiceTransformService`: assert JSON round-trip
- `InvoiceService.CreateStubAsync`: assert entity persisted with correct org scope
- `InvoiceService.ListAsync`: assert org isolation (no cross-org leakage)

All in `ProcuLink.Infrastructure.Tests` / `ProcuLink.Transform.Tests` following existing test patterns.

---

## 7. Out of scope for Wave 3

- UBL Despatch Advice parser (deferred to Wave 5)
- EDIFACT INVOIC / DESADV full implementation (requires EdiFabric license)
- Frontend invoice/ASN pages (Group I pass 16+)
- Invoice-to-supplier delivery dispatch (uses existing `DeliveryService` unchanged)
- OrderConfirmation / ORDRSP canonical model (separate Wave)
- EDI X12 810 invoice (post-12 month)
