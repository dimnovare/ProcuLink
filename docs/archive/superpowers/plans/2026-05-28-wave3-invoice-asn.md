# Wave 3 — Invoice + ASN Canonical Models Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add canonical Invoice (UBL 2.1 full, EDIFACT INVOIC stub) and ASN/DESADV (entity full, parser stub) pipelines alongside the existing PO pipeline without touching any PO code.

**Architecture:** Parallel entity hierarchy — `InvoiceEntity`/`InvoiceLineEntity` and `AdvanceShippingNoticeEntity`/`AsnPackageEntity`/`AsnPackageLineEntity` as fully independent trees. New `IInvoiceParser`/`IDesadvParser` interfaces and factories mirror `IPurchaseOrderParser`/`OrderParserFactory`. New `IInvoiceTransformService` with CSV/XML/JSON implementations. New `InvoiceService`, `ParseInvoiceJob`, and `InvoiceController`.

**Tech Stack:** ASP.NET Core 8, EF Core 8 + Npgsql, Hangfire, `System.Xml.Linq` (UBL parsing), xUnit + FluentAssertions + EF InMemory (tests)

---

## Task 1: Parse-layer records + parser interfaces

**Files:**
- Create: `ProcuLink.Transform/Parsing/ParsedInvoice.cs`
- Create: `ProcuLink.Transform/Parsing/ParsedAsn.cs`
- Create: `ProcuLink.Transform/Parsing/IInvoiceParser.cs`
- Create: `ProcuLink.Transform/Parsing/IDesadvParser.cs`
- Create: `ProcuLink.Transform/Parsing/InvoiceParseException.cs`
- Create: `ProcuLink.Transform/Parsing/DesadvParseException.cs`

- [ ] Create `ProcuLink.Transform/Parsing/ParsedInvoice.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

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

- [ ] Create `ProcuLink.Transform/Parsing/ParsedAsn.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

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

- [ ] Create `ProcuLink.Transform/Parsing/IInvoiceParser.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

public interface IInvoiceParser
{
    bool CanParse(string fileExtension, string? contentType = null);
    Task<ParsedInvoice> ParseAsync(Stream fileStream, CancellationToken ct);
}
```

- [ ] Create `ProcuLink.Transform/Parsing/IDesadvParser.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

public interface IDesadvParser
{
    bool CanParse(string fileExtension, string? contentType = null);
    Task<ParsedAsn> ParseAsync(Stream fileStream, CancellationToken ct);
}
```

- [ ] Create `ProcuLink.Transform/Parsing/InvoiceParseException.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

public sealed class InvoiceParseException : Exception
{
    public InvoiceParseException(string message) : base(message) { }
    public InvoiceParseException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] Create `ProcuLink.Transform/Parsing/DesadvParseException.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

public sealed class DesadvParseException : Exception
{
    public DesadvParseException(string message) : base(message) { }
    public DesadvParseException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] Build to verify:
```
dotnet build ProcuLink.Transform/ProcuLink.Transform.csproj --no-restore
```
Expected: 0 errors.

- [ ] Commit:
```
git add ProcuLink.Transform/Parsing/ParsedInvoice.cs ProcuLink.Transform/Parsing/ParsedAsn.cs ProcuLink.Transform/Parsing/IInvoiceParser.cs ProcuLink.Transform/Parsing/IDesadvParser.cs ProcuLink.Transform/Parsing/InvoiceParseException.cs ProcuLink.Transform/Parsing/DesadvParseException.cs
git commit -m "feat(wave3): add Invoice+ASN parse records and parser interfaces"
```

---

## Task 2: EF entities

**Files:**
- Create: `ProcuLink.Core/Entities/InvoiceEntity.cs`
- Create: `ProcuLink.Core/Entities/InvoiceLineEntity.cs`
- Create: `ProcuLink.Core/Entities/AdvanceShippingNoticeEntity.cs`
- Create: `ProcuLink.Core/Entities/AsnPackageEntity.cs`
- Create: `ProcuLink.Core/Entities/AsnPackageLineEntity.cs`

- [ ] Create `ProcuLink.Core/Entities/InvoiceEntity.cs`:

```csharp
namespace ProcuLink.Core.Entities;

public class InvoiceEntity
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? BuyerId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateOnly IssueDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? PaymentTerms { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    /// <summary>pending_review | approved | forwarded</summary>
    public string Status { get; set; } = "pending_review";
    public string? SourceFileName { get; set; }
    public string? SourceFileKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Organisation Organisation { get; set; } = null!;
    public List<InvoiceLineEntity> Lines { get; set; } = new();
}
```

- [ ] Create `ProcuLink.Core/Entities/InvoiceLineEntity.cs`:

```csharp
namespace ProcuLink.Core.Entities;

public class InvoiceLineEntity
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string UnitCode { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal LineTotal { get; set; }
    public string? BuyerItemCode { get; set; }
    public string? SupplierItemCode { get; set; }

    public InvoiceEntity Invoice { get; set; } = null!;
}
```

- [ ] Create `ProcuLink.Core/Entities/AdvanceShippingNoticeEntity.cs`:

```csharp
namespace ProcuLink.Core.Entities;

public class AdvanceShippingNoticeEntity
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? BuyerId { get; set; }
    public string ShipmentId { get; set; } = string.Empty;
    public DateOnly DespatchDate { get; set; }
    public DateOnly? EstimatedDeliveryDate { get; set; }
    public string? BuyerOrderRef { get; set; }
    public string? SupplierRef { get; set; }
    public string? SourceFileName { get; set; }
    public string? SourceFileKey { get; set; }
    public DateTime CreatedAt { get; set; }

    public Organisation Organisation { get; set; } = null!;
    public List<AsnPackageEntity> Packages { get; set; } = new();
}
```

- [ ] Create `ProcuLink.Core/Entities/AsnPackageEntity.cs`:

```csharp
namespace ProcuLink.Core.Entities;

public class AsnPackageEntity
{
    public Guid Id { get; set; }
    public Guid AsnId { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string? Sscc { get; set; }

    public AdvanceShippingNoticeEntity Asn { get; set; } = null!;
    public List<AsnPackageLineEntity> Lines { get; set; } = new();
}
```

- [ ] Create `ProcuLink.Core/Entities/AsnPackageLineEntity.cs`:

```csharp
namespace ProcuLink.Core.Entities;

public class AsnPackageLineEntity
{
    public Guid Id { get; set; }
    public Guid PackageId { get; set; }
    public string? BuyerItemCode { get; set; }
    public string? SupplierItemCode { get; set; }
    public decimal Quantity { get; set; }
    public string UnitCode { get; set; } = string.Empty;

    public AsnPackageEntity Package { get; set; } = null!;
}
```

- [ ] Build:
```
dotnet build ProcuLink.Core/ProcuLink.Core.csproj --no-restore
```

- [ ] Commit:
```
git add ProcuLink.Core/Entities/InvoiceEntity.cs ProcuLink.Core/Entities/InvoiceLineEntity.cs ProcuLink.Core/Entities/AdvanceShippingNoticeEntity.cs ProcuLink.Core/Entities/AsnPackageEntity.cs ProcuLink.Core/Entities/AsnPackageLineEntity.cs
git commit -m "feat(wave3): add Invoice and ASN entity classes"
```

---

## Task 3: DbContext registration + EF migrations

**Files:**
- Modify: `ProcuLink.Infrastructure/ProcuLinkDbContext.cs`
- Create: migration via `dotnet ef migrations add`

- [ ] Add DbSets and model config to `ProcuLinkDbContext.cs`. Add after the last existing `DbSet`:

```csharp
public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();
public DbSet<InvoiceLineEntity> InvoiceLines => Set<InvoiceLineEntity>();
public DbSet<AdvanceShippingNoticeEntity> AdvanceShippingNotices => Set<AdvanceShippingNoticeEntity>();
public DbSet<AsnPackageEntity> AsnPackages => Set<AsnPackageEntity>();
public DbSet<AsnPackageLineEntity> AsnPackageLines => Set<AsnPackageLineEntity>();
```

