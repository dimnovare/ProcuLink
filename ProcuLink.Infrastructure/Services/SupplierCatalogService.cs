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

    /// <summary>
    /// Rows per upsert batch. A real distributor feed runs to the parser's 200k row cap
    /// (<c>SupplierCatalogFileParser.MaxCatalogRows</c>). Measured against local Postgres on a
    /// synthetic 200k-row CSV: importing in ONE batch retained 615 MB and peaked at 1.43 GB
    /// working set, leaving all 200k entities tracked; at 5k batches it retained 24 MB, peaked
    /// at 415 MB, and left nothing tracked. A sweep over 1k/5k/10k batches showed wall time flat
    /// (~46 s) and retained memory linear (11 / 22 / 38 MB), so the size is chosen purely on
    /// memory. Cost: the insert path is ~40% slower than the single-batch version (29 s → 41 s
    /// for 200k rows), which a background sync absorbs; 1.4 GB of tracked entities it cannot.
    /// </summary>
    public const int UpsertBatchSize = 5_000;

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

        var now = DateTime.UtcNow;
        var created = 0;
        var updated = 0;

        // Batched so a full distributor feed (up to the parser's 200k row cap) never puts the
        // whole file in the change tracker at once — see UpsertBatchSize for the measurements.
        // Trade-off: this is no longer one all-or-nothing transaction. A failure part-way leaves
        // the earlier batches committed, which is safe here because the upsert is idempotent by
        // (org, supplier, code): the caller's retry re-applies the whole file and converges. A
        // partial import can never be mistaken for a complete one by the pull path's
        // unchanged-skip: LastFileHash has exactly one writer (CatalogPullService.PullAsync,
        // after this method returns), and CatalogSyncSourceJob additionally stamps LastSyncError
        // on failure, which the skip predicate requires to be null.
        foreach (var batch in drafts.Chunk(UpsertBatchSize))
        {
            var codes = batch.Select(kv => kv.Key).ToList();

            // One org+supplier-scoped IN query for the existing rows touched by THIS batch.
            var existing = await _db.SupplierProducts
                .Where(p => p.OrgId == orgId
                         && p.SupplierId == supplierId
                         && codes.Contains(p.Code))
                .ToListAsync(ct);

            var byCode = existing.ToDictionary(p => p.Code, StringComparer.Ordinal);
            var touched = new List<SupplierProduct>(batch.Length);

            foreach (var (code, draft) in batch)
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
                    touched.Add(row);
                }
                else
                {
                    var added = new SupplierProduct
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
                    };
                    _db.SupplierProducts.Add(added);
                    created++;
                    touched.Add(added);
                }
            }

            await _db.SaveChangesAsync(ct);

            // Release ONLY the rows this batch touched. A blanket ChangeTracker.Clear() would
            // also detach entities the caller is holding — CatalogPullService tracks the
            // SupplierCatalogSource across this call and writes its sync status to the same
            // DbContext afterwards, and that write would be silently dropped.
            foreach (var entity in touched)
                _db.Entry(entity).State = EntityState.Detached;
        }

        return (created, updated);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        _db.SupplierProducts
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive)
            .CountAsync(ct);

    /// <inheritdoc/>
    public async Task<int> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var rows = await _db.SupplierProducts
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .ToListAsync(ct);

        if (rows.Count == 0) return 0;

        _db.SupplierProducts.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static string? Clean(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
