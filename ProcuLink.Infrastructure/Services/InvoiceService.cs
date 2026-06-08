using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class InvoiceService : IInvoiceService
{
    private readonly ProcuLinkDbContext                     _db;
    private readonly IFileStorageService                    _storage;
    private readonly IEnumerable<IInvoiceTransformService>  _transformers;

    public InvoiceService(
        ProcuLinkDbContext                    db,
        IFileStorageService                   storage,
        IEnumerable<IInvoiceTransformService> transformers)
    {
        _db           = db;
        _storage      = storage;
        _transformers = transformers;
    }

    public async Task<InvoiceEntity> CreateStubAsync(
        Guid orgId, Guid? supplierId, Stream stream,
        string fileName, string contentType, CancellationToken ct)
    {
        var key = $"invoices/{orgId}/{Guid.NewGuid()}_{fileName}";
        // IFileStorageService.UploadAsync signature: (Stream content, string key, string contentType, CancellationToken ct)
        await _storage.UploadAsync(stream, key, contentType, ct);

        var inv = new InvoiceEntity
        {
            Id             = Guid.NewGuid(),
            OrganisationId = orgId,
            SupplierId     = supplierId,
            InvoiceNumber  = "PENDING",
            IssueDate      = DateOnly.FromDateTime(DateTime.UtcNow),
            Status         = "parsing",
            SourceFileName = fileName,
            SourceFileKey  = key,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };

        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync(ct);
        return inv;
    }

    public async Task<InvoiceEntity> PersistParsedAsync(
        Guid orgId, Guid invoiceId, ParsedInvoiceData data, CancellationToken ct)
    {
        var inv = await _db.Invoices
                           .Where(i => i.OrganisationId == orgId && i.Id == invoiceId)
                           .FirstOrDefaultAsync(ct)
                   ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");

        inv.InvoiceNumber = data.InvoiceNumber;
        inv.IssueDate     = data.IssueDate;
        inv.DueDate       = data.DueDate;
        inv.Currency      = data.Currency;
        inv.BuyerRef      = data.BuyerRef;
        inv.SupplierRef   = data.SupplierRef;
        inv.PaymentTerms  = data.PaymentTerms;
        inv.SubTotal      = data.SubTotal;
        inv.TaxTotal      = data.TaxTotal;
        inv.GrandTotal    = data.GrandTotal;
        inv.Status        = "pending_review";
        inv.UpdatedAt     = DateTime.UtcNow;

        var lines = data.Lines.Select(l => new InvoiceLineEntity
        {
            Id               = Guid.NewGuid(),
            InvoiceId        = inv.Id,
            OrganisationId   = orgId,
            LineNumber       = l.LineNumber,
            Description      = l.Description,
            Quantity         = l.Quantity,
            UnitCode         = l.UnitCode,
            UnitPrice        = l.UnitPrice,
            TaxRate          = l.TaxRate,
            LineTotal        = l.LineTotal,
            BuyerItemCode    = l.BuyerItemCode,
            SupplierItemCode = l.SupplierItemCode,
        }).ToList();

        _db.InvoiceLines.AddRange(lines);
        await _db.SaveChangesAsync(ct);

        inv.Lines = lines;
        return inv;
    }

    public async Task<InvoiceEntity?> GetAsync(Guid orgId, Guid invoiceId, CancellationToken ct)
        => await _db.Invoices.Include(i => i.Lines)
                             .Where(i => i.OrganisationId == orgId && i.Id == invoiceId)
                             .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<InvoiceEntity>> ListAsync(Guid orgId, CancellationToken ct)
        => await _db.Invoices
                    .Where(i => i.OrganisationId == orgId)
                    .OrderByDescending(i => i.CreatedAt)
                    .ToListAsync(ct);

    public async Task<InvoiceEntity> ApproveAsync(Guid orgId, Guid invoiceId, CancellationToken ct)
    {
        var inv = await _db.Invoices
                           .Where(i => i.OrganisationId == orgId && i.Id == invoiceId)
                           .FirstOrDefaultAsync(ct)
                   ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");
        inv.Status    = "approved";
        inv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return inv;
    }

    public async Task<(byte[] Bytes, string ContentType, string FileExtension)> ForwardAsync(
        Guid orgId, Guid invoiceId, string outputFormat, CancellationToken ct)
    {
        var inv = await _db.Invoices.Include(i => i.Lines)
                                    .Where(i => i.OrganisationId == orgId && i.Id == invoiceId)
                                    .FirstOrDefaultAsync(ct)
                  ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");

        if (inv.Status != "approved")
            throw new InvalidOperationException("Invoice must be approved before forwarding.");

        var transformer = _transformers.FirstOrDefault(t =>
            t.Format.Equals(outputFormat, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No invoice transformer for format '{outputFormat}'.");

        var bytes = await transformer.TransformAsync(inv, inv.Lines, ct);

        var (contentType, ext) = outputFormat.ToLowerInvariant() switch
        {
            "csv"    => ("text/csv", ".csv"),
            "xml"    => ("application/xml", ".xml"),
            "json"   => ("application/json", ".json"),
            // Peppol BIS Billing 3.0 — a UBL 2.1 XML document.
            "peppol" => ("application/xml", ".xml"),
            _        => ("application/octet-stream", ".bin"),
        };

        // Advance status to forwarded
        inv.Status    = "forwarded";
        inv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (bytes, contentType, ext);
    }
}