- [ ] Add model config in `OnModelCreating` — append before the closing `}`:

```csharp
// ── invoices ──────────────────────────────────────────────
modelBuilder.Entity<InvoiceEntity>(b =>
{
    b.ToTable("invoices");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.OrgId).HasColumnName("org_id");
    b.Property(x => x.SupplierId).HasColumnName("supplier_id");
    b.Property(x => x.BuyerId).HasColumnName("buyer_id");
    b.Property(x => x.InvoiceNumber).HasColumnName("invoice_number").IsRequired();
    b.Property(x => x.IssueDate).HasColumnName("issue_date");
    b.Property(x => x.DueDate).HasColumnName("due_date");
    b.Property(x => x.Currency).HasColumnName("currency").HasDefaultValue("EUR").IsRequired();
    b.Property(x => x.PaymentTerms).HasColumnName("payment_terms");
    b.Property(x => x.SubTotal).HasColumnName("sub_total").HasColumnType("numeric(18,4)");
    b.Property(x => x.TaxTotal).HasColumnName("tax_total").HasColumnType("numeric(18,4)");
    b.Property(x => x.GrandTotal).HasColumnName("grand_total").HasColumnType("numeric(18,4)");
    b.Property(x => x.Status).HasColumnName("status").HasDefaultValue("pending_review").IsRequired();
    b.Property(x => x.SourceFileName).HasColumnName("source_file_name");
    b.Property(x => x.SourceFileKey).HasColumnName("source_file_key");
    b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
    b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
    b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
    b.HasIndex(x => x.OrgId);
});

modelBuilder.Entity<InvoiceLineEntity>(b =>
{
    b.ToTable("invoice_lines");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.InvoiceId).HasColumnName("invoice_id");
    b.Property(x => x.LineNumber).HasColumnName("line_number");
    b.Property(x => x.Description).HasColumnName("description").IsRequired();
    b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(18,4)");
    b.Property(x => x.UnitCode).HasColumnName("unit_code").IsRequired();
    b.Property(x => x.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(18,4)");
    b.Property(x => x.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(8,4)");
    b.Property(x => x.LineTotal).HasColumnName("line_total").HasColumnType("numeric(18,4)");
    b.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code");
    b.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code");
    b.HasOne(x => x.Invoice).WithMany(x => x.Lines).HasForeignKey(x => x.InvoiceId);
});

modelBuilder.Entity<AdvanceShippingNoticeEntity>(b =>
{
    b.ToTable("advance_shipping_notices");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.OrgId).HasColumnName("org_id");
    b.Property(x => x.SupplierId).HasColumnName("supplier_id");
    b.Property(x => x.BuyerId).HasColumnName("buyer_id");
    b.Property(x => x.ShipmentId).HasColumnName("shipment_id").IsRequired();
    b.Property(x => x.DespatchDate).HasColumnName("despatch_date");
    b.Property(x => x.EstimatedDeliveryDate).HasColumnName("estimated_delivery_date");
    b.Property(x => x.BuyerOrderRef).HasColumnName("buyer_order_ref");
    b.Property(x => x.SupplierRef).HasColumnName("supplier_ref");
    b.Property(x => x.SourceFileName).HasColumnName("source_file_name");
    b.Property(x => x.SourceFileKey).HasColumnName("source_file_key");
    b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
    b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
    b.HasIndex(x => x.OrgId);
});

modelBuilder.Entity<AsnPackageEntity>(b =>
{
    b.ToTable("asn_packages");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.AsnId).HasColumnName("asn_id");
    b.Property(x => x.PackageId).HasColumnName("package_id").IsRequired();
    b.Property(x => x.Sscc).HasColumnName("sscc");
    b.HasOne(x => x.Asn).WithMany(x => x.Packages).HasForeignKey(x => x.AsnId);
});

modelBuilder.Entity<AsnPackageLineEntity>(b =>
{
    b.ToTable("asn_package_lines");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.PackageId).HasColumnName("package_id");
    b.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code");
    b.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code");
    b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(18,4)");
    b.Property(x => x.UnitCode).HasColumnName("unit_code").IsRequired();
    b.HasOne(x => x.Package).WithMany(x => x.Lines).HasForeignKey(x => x.PackageId);
});
```

- [ ] Generate migrations (requires running Postgres on :5435):
```
dotnet ef migrations add AddInvoicesAndLines --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
dotnet ef migrations add AddAdvanceShippingNotices --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```

- [ ] If Postgres unavailable, create migration files manually. The migration `Up()` for `AddInvoicesAndLines` must create:
  - `invoices` table with all columns from the model config above
  - `invoice_lines` table with all columns + FK to `invoices(id)`
  
  And `AddAdvanceShippingNotices` must create:
  - `advance_shipping_notices` table
  - `asn_packages` table + FK to `advance_shipping_notices(id)`
  - `asn_package_lines` table + FK to `asn_packages(id)`

- [ ] Build full solution:
```
dotnet build ProcuLink.slnx --no-restore
```

- [ ] Commit:
```
git add ProcuLink.Infrastructure/ProcuLinkDbContext.cs ProcuLink.Infrastructure/Migrations/
git commit -m "feat(wave3): register Invoice+ASN entities in DbContext, add EF migrations"
```

---

## Task 4: UblInvoiceParser (full implementation)

**Files:**
- Create: `ProcuLink.Transform/Parsing/UblInvoiceParser.cs`

UBL 2.1 Invoice namespace: `urn:oasis:names:specification:ubl:schema:xsd:Invoice-2`

Key element mappings:
- `cbc:ID` → `InvoiceNumber`
- `cbc:IssueDate` → `IssueDate`
- `cbc:DueDate` → `DueDate`
- `cbc:DocumentCurrencyCode` → `Currency`
- `cac:PaymentTerms/cbc:Note` → `PaymentTerms`
- `cac:AccountingSupplierParty/cac:Party/cac:PartyName/cbc:Name` → `SupplierRef`
- `cac:AccountingCustomerParty/cac:Party/cac:PartyName/cbc:Name` → `BuyerRef`
- `cac:TaxTotal/cbc:TaxAmount` → `TaxTotal`
- `cac:LegalMonetaryTotal/cbc:TaxExclusiveAmount` → `SubTotal`
- `cac:LegalMonetaryTotal/cbc:PayableAmount` → `GrandTotal`
- `cac:InvoiceLine` → lines array

Per line:
- `cbc:ID` → `LineNumber`
- `cbc:InvoicedQuantity` (with `@unitCode`) → `Quantity`, `UnitCode`
- `cbc:LineExtensionAmount` → `LineTotal`
- `cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory/cbc:Percent` → `TaxRate`
- `cac:Price/cbc:PriceAmount` → `UnitPrice`
- `cac:Item/cbc:Name` → `Description`
- `cac:Item/cac:BuyersItemIdentification/cbc:ID` → `BuyerItemCode`
- `cac:Item/cac:SellersItemIdentification/cbc:ID` → `SupplierItemCode`

- [ ] Create `ProcuLink.Transform/Parsing/UblInvoiceParser.cs` using the same `XDocument`/`XLinq` + local-name matching approach as `UblOrderParser`. Use the helper methods `GetChild`, `GetDescendant`, `GetAllDescendants`, `ParseDate`, `ParseDecimal`, `NullIfEmpty` — copy them verbatim from `UblOrderParser` into this class (they are private statics, no shared base class needed):

