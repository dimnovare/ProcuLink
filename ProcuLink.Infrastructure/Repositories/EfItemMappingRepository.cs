using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Canonical;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Repositories;

public class EfItemMappingRepository : IItemMappingRepository
{
    private readonly ProcuLinkDbContext _db;
    private readonly ICurrentTenantService _tenant;

    public EfItemMappingRepository(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<string?> TryGetSupplierItemCodeAsync(
        string supplierName, string buyerItemCode, CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;
        var normalizedBuyer = buyerItemCode?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(normalizedBuyer))
            return null;

        return await _db.ItemMappings
            .Where(m => m.OrgId == orgId
                     && m.BuyerItemCode == normalizedBuyer
                     && m.Supplier.Name == supplierName)
            .Select(m => (string?)m.SupplierItemCode)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ItemCodeMapping>> ListAsync(
        string supplierName, CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;

        return await _db.ItemMappings
            .Where(m => m.OrgId == orgId && m.Supplier.Name == supplierName)
            .OrderBy(m => m.BuyerItemCode)
            .Select(m => new ItemCodeMapping(m.BuyerItemCode, m.SupplierItemCode, m.CreatedAt))
            .ToListAsync(ct);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<ItemCodeMapping> UpsertAsync(
        string supplierName,
        string buyerItemCode,
        string supplierItemCode,
        CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;
        var normalizedBuyer = buyerItemCode?.Trim() ?? string.Empty;
        var trimmedSupplier = supplierItemCode?.Trim() ?? string.Empty;

        var supplier = await GetOrCreateSupplierAsync(supplierName, orgId, ct);

        var existing = await _db.ItemMappings
            .Where(m => m.OrgId == orgId
                     && m.SupplierId == supplier.Id
                     && m.BuyerItemCode == normalizedBuyer)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;

        if (existing is not null)
        {
            existing.SupplierItemCode = trimmedSupplier;
            existing.UpdatedAt = now;
        }
        else
        {
            existing = new ItemMapping
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplier.Id,
                BuyerItemCode = normalizedBuyer,
                SupplierItemCode = trimmedSupplier,
                Confidence = 1f,
                Source = "manual",
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.ItemMappings.Add(existing);
        }

        await _db.SaveChangesAsync(ct);

        return new ItemCodeMapping(existing.BuyerItemCode, existing.SupplierItemCode, existing.CreatedAt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
}
