using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="ISupplierCatalogService"/>.
/// Every query is scoped to (orgId, supplierId) — never cross-tenant — mirroring
/// <see cref="ItemMappingService"/>.
/// </summary>
public sealed class SupplierCatalogService : ISupplierCatalogService
{
    /// <summary>Default page size for <see cref="ListAsync"/> when the caller passes none.</summary>
    public const int DefaultTake = 100;

    /// <summary>Hard upper bound on a single list page, to keep the read path bounded.</summary>
    public const int MaxTake = 1000;

    private readonly ProcuLinkDbContext _db;

    public SupplierCatalogService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SupplierProduct>> ListAsync(
        Guid orgId, Guid supplierId, string? query, int? take, CancellationToken ct)
    {
        var limit = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var q = _db.SupplierProducts
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive);

        if (!string.IsNullOrWhiteSpace(query))
        {
            // Case-insensitive contains via ToLower(). This translates on BOTH Npgsql
            // (lower(col) LIKE …) and the EF InMemory provider used by the tests, unlike
            // EF.Functions.ILike which is Postgres-only and throws on InMemory.
            var needle = query.Trim().ToLowerInvariant();
            q = q.Where(p =>
                p.Code.ToLower().Contains(needle)
                || (p.Name != null && p.Name.ToLower().Contains(needle))
                || (p.Barcode != null && p.Barcode.ToLower().Contains(needle)));
        }

        return await q
            .OrderBy(p => p.Code)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<(int Created, int Updated)> UpsertManyAsync(
        Guid orgId, Guid supplierId, IEnumerable<SupplierProduct> products, CancellationToken ct)
    {
        // Collapse the input to one draft per trimmed code (last wins), dropping blanks.
        var drafts = new Dictionary<string, SupplierProduct>(StringComparer.Ordinal);
        foreach (var p in products)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Code)) continue;
            drafts[p.Code.Trim()] = p;
        }

        if (drafts.Count == 0) return (0, 0);

        var codes = drafts.Keys.ToList();

        // One org+supplier-scoped IN query for all existing rows touched by this batch.
        var existing = await _db.SupplierProducts
            .Where(p => p.OrgId == orgId
                     && p.SupplierId == supplierId
                     && codes.Contains(p.Code))
            .ToListAsync(ct);

        var byCode = existing.ToDictionary(p => p.Code, StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var created = 0;
        var updated = 0;

        foreach (var (code, draft) in drafts)
        {
            if (byCode.TryGetValue(code, out var row))
            {
                row.Name       = Clean(draft.Name);
                row.Unit       = Clean(draft.Unit);
                row.Price      = draft.Price;
                row.Currency   = Clean(draft.Currency);
                row.Barcode    = Clean(draft.Barcode);
                row.ExternalId = Clean(draft.ExternalId);
                row.IsActive   = true; // re-import reactivates a previously discontinued code
                row.UpdatedAt  = now;
                updated++;
            }
            else
            {
                _db.SupplierProducts.Add(new SupplierProduct
                {
                    Id         = Guid.NewGuid(),
                    OrgId      = orgId,
                    SupplierId = supplierId,
                    Code       = code,
                    Name       = Clean(draft.Name),
                    Unit       = Clean(draft.Unit),
                    Price      = draft.Price,
                    Currency   = Clean(draft.Currency),
                    Barcode    = Clean(draft.Barcode),
                    ExternalId = Clean(draft.ExternalId),
                    IsActive   = true,
                    CreatedAt  = now,
                    UpdatedAt  = now,
                });
                created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return (created, updated);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        _db.SupplierProducts
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive)
            .CountAsync(ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Set-based on purpose: one <c>DELETE … WHERE org_id = … AND supplier_id = …</c>, so
    /// clearing a catalog at the 200,000-row cap (<c>SupplierCatalogFileParser.MaxCatalogRows</c>)
    /// costs no per-row memory. Loading the rows first cost ~976 B per tracked SupplierProduct —
    /// a ~200 MB spike on a full catalog.
    ///
    /// <c>ExecuteDelete</c> commits IMMEDIATELY and outside the context's pending changes: it
    /// neither participates in a later <c>SaveChanges</c> nor flushes whatever else the caller
    /// has tracked. The only caller (<c>SuppliersController.ClearCatalog</c>) tracks nothing
    /// across the call, so there is no half-apply window. A future caller that mutates tracked
    /// entities around this one must wrap both in a single explicit transaction.
    ///
    /// Covered by <c>SupplierCatalogDeletePostgresTests</c> on real Postgres — the EF InMemory
    /// provider does not implement <c>ExecuteDelete</c> and throws on it.
    /// </remarks>
    public Task<int> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        _db.SupplierProducts
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .ExecuteDeleteAsync(ct);

    private static string? Clean(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