```csharp
using System.Globalization;
using System.Xml.Linq;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses UBL 2.1 Invoice documents into a <see cref="ParsedInvoice"/>.
/// Namespace: urn:oasis:names:specification:ubl:schema:xsd:Invoice-2
/// Also accepts bare root elements (no namespace) for test fixtures.
/// </summary>
public sealed class UblInvoiceParser : IInvoiceParser
{
    private const string UblInvoiceNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";

    public bool CanParse(string fileExtension, string? contentType = null) =>
        string.Equals(fileExtension, ".xml", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileExtension, ".ubl", StringComparison.OrdinalIgnoreCase);

    public static bool IsUblInvoiceDocument(Stream stream)
    {
        if (stream is null) return false;
        var originalPosition = stream.CanSeek ? stream.Position : -1L;
        try
        {
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                CloseInput = false, IgnoreWhitespace = true,
                IgnoreComments = true, DtdProcessing = System.Xml.DtdProcessing.Prohibit
            });
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                return string.Equals(reader.LocalName, "Invoice", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(reader.NamespaceURI, UblInvoiceNs, StringComparison.Ordinal);
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (stream.CanSeek && originalPosition >= 0) stream.Position = originalPosition;
        }
    }

    public async Task<ParsedInvoice> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        XDocument doc;
        try { doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, ct); }
        catch (Exception ex) { throw new InvoiceParseException($"UBL Invoice could not be parsed: {ex.Message}", ex); }

        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "Invoice", StringComparison.OrdinalIgnoreCase))
            throw new InvoiceParseException("Document root element is not <Invoice>.");

        var rootNs = root.Name.NamespaceName;
        if (!string.IsNullOrEmpty(rootNs) && !string.Equals(rootNs, UblInvoiceNs, StringComparison.Ordinal))
            throw new InvoiceParseException($"Root <Invoice> has namespace '{rootNs}', expected UBL 2.1 Invoice namespace.");

        var invoiceNumber = GetChild(root, "ID")?.Value?.Trim()
            ?? throw new InvoiceParseException("Required <cbc:ID> is missing on <Invoice>.");

        var issueDate = ParseDate(GetChild(root, "IssueDate")?.Value)
            ?? throw new InvoiceParseException("Required <cbc:IssueDate> is missing.");

        var dueDate = ParseDate(GetChild(root, "DueDate")?.Value);
        var currency = GetChild(root, "DocumentCurrencyCode")?.Value?.Trim() ?? "EUR";

        var paymentTermsEl = GetDescendant(root, "PaymentTerms");
        var paymentTerms = paymentTermsEl is null ? null : GetChild(paymentTermsEl, "Note")?.Value?.Trim();

        var supplierPartyEl = GetDescendant(root, "AccountingSupplierParty");
        var supplierRef = ExtractPartyName(supplierPartyEl);

        var customerPartyEl = GetDescendant(root, "AccountingCustomerParty");
        var buyerRef = ExtractPartyName(customerPartyEl);

        var taxTotalEl = GetDescendant(root, "TaxTotal");
        var taxTotal = ParseDecimal(taxTotalEl is null ? null : GetChild(taxTotalEl, "TaxAmount")?.Value) ?? 0m;

        var monetaryEl = GetDescendant(root, "LegalMonetaryTotal");
        var subTotal = ParseDecimal(monetaryEl is null ? null : GetChild(monetaryEl, "TaxExclusiveAmount")?.Value) ?? 0m;
        var grandTotal = ParseDecimal(monetaryEl is null ? null : GetChild(monetaryEl, "PayableAmount")?.Value) ?? 0m;

        var invoiceLineEls = GetAllDescendants(root, "InvoiceLine").ToList();
        if (invoiceLineEls.Count == 0)
            throw new InvoiceParseException("At least one <cac:InvoiceLine> is required.");

        var lines = new List<ParsedInvoiceLine>(invoiceLineEls.Count);
        int auto = 1;
        foreach (var lineEl in invoiceLineEls)
        {
            var lineIdStr = GetChild(lineEl, "ID")?.Value?.Trim();
            var lineNumber = int.TryParse(lineIdStr, out var ln) ? ln : auto;

            var qtyEl = GetChild(lineEl, "InvoicedQuantity");
            var quantity = ParseDecimal(qtyEl?.Value) ?? 0m;
            var unitCode = qtyEl?.Attribute("unitCode")?.Value?.Trim() ?? "EA";

            var lineTotal = ParseDecimal(GetChild(lineEl, "LineExtensionAmount")?.Value) ?? 0m;

            var taxSubEl = GetDescendant(lineEl, "TaxSubtotal");
            var taxCatEl = taxSubEl is null ? null : GetDescendant(taxSubEl, "TaxCategory");
            var taxRate = ParseDecimal(taxCatEl is null ? null : GetChild(taxCatEl, "Percent")?.Value) ?? 0m;

            var priceEl = GetChild(lineEl, "Price");
            var unitPrice = ParseDecimal(priceEl is null ? null : GetChild(priceEl, "PriceAmount")?.Value) ?? 0m;

            var itemEl = GetChild(lineEl, "Item")
                ?? throw new InvoiceParseException($"<cac:Item> missing on InvoiceLine #{lineNumber}.");
            var description = GetChild(itemEl, "Name")?.Value?.Trim() ?? string.Empty;

            var buyerIdEl = GetChild(itemEl, "BuyersItemIdentification");
            var buyerItemCode = buyerIdEl is null ? null : GetChild(buyerIdEl, "ID")?.Value?.Trim();

            var sellerIdEl = GetChild(itemEl, "SellersItemIdentification");
            var supplierItemCode = sellerIdEl is null ? null : GetChild(sellerIdEl, "ID")?.Value?.Trim();

            lines.Add(new ParsedInvoiceLine(
                LineNumber: lineNumber,
                Description: description,
                Quantity: quantity,
                UnitCode: unitCode,
                UnitPrice: unitPrice,
                TaxRate: taxRate,
                LineTotal: lineTotal,
                BuyerItemCode: NullIfEmpty(buyerItemCode),
                SupplierItemCode: NullIfEmpty(supplierItemCode)));
            auto++;
        }

        return new ParsedInvoice(
            InvoiceNumber: invoiceNumber,
            IssueDate: DateOnly.FromDateTime(issueDate),
            DueDate: dueDate.HasValue ? DateOnly.FromDateTime(dueDate.Value) : null,
            Currency: currency.ToUpperInvariant(),
            BuyerRef: NullIfEmpty(buyerRef),
            SupplierRef: NullIfEmpty(supplierRef),
            PaymentTerms: NullIfEmpty(paymentTerms),
            SubTotal: subTotal,
            TaxTotal: taxTotal,
            GrandTotal: grandTotal,
            Lines: lines);
    }

    private static string? ExtractPartyName(XElement? partyContainer)
    {
        if (partyContainer is null) return null;
        var partyEl = GetDescendant(partyContainer, "Party");
        if (partyEl is null) return null;
        var partyNameEl = GetChild(partyEl, "PartyName");
        var name = partyNameEl is null ? null : GetChild(partyNameEl, "Name")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;
        var legalEl = GetChild(partyEl, "PartyLegalEntity");
        return legalEl is null ? null : GetChild(legalEl, "RegistrationName")?.Value?.Trim();
    }

    private static XElement? GetChild(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static XElement? GetDescendant(XElement? parent, string localName) =>
        parent?.Descendants().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> GetAllDescendants(XElement parent, string localName) =>
        parent.Descendants().Where(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] formats = { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ? dt : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var n = value.Trim();
        if (n.Contains(',') && !n.Contains('.')) n = n.Replace(',', '.');
        return decimal.TryParse(n, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

- [ ] Build:
```
dotnet build ProcuLink.Transform/ProcuLink.Transform.csproj --no-restore
```

- [ ] Commit:
```
git add ProcuLink.Transform/Parsing/UblInvoiceParser.cs
git commit -m "feat(wave3): add UblInvoiceParser — full UBL 2.1 Invoice parser"
```

---

## Task 5: EDIFACT stubs + parser factories

**Files:**
- Create: `ProcuLink.Transform/Parsing/EdifactInvoiceParser.cs`
- Create: `ProcuLink.Transform/Parsing/EdifactDesadvParser.cs`
- Create: `ProcuLink.Transform/Parsing/InvoiceParserFactory.cs`
- Create: `ProcuLink.Transform/Parsing/DesadvParserFactory.cs`

- [ ] Create `ProcuLink.Transform/Parsing/EdifactInvoiceParser.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// EDIFACT INVOIC parser stub. Requires EdiFabric license — see docs/format-channel-roadmap.md §4.4.
/// Registered in DI so the factory can route .edi files; throws NotImplementedException until implemented.
/// </summary>
public sealed class EdifactInvoiceParser : IInvoiceParser
{
    private readonly ILogger<EdifactInvoiceParser> _logger;

