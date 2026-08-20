using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Catalog;
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

    /// <summary>
    /// Escape character for the ILIKE search pattern, and the escaping that makes the user's
    /// query LITERAL inside it — `%` and `_` are search text here, never wildcards, matching the
    /// ToLower().Contains semantics this search always had.
    /// </summary>
    private const string LikeEscapeChar = "\\";

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

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
            // Case-insensitive substring on code / name / barcode / manufacturer part number.
            // An operator reviewing a punchout line has the MANUFACTURER's part number in front
            // of them, not the supplier's code — searching the catalog for it has to find the
            // product, or the manual fallback is "grep the whole catalog by eye".
            if (_db.Database.IsNpgsql())
            {
                // ILIKE, not ToLower().Contains. The AddCatalogTrigramIndexes migration ships GIN
                // gin_trgm_ops indexes on code and name, and those serve the % / LIKE / ILIKE
                // operator family — but the index is on `code`, not `lower(code)`, so the previous
                // `lower(code) LIKE '%…%'` translation could never use it and degraded to a
                // bounded sequential scan. ILIKE keeps identical semantics (case-insensitive
                // substring) and leaves the indexed columns index-servable. LIKE wildcards in the
                // user's query are escaped so `%` and `_` stay LITERAL text, exactly as
                // ToLower().Contains treated them. Barcode and manufacturer_part_number carry no
                // trigram index; ILIKE there is semantically identical and loses nothing.
                // Pinned (semantics + translation shape) by CatalogTrigramIndexUsagePostgresTests.
                var pattern = "%" + EscapeLikePattern(query.Trim()) + "%";
                q = q.Where(p =>
                    EF.Functions.ILike(p.Code, pattern, LikeEscapeChar)
                    || (p.Name != null && EF.Functions.ILike(p.Name, pattern, LikeEscapeChar))
                    || (p.Barcode != null && EF.Functions.ILike(p.Barcode, pattern, LikeEscapeChar))
                    || (p.ManufacturerPartNumber != null
                        && EF.Functions.ILike(p.ManufacturerPartNumber, pattern, LikeEscapeChar)));
            }
            else
            {
                // EF.Functions.ILike is Postgres-only and throws on the EF InMemory provider used
                // by the tests — same guard pattern as CatalogRetrievalService. This branch keeps
                // the original translation-safe spelling with identical semantics.
                var needle = query.Trim().ToLowerInvariant();
                q = q.Where(p =>
                    p.Code.ToLower().Contains(needle)
                    || (p.Name != null && p.Name.ToLower().Contains(needle))
                    || (p.Barcode != null && p.Barcode.ToLower().Contains(needle))
                    || (p.ManufacturerPartNumber != null
                        && p.ManufacturerPartNumber.ToLower().Contains(needle)));
            }
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
        // ── DELIBERATELY Ordinal, unlike every READ of this column ───────────────────────────
        // The catalog RESOLVER folds case (OrderServiceShared.BuildCatalogLookupAsync,
        // CatalogRetrievalService, the auto-detect probe — all ItemCodeComparison). This WRITE path
        // does not, and that asymmetry is the intended answer, not an oversight:
        //
        //   * Folding here would collapse "AB-1" and "ab-1" arriving in ONE supplier feed into one
        //     draft and silently DELETE a product from the customer's catalog. The item code is a
        //     namespace the SUPPLIER controls; an import must not decide two of their SKUs are the
        //     same thing. Losing a row on import is worse than holding a twin.
        //   * A twin here is not an outage: the read side resolves it deterministically
        //     (BuildCatalogLookupAsync orders by Code then Id before its first-wins TryAdd), so the
        //     same order always reaches the same product.
        //   * Merging existing twins is a decision about a customer's data — the founder's, not an
        //     importer's. IItemMappingService.FindCaseVariantTwinsAsync reports the analogous
        //     situation for learned mappings rather than acting on it, for the same reason.
        //
        // Registered as an accepted exception in ItemCodeComparerGuardTests; changing it means
        // changing that reason too.
        //
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
                    // Manufacturer part number + its normalised lookup key are written together,
                    // here and nowhere else, so the key can never drift from the raw value.
                    row.ManufacturerPartNumber = Clean(draft.ManufacturerPartNumber);
                    row.ManufacturerPartNumberNormalized =
                        ProductKeyNormalizer.Normalize(row.ManufacturerPartNumber);
                    row.ManufacturerName = Clean(draft.ManufacturerName);
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
                        ManufacturerPartNumber = Clean(draft.ManufacturerPartNumber),
                        ManufacturerPartNumberNormalized =
                            ProductKeyNormalizer.Normalize(Clean(draft.ManufacturerPartNumber)),
                        ManufacturerName = Clean(draft.ManufacturerName),
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
