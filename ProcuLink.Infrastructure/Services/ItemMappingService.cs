using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IItemMappingService"/>.
/// All queries are scoped to (orgId, supplierId) — never cross-tenant.
/// Phase 2 uses exact-match resolution only; fuzzy/AI matching is Phase 4.
/// </summary>
public sealed class ItemMappingService : IItemMappingService
{
    private readonly ProcuLinkDbContext _db;

    public ItemMappingService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<string?> ResolveAsync(
        Guid orgId, Guid supplierId, string buyerItemCode, CancellationToken ct)
    {
        var normalised = buyerItemCode.Trim();

        var mapping = await _db.ItemMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId
                     && m.SupplierId == supplierId
                     && m.BuyerItemCode == normalised)
            .Select(m => m.SupplierItemCode)
            .FirstOrDefaultAsync(ct);

        return mapping;
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(
        Guid orgId, Guid supplierId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct)
    {
        var normalised = buyerItemCode.Trim();
        var sourceStr  = source.ToString().ToLowerInvariant();

        var existing = await _db.ItemMappings
            .Where(m => m.OrgId == orgId
                     && m.SupplierId == supplierId
                     && m.BuyerItemCode == normalised)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;

        if (existing is null)
        {
            _db.ItemMappings.Add(new ItemMapping
            {
                Id               = Guid.NewGuid(),
                OrgId            = orgId,
                SupplierId       = supplierId,
                BuyerItemCode    = normalised,
                SupplierItemCode = supplierItemCode.Trim(),
                Confidence       = source == MappingSource.Manual ? 1.0f : 0.8f,
                Source           = sourceStr,
                CreatedAt        = now,
                UpdatedAt        = now
            });
        }
        else
        {
            existing.SupplierItemCode = supplierItemCode.Trim();
            existing.Source           = sourceStr;
            existing.Confidence       = source == MappingSource.Manual ? 1.0f : existing.Confidence;
            existing.UpdatedAt        = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ItemMapping>> GetForSupplierAsync(
        Guid orgId, Guid supplierId, CancellationToken ct)
    {
        return await _db.ItemMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.SupplierId == supplierId)
            .OrderBy(m => m.BuyerItemCode)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid orgId, Guid mappingId, CancellationToken ct)
    {
        var mapping = await _db.ItemMappings
            .Where(m => m.Id == mappingId && m.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (mapping is not null)
        {
            _db.ItemMappings.Remove(mapping);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task<ItemMapping> CreateAsync(
        Guid orgId, Guid supplierId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var mapping = new ItemMapping
        {
            Id               = Guid.NewGuid(),
            OrgId            = orgId,
            SupplierId       = supplierId,
            BuyerItemCode    = buyerItemCode.Trim(),
            SupplierItemCode = supplierItemCode.Trim(),
            Confidence       = source == MappingSource.Manual ? 1.0f : 0.8f,
            Source           = source.ToString().ToLowerInvariant(),
            CreatedAt        = now,
            UpdatedAt        = now,
        };
        _db.ItemMappings.Add(mapping);
        await _db.SaveChangesAsync(ct);
        return mapping;
    }

    /// <inheritdoc/>
    public async Task<ItemMapping?> UpdateByIdAsync(
        Guid orgId, Guid mappingId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct)
    {
        var mapping = await _db.ItemMappings
            .Where(m => m.Id == mappingId && m.OrgId == orgId)
            .FirstOrDefaultAsync(ct);
        if (mapping is null) return null;
        mapping.BuyerItemCode    = buyerItemCode.Trim();
        mapping.SupplierItemCode = supplierItemCode.Trim();
        mapping.Source           = source.ToString().ToLowerInvariant();
        mapping.Confidence       = source == MappingSource.Manual ? 1.0f : mapping.Confidence;
        mapping.UpdatedAt        = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return mapping;
    }
}