    public EdifactInvoiceParser(ILogger<EdifactInvoiceParser> logger)
    {
        _logger = logger;
        _logger.LogWarning("EdifactInvoiceParser: EdiFabric license not yet configured. INVOIC parsing will throw NotImplementedException.");
    }

    public bool CanParse(string fileExtension, string? contentType = null) =>
        string.Equals(fileExtension, ".edi", StringComparison.OrdinalIgnoreCase)
        || string.Equals(contentType, "application/edifact", StringComparison.OrdinalIgnoreCase);

    public Task<ParsedInvoice> ParseAsync(Stream fileStream, CancellationToken ct) =>
        throw new NotImplementedException(
            "EDIFACT INVOIC parsing requires an EdiFabric license. See docs/format-channel-roadmap.md §4.4.");
}
```

- [ ] Create `ProcuLink.Transform/Parsing/EdifactDesadvParser.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// EDIFACT DESADV (ASN) parser stub. Requires EdiFabric license — see docs/format-channel-roadmap.md §4.4.
/// </summary>
public sealed class EdifactDesadvParser : IDesadvParser
{
    private readonly ILogger<EdifactDesadvParser> _logger;

    public EdifactDesadvParser(ILogger<EdifactDesadvParser> logger)
    {
        _logger = logger;
        _logger.LogWarning("EdifactDesadvParser: EdiFabric license not yet configured. DESADV parsing will throw NotImplementedException.");
    }

    public bool CanParse(string fileExtension, string? contentType = null) =>
        string.Equals(fileExtension, ".edi", StringComparison.OrdinalIgnoreCase)
        || string.Equals(contentType, "application/edifact", StringComparison.OrdinalIgnoreCase);

    public Task<ParsedAsn> ParseAsync(Stream fileStream, CancellationToken ct) =>
        throw new NotImplementedException(
            "EDIFACT DESADV parsing requires an EdiFabric license. See docs/format-channel-roadmap.md §4.4.");
}
```

- [ ] Create `ProcuLink.Transform/Parsing/InvoiceParserFactory.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

public sealed class InvoiceParserFactory
{
    private readonly IEnumerable<IInvoiceParser> _parsers;

    public InvoiceParserFactory(IEnumerable<IInvoiceParser> parsers)
        => _parsers = parsers;

    public IInvoiceParser GetParser(string fileExtension, Stream? peek = null)
    {
        var ext = (fileExtension ?? string.Empty).ToLowerInvariant();

        // Disambiguate .xml — UBL Invoice takes priority when namespace matches
        if (ext == ".xml" && peek?.CanSeek == true)
        {
            if (UblInvoiceParser.IsUblInvoiceDocument(peek))
            {
                var ubl = _parsers.OfType<UblInvoiceParser>().FirstOrDefault();
                if (ubl is not null) return ubl;
            }
        }

        var parser = _parsers.FirstOrDefault(p => p.CanParse(ext));
        if (parser is null)
            throw new UnsupportedFileFormatException(fileExtension);

        return parser;
    }
}
```

- [ ] Create `ProcuLink.Transform/Parsing/DesadvParserFactory.cs`:

```csharp
namespace ProcuLink.Transform.Parsing;

public sealed class DesadvParserFactory
{
    private readonly IEnumerable<IDesadvParser> _parsers;

    public DesadvParserFactory(IEnumerable<IDesadvParser> parsers)
        => _parsers = parsers;

    public IDesadvParser GetParser(string fileExtension)
    {
        var ext = (fileExtension ?? string.Empty).ToLowerInvariant();
        var parser = _parsers.FirstOrDefault(p => p.CanParse(ext));
        if (parser is null)
            throw new UnsupportedFileFormatException(fileExtension);
        return parser;
    }
}
```

- [ ] Build and commit:
```
dotnet build ProcuLink.Transform/ProcuLink.Transform.csproj --no-restore
git add ProcuLink.Transform/Parsing/EdifactInvoiceParser.cs ProcuLink.Transform/Parsing/EdifactDesadvParser.cs ProcuLink.Transform/Parsing/InvoiceParserFactory.cs ProcuLink.Transform/Parsing/DesadvParserFactory.cs
git commit -m "feat(wave3): add EDIFACT Invoice+DESADV stubs and parser factories"
```

---

## Task 6: Invoice output transformers

**Files:**
- Create: `ProcuLink.Core/Services/IInvoiceTransformService.cs`
- Create: `ProcuLink.Transform/Output/CsvInvoiceTransformService.cs`
- Create: `ProcuLink.Transform/Output/XmlInvoiceTransformService.cs`
- Create: `ProcuLink.Transform/Output/JsonInvoiceTransformService.cs`
- Create: `ProcuLink.Transform/Output/IDesadvTransformService.cs` (stub interface only)

- [ ] Create `ProcuLink.Core/Services/IInvoiceTransformService.cs`:

```csharp
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IInvoiceTransformService
{
    string Format { get; }  // "csv" | "xml" | "json"
    Task<byte[]> TransformAsync(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, CancellationToken ct);
}
```

- [ ] Create `ProcuLink.Transform/Output/CsvInvoiceTransformService.cs`:

```csharp
using System.Globalization;
using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

public sealed class CsvInvoiceTransformService : IInvoiceTransformService
{
    public string Format => "csv";

