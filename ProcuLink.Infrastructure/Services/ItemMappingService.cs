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
    public async Task<IReadOnlyDictionary<string, string?>> ResolveManyAsync(
        Guid orgId, Guid supplierId, IEnumerable<string> buyerItemCodes, CancellationToken ct)
    {
        // Distinct, trimmed, non-blank set of codes to look up. Keyed case-sensitively
        // to mirror ResolveAsync (BuyerItemCode == normalised) exact matching.
        var requested = buyerItemCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Seed every requested code with null so callers can treat the dictionary as
        // total over the (non-blank) input set — a missing key never throws.
        var result = new Dictionary<string, string?>(requested.Count, StringComparer.Ordinal);
        foreach (var code in requested)
            result[code] = null;

        if (requested.Count == 0)
            return result;

        // One org+supplier-scoped IN query for all codes instead of N point lookups.
        var rows = await _db.ItemMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId
                     && m.SupplierId == supplierId
                     && requested.Contains(m.BuyerItemCode))
            .Select(m => new { m.BuyerItemCode, m.SupplierItemCode })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            // Only overwrite a key that was actually requested (case-sensitive) so
            // behaviour matches ResolveAsync even if the DB collation is broader.
            if (result.ContainsKey(row.BuyerItemCode))
                result[row.BuyerItemCode] = row.SupplierItemCode;
        }

        return result;
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
                AppliedCount     = 1,
                CreatedAt        = now,
                UpdatedAt        = now
            });
        }
        else
        {
            var codeChanged = !string.Equals(
                existing.SupplierItemCode, supplierItemCode.Trim(),
                StringComparison.OrdinalIgnoreCase);

            if (codeChanged)
            {
                _db.MappingCorrections.Add(new MappingCorrection
                {
                    Id                  = Guid.NewGuid(),
                    OrgId               = orgId,
                    MappingId           = existing.Id,
                    OldSupplierItemCode = existing.SupplierItemCode,
                    NewSupplierItemCode = supplierItemCode.Trim(),
                    Source              = sourceStr,
                    CorrectedAt         = now,
                });
            }

            existing.SupplierItemCode = supplierItemCode.Trim();
            existing.Source           = sourceStr;
            existing.Confidence       = source == MappingSource.Manual ? 1.0f : existing.Confidence;
            existing.AppliedCount    += 1;
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
