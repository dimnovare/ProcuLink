using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

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

/// <summary>
/// List-row projection for the inbound Invoices table. Carries the joined supplier
/// display name (from <see cref="Supplier.Name"/>, falling back to the free-text
/// SupplierRef) and the line count, neither of which lives on <see cref="InvoiceEntity"/>
/// directly. Field names mirror the frontend <c>InvoiceDto</c> contract exactly.
/// </summary>
public sealed record InvoiceListItem(
    Guid     Id,
    Guid?    SupplierId,
    string?  SupplierName,
    string   InvoiceNumber,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string   Currency,
    decimal  GrandTotal,
    string   Status,
    int      LineCount,
    string?  SourceFileName,
    DateTime CreatedAt);

public interface IInvoiceService
{
    /// <summary>Upload source file to R2, create stub with status=parsing.</summary>
    Task<InvoiceEntity> CreateStubAsync(
        Guid orgId, Guid? supplierId, Stream stream,
        string fileName, string contentType, CancellationToken ct);

    /// <summary>Persist parsed invoice data to an existing stub (called by ParseInvoiceJob).</summary>
    Task<InvoiceEntity> PersistParsedAsync(
        Guid orgId, Guid invoiceId, ParsedInvoiceData data, CancellationToken ct);

    Task<InvoiceEntity?> GetAsync(Guid orgId, Guid invoiceId, CancellationToken ct);

    /// <summary>
    /// List the org's invoices for the inbound Invoices table, enriched with the
    /// joined supplier display name and line count.
    /// </summary>
    Task<IReadOnlyList<InvoiceListItem>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>Advance status from pending_review → approved.</summary>
    Task<InvoiceEntity> ApproveAsync(Guid orgId, Guid invoiceId, CancellationToken ct);

    /// <summary>Transform an approved invoice to the requested format and return bytes.</summary>
    Task<(byte[] Bytes, string ContentType, string FileExtension)> ForwardAsync(
        Guid orgId, Guid invoiceId, string outputFormat, CancellationToken ct);
}