    public Task<byte[]> TransformAsync(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"InvoiceNumber,{Esc(invoice.InvoiceNumber)}");
        sb.AppendLine($"IssueDate,{invoice.IssueDate:yyyy-MM-dd}");
        if (invoice.DueDate.HasValue) sb.AppendLine($"DueDate,{invoice.DueDate:yyyy-MM-dd}");
        sb.AppendLine($"Currency,{Esc(invoice.Currency)}");
        sb.AppendLine($"SubTotal,{invoice.SubTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"TaxTotal,{invoice.TaxTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"GrandTotal,{invoice.GrandTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("LineNumber,Description,Quantity,UnitCode,UnitPrice,TaxRate,LineTotal,BuyerItemCode,SupplierItemCode");

        foreach (var l in lines.OrderBy(x => x.LineNumber))
        {
            sb.AppendLine(string.Join(",",
                l.LineNumber,
                Esc(l.Description),
                l.Quantity.ToString(CultureInfo.InvariantCulture),
                Esc(l.UnitCode),
                l.UnitPrice.ToString(CultureInfo.InvariantCulture),
                l.TaxRate.ToString(CultureInfo.InvariantCulture),
                l.LineTotal.ToString(CultureInfo.InvariantCulture),
                Esc(l.BuyerItemCode ?? string.Empty),
                Esc(l.SupplierItemCode ?? string.Empty)));
        }

        return Task.FromResult(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Esc(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;
}
```

- [ ] Create `ProcuLink.Transform/Output/XmlInvoiceTransformService.cs`:

```csharp
using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

public sealed class XmlInvoiceTransformService : IInvoiceTransformService
{
    public string Format => "xml";

    public Task<byte[]> TransformAsync(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, CancellationToken ct)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Invoice",
                new XElement("InvoiceNumber", invoice.InvoiceNumber),
                new XElement("IssueDate", invoice.IssueDate.ToString("yyyy-MM-dd")),
                invoice.DueDate.HasValue ? new XElement("DueDate", invoice.DueDate.Value.ToString("yyyy-MM-dd")) : null!,
                new XElement("Currency", invoice.Currency),
                invoice.PaymentTerms is not null ? new XElement("PaymentTerms", invoice.PaymentTerms) : null!,
                new XElement("SubTotal", invoice.SubTotal),
                new XElement("TaxTotal", invoice.TaxTotal),
                new XElement("GrandTotal", invoice.GrandTotal),
                new XElement("Lines",
                    lines.OrderBy(l => l.LineNumber).Select(l =>
                        new XElement("Line",
                            new XElement("LineNumber", l.LineNumber),
                            new XElement("Description", l.Description),
                            new XElement("Quantity", l.Quantity),
                            new XElement("UnitCode", l.UnitCode),
                            new XElement("UnitPrice", l.UnitPrice),
                            new XElement("TaxRate", l.TaxRate),
                            new XElement("LineTotal", l.LineTotal),
                            l.BuyerItemCode is not null ? new XElement("BuyerItemCode", l.BuyerItemCode) : null!,
                            l.SupplierItemCode is not null ? new XElement("SupplierItemCode", l.SupplierItemCode) : null!)))));

        return Task.FromResult(Encoding.UTF8.GetBytes(doc.ToString()));
    }
}
```

- [ ] Create `ProcuLink.Transform/Output/JsonInvoiceTransformService.cs`:

```csharp
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

public sealed class JsonInvoiceTransformService : IInvoiceTransformService
{
    public string Format => "json";

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public Task<byte[]> TransformAsync(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, CancellationToken ct)
    {
        var payload = new
        {
            invoiceNumber = invoice.InvoiceNumber,
            issueDate = invoice.IssueDate.ToString("yyyy-MM-dd"),
            dueDate = invoice.DueDate?.ToString("yyyy-MM-dd"),
            currency = invoice.Currency,
            paymentTerms = invoice.PaymentTerms,
            subTotal = invoice.SubTotal,
            taxTotal = invoice.TaxTotal,
            grandTotal = invoice.GrandTotal,
            lines = lines.OrderBy(l => l.LineNumber).Select(l => new
            {
                lineNumber = l.LineNumber,
                description = l.Description,
                quantity = l.Quantity,
                unitCode = l.UnitCode,
                unitPrice = l.UnitPrice,
                taxRate = l.TaxRate,
                lineTotal = l.LineTotal,
                buyerItemCode = l.BuyerItemCode,
                supplierItemCode = l.SupplierItemCode
            }).ToArray()
        };

        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(payload, _opts));
    }
}
```

- [ ] Add `IDesadvTransformService` stub to `ProcuLink.Core/Services/IDesadvTransformService.cs`:

```csharp
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IDesadvTransformService
{
    string Format { get; }
    Task<byte[]> TransformAsync(AdvanceShippingNoticeEntity asn, IReadOnlyList<AsnPackageEntity> packages, CancellationToken ct);
}
```

- [ ] Build and commit:
```
dotnet build ProcuLink.slnx --no-restore
git add ProcuLink.Core/Services/IInvoiceTransformService.cs ProcuLink.Core/Services/IDesadvTransformService.cs ProcuLink.Transform/Output/CsvInvoiceTransformService.cs ProcuLink.Transform/Output/XmlInvoiceTransformService.cs ProcuLink.Transform/Output/JsonInvoiceTransformService.cs
git commit -m "feat(wave3): add Invoice transform services (CSV/XML/JSON) and IDesadvTransformService stub"
```

---

## Task 7: IInvoiceService + InvoiceService + IDesadvService stub

**Files:**
- Create: `ProcuLink.Core/Services/IInvoiceService.cs`
- Create: `ProcuLink.Core/Services/IDesadvService.cs`
- Create: `ProcuLink.Infrastructure/Services/InvoiceService.cs`

- [ ] Create `ProcuLink.Core/Services/IInvoiceService.cs`:

```csharp
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IInvoiceService
{
    Task<InvoiceEntity> CreateStubAsync(Guid orgId, Guid? supplierId, Stream stream, string fileName, string contentType, CancellationToken ct);
    Task<InvoiceEntity?> GetAsync(Guid orgId, Guid invoiceId, CancellationToken ct);
    Task<IReadOnlyList<InvoiceEntity>> ListAsync(Guid orgId, CancellationToken ct);
    Task<InvoiceEntity> ApproveAsync(Guid orgId, Guid invoiceId, CancellationToken ct);
    Task<byte[]> ForwardAsync(Guid orgId, Guid invoiceId, string outputFormat, CancellationToken ct);
    Task PersistParsedAsync(Guid orgId, Guid invoiceId, ParsedInvoiceData data, CancellationToken ct);
}

public sealed record ParsedInvoiceData(
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
    IReadOnlyList<ParsedInvoiceLineData> Lines);

