using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Canonical;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Repositories;

public class EfOrderRepository : IOrderRepository
{
    private readonly ProcuLinkDbContext _db;
    private readonly ICurrentTenantService _tenant;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public EfOrderRepository(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task SaveAsync(PurchaseOrder po, CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;
        var supplier = await GetOrCreateSupplierAsync(po.SupplierName, orgId, ct);

        var existing = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Where(x => x.Id == po.Id && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        var canonicalJson = JsonDocument.Parse(JsonSerializer.Serialize(po, JsonOpts));
        var status = po.AutomationStatus == AutomationStatus.Automatable
            ? "ready"
            : "pending_review";

        if (existing is null)
        {
            var entity = new PurchaseOrderEntity
            {
                Id = po.Id,
                OrgId = orgId,
                SupplierId = supplier.Id,
                PoNumber = po.PoNumber,
                OrderDate = DateOnly.FromDateTime(po.OrderDate == default ? DateTime.UtcNow : po.OrderDate),
                Currency = po.Currency,
                Status = status,
                CanonicalJson = canonicalJson,
                CreatedAt = po.CreatedAt == default ? DateTime.UtcNow : DateTime.SpecifyKind(po.CreatedAt, DateTimeKind.Utc),
                UpdatedAt = DateTime.UtcNow,
                Lines = po.Lines.Select(MapLineToEntity).ToList()
            };
            _db.PurchaseOrders.Add(entity);
        }
        else
        {
            existing.SupplierId = supplier.Id;
            existing.PoNumber = po.PoNumber;
            existing.OrderDate = DateOnly.FromDateTime(po.OrderDate == default ? DateTime.UtcNow : po.OrderDate);
            existing.Currency = po.Currency;
            existing.Status = status;
            existing.CanonicalJson = canonicalJson;
            existing.UpdatedAt = DateTime.UtcNow;

            _db.PurchaseOrderLines.RemoveRange(existing.Lines);
            existing.Lines = po.Lines.Select(MapLineToEntity).ToList();
        }

        await _db.SaveChangesAsync(ct);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<PurchaseOrder?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;

        var entity = await _db.PurchaseOrders
            .AsNoTracking()
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Where(x => x.Id == id && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToCanonical(entity);
    }

    public async Task<IReadOnlyList<PurchaseOrder>> ListAsync(CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;

        var entities = await _db.PurchaseOrders
            .AsNoTracking()
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Where(x => x.OrgId == orgId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToCanonical).ToList();
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Get an existing Supplier by name (case-sensitive, trimmed) for this org,
    /// or add a new one to the change tracker. The caller must call SaveChangesAsync.
    /// </summary>
    private async Task<Supplier> GetOrCreateSupplierAsync(
        string supplierName, Guid orgId, CancellationToken ct)
    {
        var supplier = await _db.Suppliers
            .Where(s => s.OrgId == orgId && s.Name == supplierName)
            .FirstOrDefaultAsync(ct);

        if (supplier is null)
        {
            supplier = new Supplier
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                Name = supplierName,
                CreatedAt = DateTime.UtcNow
            };
            _db.Suppliers.Add(supplier);
        }

        return supplier;
    }

    private static PurchaseOrderLineEntity MapLineToEntity(PurchaseOrderLine l) =>
        new()
        {
            Id = Guid.NewGuid(),
            LineNumber = l.LineNumber,
            BuyerItemCode = l.BuyerItemCode,
            SupplierItemCode = l.SupplierItemCode,
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            Confidence = 0f,
            NeedsReview = string.IsNullOrWhiteSpace(l.SupplierItemCode)
        };

    /// <summary>
    /// Reconstruct the Canonical PurchaseOrder.
    /// Prefers the stored canonical_json for full fidelity (preserves BuyerName,
    /// AutomationReason, etc.). Falls back to entity fields if json is absent.
    /// </summary>
    private static PurchaseOrder ToCanonical(PurchaseOrderEntity e)
    {
        if (e.CanonicalJson is not null)
        {
            var po = JsonSerializer.Deserialize<PurchaseOrder>(
                e.CanonicalJson.RootElement.GetRawText(), JsonOpts);
            if (po is not null)
                return po;
        }

        // Fallback reconstruction
        return new PurchaseOrder
        {
            Id = e.Id,
            PoNumber = e.PoNumber,
            SupplierName = e.Supplier?.Name ?? string.Empty,
            OrderDate = e.OrderDate.ToDateTime(TimeOnly.MinValue),
            Currency = e.Currency,
            AutomationStatus = e.Status == "ready"
                ? AutomationStatus.Automatable
                : AutomationStatus.NeedsClarification,
            CreatedAt = e.CreatedAt,
            Lines = e.Lines.Select(l => new PurchaseOrderLine
            {
                LineNumber = l.LineNumber,
                BuyerItemCode = l.BuyerItemCode,
                SupplierItemCode = l.SupplierItemCode,
                Description = l.Description ?? string.Empty,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };
    }
}
