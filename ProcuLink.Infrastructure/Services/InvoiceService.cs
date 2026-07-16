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

    // SCALE-GATED CONSTRAINT: this returns the org's ENTIRE invoice list (no LIMIT /
    // pagination) and there is no composite index on (organisation_id, created_at), so
    // the OrderByDescending falls back to a sort over the org partition. Acceptable
    // today — invoice ingestion is a low-volume, Integration+ secondary surface (the PO
    // path is the primary one) — but REVISIT past ~1k invoices/org: add a
    // (organisation_id, created_at DESC) index and page this the way GET /api/orders
    // already does (limit/offset + totalCount). See
    // docs/audit/2026-06-12-scale-gated-constraints.md.
    public async Task<IReadOnlyList<InvoiceListItem>> ListAsync(Guid orgId, CancellationToken ct)
        => await _db.Invoices
                    .Where(i => i.OrganisationId == orgId)
                    .OrderByDescending(i => i.CreatedAt)
                    // Left-join the supplier by id so the display name resolves even though
                    // InvoiceEntity has no Supplier navigation. Fall back to the free-text
                    // SupplierRef captured at parse time when no supplier row is linked.
                    .Select(i => new InvoiceListItem(
                        i.Id,
                        i.SupplierId,
                        _db.Suppliers
                            .Where(s => s.OrgId == orgId && s.Id == i.SupplierId)
                            .Select(s => s.Name)
                            .FirstOrDefault() ?? i.SupplierRef,
                        i.InvoiceNumber,
                        i.IssueDate,
                        i.DueDate,
                        i.Currency,
                        i.GrandTotal,
                        i.Status,
                        i.Lines.Count,
                        i.SourceFileName,
                        i.CreatedAt))
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

    // Loads the row and persists via the change tracker (not ExecuteUpdate) so this
    // works on both Postgres and the EF InMemory test provider, and re-reads at the
    // moment of failure so the row is not stale. Mirrors OrderIngestionService.SetOrderFailedAsync.
    public async Task SetFailedAsync(Guid orgId, Guid invoiceId, CancellationToken ct)
    {
        // Poisoned-context guard (finding C1): this runs from a catch AFTER a failed
        // PersistParsedAsync may have staged invoice lines (AddRange) and mutated the invoice
        // on the SAME scoped DbContext. Without clearing, the SaveChanges below would flush that
        // poisoned set — committing parsed lines under a "failed" invoice, which the status guard
        // then blocks from ever re-parsing. Clear first so only the clean status flip persists;
        // the row is re-loaded fresh below.
        _db.ChangeTracker.Clear();

        var inv = await _db.Invoices
                           .Where(i => i.OrganisationId == orgId && i.Id == invoiceId)
                           .FirstOrDefaultAsync(ct);
        if (inv is null) return;

        inv.Status    = "failed";
        inv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
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