public sealed record ParsedInvoiceLineData(
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

- [ ] Create `ProcuLink.Core/Services/IDesadvService.cs`:

```csharp
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IDesadvService
{
    Task<AdvanceShippingNoticeEntity> CreateStubAsync(Guid orgId, Guid? supplierId, Stream stream, string fileName, string contentType, CancellationToken ct);
    Task<AdvanceShippingNoticeEntity?> GetAsync(Guid orgId, Guid asnId, CancellationToken ct);
    Task<IReadOnlyList<AdvanceShippingNoticeEntity>> ListAsync(Guid orgId, CancellationToken ct);
}
```

- [ ] Create `ProcuLink.Infrastructure/Services/InvoiceService.cs`. This service must:
  - Use `ProcuLinkDbContext` and `IFileStorageService` (same as OrderService pattern)
  - Scope all EF queries with `OrgId == orgId`
  - `CreateStubAsync`: store file to storage (use `IFileStorageService.UploadAsync`), create `InvoiceEntity` with `Status = "pending_review"`, save, return
  - `PersistParsedAsync`: load invoice by id+orgId, update all parsed fields + lines, save
  - `ForwardAsync`: load invoice + lines, resolve `IInvoiceTransformService` by `outputFormat`, call `TransformAsync`, return bytes
  - `GetAsync`/`ListAsync`: EF queries with org filter, include Lines for `GetAsync`
  - `ApproveAsync`: set status to `"approved"`, save, return

```csharp
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class InvoiceService : IInvoiceService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IEnumerable<IInvoiceTransformService> _transformers;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(
        ProcuLinkDbContext db,
        IFileStorageService storage,
        IEnumerable<IInvoiceTransformService> transformers,
        ILogger<InvoiceService> logger)
    {
        _db = db;
        _storage = storage;
        _transformers = transformers;
        _logger = logger;
    }

    public async Task<InvoiceEntity> CreateStubAsync(Guid orgId, Guid? supplierId, Stream stream, string fileName, string contentType, CancellationToken ct)
    {
        var key = $"invoices/{orgId}/{Guid.NewGuid()}/{fileName}";
        await _storage.UploadAsync(key, stream, contentType, ct);

        var invoice = new InvoiceEntity
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            InvoiceNumber = string.Empty,
            IssueDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = "pending_review",
            SourceFileName = fileName,
            SourceFileKey = key,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task PersistParsedAsync(Guid orgId, Guid invoiceId, ParsedInvoiceData data, CancellationToken ct)
    {
        var invoice = await _db.Invoices
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == invoiceId && x.OrgId == orgId, ct)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found for org {orgId}");

        invoice.InvoiceNumber = data.InvoiceNumber;
        invoice.IssueDate = data.IssueDate;
        invoice.DueDate = data.DueDate;
        invoice.Currency = data.Currency;
        invoice.BuyerRef = data.BuyerRef;
        invoice.SupplierRef = data.SupplierRef;
        invoice.PaymentTerms = data.PaymentTerms;
        invoice.SubTotal = data.SubTotal;
        invoice.TaxTotal = data.TaxTotal;
        invoice.GrandTotal = data.GrandTotal;
        invoice.UpdatedAt = DateTime.UtcNow;

        _db.InvoiceLines.RemoveRange(invoice.Lines);

        foreach (var l in data.Lines)
        {
            _db.InvoiceLines.Add(new InvoiceLineEntity
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                LineNumber = l.LineNumber,
                Description = l.Description,
                Quantity = l.Quantity,
                UnitCode = l.UnitCode,
                UnitPrice = l.UnitPrice,
                TaxRate = l.TaxRate,
                LineTotal = l.LineTotal,
                BuyerItemCode = l.BuyerItemCode,
                SupplierItemCode = l.SupplierItemCode
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<InvoiceEntity?> GetAsync(Guid orgId, Guid invoiceId, CancellationToken ct) =>
        await _db.Invoices
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == invoiceId && x.OrgId == orgId, ct);

    public async Task<IReadOnlyList<InvoiceEntity>> ListAsync(Guid orgId, CancellationToken ct) =>
        await _db.Invoices
            .Where(x => x.OrgId == orgId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

    public async Task<InvoiceEntity> ApproveAsync(Guid orgId, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await _db.Invoices
            .FirstOrDefaultAsync(x => x.Id == invoiceId && x.OrgId == orgId, ct)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");

        invoice.Status = "approved";
        invoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<byte[]> ForwardAsync(Guid orgId, Guid invoiceId, string outputFormat, CancellationToken ct)
    {
        var invoice = await _db.Invoices
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == invoiceId && x.OrgId == orgId, ct)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");

        var transformer = _transformers.FirstOrDefault(t =>
            string.Equals(t.Format, outputFormat, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No invoice transformer for format '{outputFormat}'");

        return await transformer.TransformAsync(invoice, invoice.Lines, ct);
    }
}
```

Note: `InvoiceEntity` needs `BuyerRef` and `SupplierRef` properties — add them to the entity class created in Task 2:
```csharp
public string? BuyerRef { get; set; }
public string? SupplierRef { get; set; }
```
And add corresponding column mappings in DbContext (`buyer_ref`, `supplier_ref`).

Also add a simple `DesadvService` stub:

```csharp
// ProcuLink.Infrastructure/Services/DesadvService.cs
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class DesadvService : IDesadvService
{
    private readonly ProcuLinkDbContext _db;

    public DesadvService(ProcuLinkDbContext db) => _db = db;

    public async Task<AdvanceShippingNoticeEntity> CreateStubAsync(
        Guid orgId, Guid? supplierId, Stream stream, string fileName, string contentType, CancellationToken ct)
    {
        var asn = new AdvanceShippingNoticeEntity
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            ShipmentId = string.Empty, DespatchDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SourceFileName = fileName, CreatedAt = DateTime.UtcNow
        };
        _db.AdvanceShippingNotices.Add(asn);
        await _db.SaveChangesAsync(ct);
        return asn;
    }

    public async Task<AdvanceShippingNoticeEntity?> GetAsync(Guid orgId, Guid asnId, CancellationToken ct) =>
        await _db.AdvanceShippingNotices
            .Include(x => x.Packages).ThenInclude(p => p.Lines)
            .FirstOrDefaultAsync(x => x.Id == asnId && x.OrgId == orgId, ct);

    public async Task<IReadOnlyList<AdvanceShippingNoticeEntity>> ListAsync(Guid orgId, CancellationToken ct) =>
        await _db.AdvanceShippingNotices
            .Where(x => x.OrgId == orgId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
}
```

- [ ] Build and commit:
```
dotnet build ProcuLink.slnx --no-restore
git add ProcuLink.Core/Services/IInvoiceService.cs ProcuLink.Core/Services/IDesadvService.cs ProcuLink.Infrastructure/Services/InvoiceService.cs ProcuLink.Infrastructure/Services/DesadvService.cs
git commit -m "feat(wave3): add IInvoiceService, InvoiceService, IDesadvService, DesadvService"
```

---

## Task 8: ParseInvoiceJob Hangfire job

**Files:**
- Create: `ProcuLink.Api/Jobs/ParseInvoiceJob.cs`

- [ ] Create `ProcuLink.Api/Jobs/ParseInvoiceJob.cs` — mirrors `ParseOrderJob` exactly:

```csharp
using Hangfire;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Storage;
using ProcuLink.Transform.Parsing;
using Microsoft.EntityFrameworkCore;

namespace ProcuLink.Api.Jobs;

public sealed class ParseInvoiceJob
{
    private readonly IInvoiceService _invoices;
    private readonly IFileStorageService _storage;
    private readonly InvoiceParserFactory _factory;
    private readonly ILogger<ParseInvoiceJob> _logger;

    public ParseInvoiceJob(
        IInvoiceService invoices,
        IFileStorageService storage,
        InvoiceParserFactory factory,
        ILogger<ParseInvoiceJob> logger)
    {
        _invoices = invoices;
        _storage = storage;
        _factory = factory;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public async Task ExecuteAsync(Guid invoiceId, Guid organisationId, CancellationToken ct)
    {
        _logger.LogInformation("ParseInvoiceJob starting for invoice {InvoiceId}", invoiceId);

        var invoice = await _invoices.GetAsync(organisationId, invoiceId, ct);
        if (invoice is null)
        {
            _logger.LogWarning("ParseInvoiceJob: invoice {InvoiceId} not found", invoiceId);
            return;
        }

        if (string.IsNullOrWhiteSpace(invoice.SourceFileKey))
        {
            _logger.LogWarning("ParseInvoiceJob: no source file key for invoice {InvoiceId}", invoiceId);
            return;
        }

        try
        {
            var ext = Path.GetExtension(invoice.SourceFileName ?? string.Empty);
            using var stream = await _storage.DownloadAsync(invoice.SourceFileKey, ct);
            var parser = _factory.GetParser(ext, stream);
            var parsed = await parser.ParseAsync(stream, ct);

            await _invoices.PersistParsedAsync(organisationId, invoiceId,
                new ParsedInvoiceData(
                    InvoiceNumber: parsed.InvoiceNumber,
                    IssueDate: parsed.IssueDate,
                    DueDate: parsed.DueDate,
                    Currency: parsed.Currency,
                    BuyerRef: parsed.BuyerRef,
                    SupplierRef: parsed.SupplierRef,
                    PaymentTerms: parsed.PaymentTerms,
                    SubTotal: parsed.SubTotal,
                    TaxTotal: parsed.TaxTotal,
                    GrandTotal: parsed.GrandTotal,
                    Lines: parsed.Lines.Select(l => new ParsedInvoiceLineData(
                        LineNumber: l.LineNumber,
                        Description: l.Description,
                        Quantity: l.Quantity,
                        UnitCode: l.UnitCode,
                        UnitPrice: l.UnitPrice,
                        TaxRate: l.TaxRate,
                        LineTotal: l.LineTotal,
                        BuyerItemCode: l.BuyerItemCode,
                        SupplierItemCode: l.SupplierItemCode)).ToList()),
                ct);

            _logger.LogInformation("ParseInvoiceJob completed for invoice {InvoiceId}", invoiceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ParseInvoiceJob failed for invoice {InvoiceId}", invoiceId);
            throw;
        }
    }

    public static void Enqueue(IBackgroundJobClient jobs, Guid invoiceId, Guid organisationId) =>
        jobs.Enqueue<ParseInvoiceJob>(j => j.ExecuteAsync(invoiceId, organisationId, CancellationToken.None));
}
```

- [ ] Build and commit:
```
dotnet build ProcuLink.slnx --no-restore
git add ProcuLink.Api/Jobs/ParseInvoiceJob.cs
git commit -m "feat(wave3): add ParseInvoiceJob Hangfire job"
```

---

## Task 9: InvoiceController + DesadvController + DI registration

**Files:**
- Create: `ProcuLink.Api/Controllers/InvoiceController.cs`
- Create: `ProcuLink.Api/Controllers/DesadvController.cs`
- Modify: `ProcuLink.Api/Program.cs`

- [ ] Create `ProcuLink.Api/Controllers/InvoiceController.cs`:

```csharp
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/invoices")]
public sealed class InvoiceController : ControllerBase
{
    private readonly IInvoiceService _invoices;
    private readonly ICurrentTenantService _tenant;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<InvoiceController> _logger;

    public InvoiceController(
        IInvoiceService invoices,
        ICurrentTenantService tenant,
        IBackgroundJobClient jobs,
        ILogger<InvoiceController> logger)
    {
        _invoices = invoices;
        _tenant = tenant;
        _jobs = jobs;
        _logger = logger;
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] Guid? supplierId, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "File is required." });

        var allowed = new[] { ".xml", ".ubl", ".edi" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest(new { error = $"Unsupported file type '{ext}'. Allowed: {string.Join(", ", allowed)}" });

        var orgId = await _tenant.GetCurrentOrgIdAsync();
        using var stream = file.OpenReadStream();

        var invoice = await _invoices.CreateStubAsync(orgId, supplierId, stream, file.FileName, file.ContentType, ct);
        ParseInvoiceJob.Enqueue(_jobs, invoice.Id, orgId);

        return Ok(new { invoiceId = invoice.Id, status = invoice.Status });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = await _tenant.GetCurrentOrgIdAsync();
        var invoices = await _invoices.ListAsync(orgId, ct);
        return Ok(invoices.Select(i => new
        {
            i.Id, i.InvoiceNumber, i.IssueDate, i.DueDate, i.Currency,
            i.GrandTotal, i.Status, i.CreatedAt
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var orgId = await _tenant.GetCurrentOrgIdAsync();
        var invoice = await _invoices.GetAsync(orgId, id, ct);
        if (invoice is null) return NotFound();
        return Ok(invoice);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var orgId = await _tenant.GetCurrentOrgIdAsync();
        var invoice = await _invoices.ApproveAsync(orgId, id, ct);
        return Ok(new { invoice.Id, invoice.Status });
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        var allowed = new[] { "csv", "xml", "json" };
        if (!allowed.Contains(format.ToLowerInvariant()))
            return BadRequest(new { error = $"Unsupported format '{format}'. Allowed: csv, xml, json" });

        var orgId = await _tenant.GetCurrentOrgIdAsync();
        var bytes = await _invoices.ForwardAsync(orgId, id, format, ct);

        var contentType = format switch
        {
            "csv" => "text/csv",
            "xml" => "application/xml",
            "json" => "application/json",
            _ => "application/octet-stream"
        };
        return File(bytes, contentType, $"invoice-{id}.{format}");
    }
}
```

- [ ] Create `ProcuLink.Api/Controllers/DesadvController.cs` — stub returning 501:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/asn")]
public sealed class DesadvController : ControllerBase
{
    [HttpPost("upload")]
    public IActionResult Upload() =>
        StatusCode(501, new { error = "ASN/DESADV ingress requires EdiFabric license. See docs/format-channel-roadmap.md §4.4." });

    [HttpGet]
    public IActionResult List() =>
        StatusCode(501, new { error = "ASN/DESADV support is not yet implemented." });

    [HttpGet("{id:guid}")]
    public IActionResult Get(Guid id) =>
        StatusCode(501, new { error = "ASN/DESADV support is not yet implemented." });
}
```

- [ ] Register all Wave 3 services in `Program.cs` — add after existing service registrations:

```csharp
// ── Wave 3: Invoice parsers ───────────────────────────────────────────────
builder.Services.AddSingleton<UblInvoiceParser>();
builder.Services.AddSingleton<EdifactInvoiceParser>();
builder.Services.AddSingleton<IInvoiceParser, UblInvoiceParser>(sp => sp.GetRequiredService<UblInvoiceParser>());
builder.Services.AddSingleton<IInvoiceParser, EdifactInvoiceParser>(sp => sp.GetRequiredService<EdifactInvoiceParser>());
builder.Services.AddSingleton<InvoiceParserFactory>();
builder.Services.AddSingleton<EdifactDesadvParser>();
builder.Services.AddSingleton<IDesadvParser, EdifactDesadvParser>(sp => sp.GetRequiredService<EdifactDesadvParser>());
builder.Services.AddSingleton<DesadvParserFactory>();

// ── Wave 3: Invoice transform services ───────────────────────────────────
builder.Services.AddSingleton<IInvoiceTransformService, CsvInvoiceTransformService>();
builder.Services.AddSingleton<IInvoiceTransformService, XmlInvoiceTransformService>();
builder.Services.AddSingleton<IInvoiceTransformService, JsonInvoiceTransformService>();

// ── Wave 3: Invoice + DESADV application services ────────────────────────
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IDesadvService, DesadvService>();
```

Add required `using` statements at top of Program.cs:
```csharp
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using ProcuLink.Transform.Output;
```

- [ ] Build full solution:
```
dotnet build ProcuLink.slnx --no-restore
```
Expected: 0 errors.

- [ ] Commit:
```
git add ProcuLink.Api/Controllers/InvoiceController.cs ProcuLink.Api/Controllers/DesadvController.cs ProcuLink.Api/Program.cs
git commit -m "feat(wave3): add InvoiceController, DesadvController stub, DI registration"
```

---

## Task 10: Tests

**Files:**
- Create: `ProcuLink.Transform.Tests/Parsing/UblInvoiceParserTests.cs`
- Create: `ProcuLink.Transform.Tests/Parsing/EdifactInvoiceParserTests.cs`
- Create: `ProcuLink.Transform.Tests/Output/CsvInvoiceTransformServiceTests.cs`
- Create: `ProcuLink.Infrastructure.Tests/Services/InvoiceServiceTests.cs`

- [ ] Create test fixture XML — minimal valid UBL 2.1 Invoice (place inline in test as a string constant):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
         xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
         xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
  <cbc:ID>INV-001</cbc:ID>
  <cbc:IssueDate>2026-05-28</cbc:IssueDate>
  <cbc:DueDate>2026-06-28</cbc:DueDate>
  <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
  <cac:AccountingSupplierParty><cac:Party><cac:PartyName><cbc:Name>Acme Supplier</cbc:Name></cac:PartyName></cac:Party></cac:AccountingSupplierParty>
  <cac:AccountingCustomerParty><cac:Party><cac:PartyName><cbc:Name>Buyer Co</cbc:Name></cac:PartyName></cac:Party></cac:AccountingCustomerParty>
  <cac:TaxTotal><cbc:TaxAmount currencyID="EUR">20.00</cbc:TaxAmount></cac:TaxTotal>
  <cac:LegalMonetaryTotal>
    <cbc:TaxExclusiveAmount currencyID="EUR">100.00</cbc:TaxExclusiveAmount>
    <cbc:PayableAmount currencyID="EUR">120.00</cbc:PayableAmount>
  </cac:LegalMonetaryTotal>
  <cac:InvoiceLine>
    <cbc:ID>1</cbc:ID>
    <cbc:InvoicedQuantity unitCode="EA">2</cbc:InvoicedQuantity>
    <cbc:LineExtensionAmount currencyID="EUR">100.00</cbc:LineExtensionAmount>
    <cac:TaxTotal><cac:TaxSubtotal><cac:TaxCategory><cbc:Percent>20</cbc:Percent></cac:TaxCategory></cac:TaxSubtotal></cac:TaxTotal>
    <cac:Price><cbc:PriceAmount currencyID="EUR">50.00</cbc:PriceAmount></cac:Price>
    <cac:Item>
      <cbc:Name>Widget A</cbc:Name>
      <cac:BuyersItemIdentification><cbc:ID>BUYER-001</cbc:ID></cac:BuyersItemIdentification>
      <cac:SellersItemIdentification><cbc:ID>SUP-001</cbc:ID></cac:SellersItemIdentification>
    </cac:Item>
  </cac:InvoiceLine>
</Invoice>
```

- [ ] Create `ProcuLink.Transform.Tests/Parsing/UblInvoiceParserTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Transform.Tests.Parsing;

public class UblInvoiceParserTests
{
    private const string MinimalUblInvoice = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
                 xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                 xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
          <cbc:ID>INV-001</cbc:ID>
          <cbc:IssueDate>2026-05-28</cbc:IssueDate>
          <cbc:DueDate>2026-06-28</cbc:DueDate>
          <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cac:AccountingSupplierParty><cac:Party><cac:PartyName><cbc:Name>Acme Supplier</cbc:Name></cac:PartyName></cac:Party></cac:AccountingSupplierParty>
          <cac:AccountingCustomerParty><cac:Party><cac:PartyName><cbc:Name>Buyer Co</cbc:Name></cac:PartyName></cac:Party></cac:AccountingCustomerParty>
          <cac:TaxTotal><cbc:TaxAmount currencyID="EUR">20.00</cbc:TaxAmount></cac:TaxTotal>
          <cac:LegalMonetaryTotal>
            <cbc:TaxExclusiveAmount currencyID="EUR">100.00</cbc:TaxExclusiveAmount>
            <cbc:PayableAmount currencyID="EUR">120.00</cbc:PayableAmount>
          </cac:LegalMonetaryTotal>
          <cac:InvoiceLine>
            <cbc:ID>1</cbc:ID>
            <cbc:InvoicedQuantity unitCode="EA">2</cbc:InvoicedQuantity>
            <cbc:LineExtensionAmount currencyID="EUR">100.00</cbc:LineExtensionAmount>
            <cac:TaxTotal><cac:TaxSubtotal><cac:TaxCategory><cbc:Percent>20</cbc:Percent></cac:TaxCategory></cac:TaxSubtotal></cac:TaxTotal>
            <cac:Price><cbc:PriceAmount currencyID="EUR">50.00</cbc:PriceAmount></cac:Price>
            <cac:Item>
              <cbc:Name>Widget A</cbc:Name>
              <cac:BuyersItemIdentification><cbc:ID>BUYER-001</cbc:ID></cac:BuyersItemIdentification>
              <cac:SellersItemIdentification><cbc:ID>SUP-001</cbc:ID></cac:SellersItemIdentification>
            </cac:Item>
          </cac:InvoiceLine>
        </Invoice>
        """;

    private static Stream ToStream(string xml) =>
        new MemoryStream(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void CanParse_Xml_ReturnsTrue() =>
        new UblInvoiceParser().CanParse(".xml").Should().BeTrue();

    [Fact]
    public void CanParse_Csv_ReturnsFalse() =>
        new UblInvoiceParser().CanParse(".csv").Should().BeFalse();

    [Fact]
    public async Task ParseAsync_MinimalInvoice_ReturnsCorrectHeader()
    {
        var parser = new UblInvoiceParser();
        var result = await parser.ParseAsync(ToStream(MinimalUblInvoice), CancellationToken.None);

        result.InvoiceNumber.Should().Be("INV-001");
        result.IssueDate.Should().Be(new DateOnly(2026, 5, 28));
        result.DueDate.Should().Be(new DateOnly(2026, 6, 28));
        result.Currency.Should().Be("EUR");
        result.TaxTotal.Should().Be(20.00m);
        result.SubTotal.Should().Be(100.00m);
        result.GrandTotal.Should().Be(120.00m);
        result.SupplierRef.Should().Be("Acme Supplier");
        result.BuyerRef.Should().Be("Buyer Co");
    }

    [Fact]
    public async Task ParseAsync_MinimalInvoice_ReturnsCorrectLine()
    {
        var parser = new UblInvoiceParser();
        var result = await parser.ParseAsync(ToStream(MinimalUblInvoice), CancellationToken.None);

        result.Lines.Should().HaveCount(1);
        var line = result.Lines[0];
        line.LineNumber.Should().Be(1);
        line.Description.Should().Be("Widget A");
        line.Quantity.Should().Be(2m);
        line.UnitCode.Should().Be("EA");
        line.UnitPrice.Should().Be(50.00m);
        line.TaxRate.Should().Be(20m);
        line.LineTotal.Should().Be(100.00m);
        line.BuyerItemCode.Should().Be("BUYER-001");
        line.SupplierItemCode.Should().Be("SUP-001");
    }

    [Fact]
    public async Task ParseAsync_WrongRoot_ThrowsInvoiceParseException()
    {
        var xml = "<Order xmlns='urn:oasis:names:specification:ubl:schema:xsd:Order-2'><ID>1</ID></Order>";
        var parser = new UblInvoiceParser();
        await Assert.ThrowsAsync<InvoiceParseException>(() =>
            parser.ParseAsync(ToStream(xml), CancellationToken.None));
    }
}
```

- [ ] Create `ProcuLink.Transform.Tests/Parsing/EdifactInvoiceParserTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Transform.Tests.Parsing;

public class EdifactInvoiceParserTests
{
    [Fact]
    public void CanParse_Edi_ReturnsTrue() =>
        new EdifactInvoiceParser(NullLogger<EdifactInvoiceParser>.Instance).CanParse(".edi").Should().BeTrue();

    [Fact]
    public async Task ParseAsync_AlwaysThrowsNotImplementedException()
    {
        var parser = new EdifactInvoiceParser(NullLogger<EdifactInvoiceParser>.Instance);
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            parser.ParseAsync(stream, CancellationToken.None));
    }
}
```

- [ ] Create `ProcuLink.Transform.Tests/Output/CsvInvoiceTransformServiceTests.cs`:

```csharp
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using System.Text;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class CsvInvoiceTransformServiceTests
{
    [Fact]
    public async Task TransformAsync_ProducesHeaderAndLine()
    {
        var invoice = new InvoiceEntity
        {
            Id = Guid.NewGuid(), OrgId = Guid.NewGuid(),
            InvoiceNumber = "INV-001", IssueDate = new DateOnly(2026, 5, 28),
            Currency = "EUR", SubTotal = 100m, TaxTotal = 20m, GrandTotal = 120m,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var lines = new List<InvoiceLineEntity>
        {
            new() { Id = Guid.NewGuid(), InvoiceId = invoice.Id, LineNumber = 1,
                Description = "Widget A", Quantity = 2, UnitCode = "EA",
                UnitPrice = 50m, TaxRate = 20m, LineTotal = 100m,
                BuyerItemCode = "B001", SupplierItemCode = "S001" }
        };

        var svc = new CsvInvoiceTransformService();
        var bytes = await svc.TransformAsync(invoice, lines, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(bytes);

        csv.Should().Contain("INV-001");
        csv.Should().Contain("Widget A");
        csv.Should().Contain("B001");
        csv.Should().Contain("S001");
        csv.Should().Contain("50");
        csv.Should().Contain("100");
    }
}
```

- [ ] Run tests:
```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore
```
Expected: all existing tests pass + new tests pass.

- [ ] Also run infrastructure tests:
```
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore
```

- [ ] Commit:
```
git add ProcuLink.Transform.Tests/ ProcuLink.Infrastructure.Tests/Services/InvoiceServiceTests.cs
git commit -m "test(wave3): UblInvoiceParser, EdifactInvoiceParser stub, CsvInvoiceTransformService tests"
```

---

## Final verification

- [ ] Full solution build:
```
dotnet build ProcuLink.slnx --no-restore
```

- [ ] Full test suite:
```
dotnet test ProcuLink.slnx --no-restore
```
Expected: all prior tests pass + Wave 3 tests pass.

- [ ] Update `STATUS.md` — add under current queue:
```markdown
| **Wave 3** | Invoice + ASN canonical models | ✅ Implemented — UBL Invoice full, EDIFACT INVOIC/DESADV stubs (EdiFabric pending) |
```
