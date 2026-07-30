using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Catalog;
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

    /// <summary>
    /// One candidate row, projected so the deterministic tie-break below can run client-side over a
    /// tiny set instead of being expressed as fragile ordering inside the SQL.
    /// </summary>
    private sealed record MappingCandidate(
        Guid Id, string BuyerItemCode, string SupplierItemCode, DateTime UpdatedAt);

    /// <summary>
    /// Picks ONE row when a code matches several (only possible for case-variant rows written before
    /// <see cref="UpsertAsync"/> became case-aware). An exact-case row always wins; otherwise the
    /// most recently updated, with the id as a final tie-break so the answer never depends on the
    /// order the database happened to return. Determinism matters more than which row wins: a
    /// resolver that alternates between two supplier codes ships two different documents for the
    /// same order.
    /// </summary>
    private static MappingCandidate? PickDeterministically(
        IEnumerable<MappingCandidate> candidates, string trimmedRequested)
    {
        var list = candidates.ToList();
        if (list.Count == 0) return null;

        var exact = list.FirstOrDefault(
            c => string.Equals(c.BuyerItemCode, trimmedRequested, StringComparison.Ordinal));
        if (exact is not null) return exact;

        return list
            .OrderByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.Id)
            .First();
    }

    // ── Query-plan note (WP-14) ──────────────────────────────────────────────────
    // The predicates below fold case with `.ToLower()`, which cannot be served by the plain btree
    // index IX(org_id, supplier_id, buyer_item_code) on its third column. This is NOT a sequential
    // scan of the table: the (org_id, supplier_id) equality prefix of that same index still applies,
    // so the case-folded term only filters within ONE supplier's mapping partition — a set sized by
    // how many codes one buyer taught for one supplier, not by the whole table. The exact-match term
    // is OR'd in FIRST so the planner keeps the fully-indexed path available for the common case.
    // No functional index and no migration were added: that would mean DDL on the production table
    // for a partition that is small by construction. If a single (org, supplier) ever accumulates
    // enough mappings for this to matter, the fix is `CREATE INDEX CONCURRENTLY ... (lower(buyer_item_code))`
    // — deliberately deferred rather than done blind.

    /// <inheritdoc/>
    public async Task<string?> ResolveAsync(
        Guid orgId, Guid supplierId, string buyerItemCode, CancellationToken ct)
    {
        var trimmed = buyerItemCode.Trim();
        var key     = ItemCodeComparison.Key(trimmed);

        var candidates = await _db.ItemMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId
                     && m.SupplierId == supplierId
                     && (m.BuyerItemCode == trimmed || m.BuyerItemCode.ToLower() == key))
            .Select(m => new MappingCandidate(m.Id, m.BuyerItemCode, m.SupplierItemCode, m.UpdatedAt))
            .ToListAsync(ct);

        return PickDeterministically(candidates, trimmed)?.SupplierItemCode;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string?>> ResolveManyAsync(
        Guid orgId, Guid supplierId, IEnumerable<string> buyerItemCodes, CancellationToken ct)
    {
        // Distinct, trimmed, non-blank set of codes to look up — de-duplicated under the SHARED item
        // code rule, so "B-1" and "b-1" arriving on the same order are one lookup, not two.
        var requested = buyerItemCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(ItemCodeComparison.Comparer)
            .ToList();

        // Seed every requested code with null so callers can treat the dictionary as total over the
        // (non-blank) input set — a missing key never throws. Keyed with the shared comparer so a
        // caller looking up `line.BuyerItemCode.Trim()` hits regardless of the spelling it holds.
        var result = new Dictionary<string, string?>(requested.Count, ItemCodeComparison.Comparer);
        foreach (var code in requested)
            result[code] = null;

        if (requested.Count == 0)
            return result;

        var keys = requested.Select(ItemCodeComparison.Key).ToList();

        // One org+supplier-scoped query for all codes instead of N point lookups.
        var rows = await _db.ItemMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId
                     && m.SupplierId == supplierId
                     && (requested.Contains(m.BuyerItemCode) || keys.Contains(m.BuyerItemCode.ToLower())))
            .Select(m => new MappingCandidate(m.Id, m.BuyerItemCode, m.SupplierItemCode, m.UpdatedAt))
            .ToListAsync(ct);

        // Resolve per REQUESTED code (not per row) so the same tie-break as ResolveAsync applies and
        // the two resolvers cannot disagree about which of two case-variant rows wins.
        foreach (var code in requested)
        {
            var matches = rows.Where(r => ItemCodeComparison.Matches(r.BuyerItemCode, code));
            result[code] = PickDeterministically(matches, code)?.SupplierItemCode;
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
        var key        = ItemCodeComparison.Key(normalised);
        var sourceStr  = source.ToString().ToLowerInvariant();

        // The WRITE side must use the SAME case rule as the read side. Fixing resolution alone is a
        // trap: with a case-blind resolver but a case-SENSITIVE existing-row lookup, an operator
        // correcting "b-1" while "B-1" is already stored inserts a SECOND row (the unique index is
        // case-sensitive, so the database permits it) — and the resolver then has two candidates for
        // one code. That converts a missed mapping into an unstable one, which is worse. Matching
        // case-insensitively here means a correction always UPDATES the mapping it corrects.
        var candidates = await _db.ItemMappings
            .Where(m => m.OrgId == orgId
                     && m.SupplierId == supplierId
                     && (m.BuyerItemCode == normalised || m.BuyerItemCode.ToLower() == key))
            .ToListAsync(ct);

        var existing = candidates.Count == 0
            ? null
            : candidates.FirstOrDefault(m => string.Equals(m.BuyerItemCode, normalised, StringComparison.Ordinal))
              ?? candidates.OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.Id).First();

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
