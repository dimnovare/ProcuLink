using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Catalog;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Group V10 — indexed catalog retrieval at scale. EF Core implementation of
/// <see cref="ICatalogRetrievalService"/>: exact code/barcode lookup first (served by the unique
/// / btree indexes), then pg_trgm trigram similarity ranking for the fuzzy remainder — without
/// materialising the whole catalog. Every query is scoped to (orgId, supplierId).
///
/// Provider safety: the trigram path uses <c>EF.Functions.TrigramsAreSimilar</c> (the
/// index-served <c>%</c> pre-filter) and <c>EF.Functions.TrigramsSimilarity</c> (the ranking),
/// both Postgres-only — they throw on the EF InMemory provider used by the tests. This service detects a
/// non-Postgres provider up front (<see cref="ShouldUseIndexedRetrievalAsync"/>) AND defensively
/// catches a translation failure at query time, signalling the caller (via <c>null</c>) to fall
/// back to the existing in-memory lexical path. The trigram RANKING therefore cannot be proven by
/// the InMemory tests — it needs live-Postgres verification.
/// </summary>
public sealed class CatalogRetrievalService : ICatalogRetrievalService
{
    private readonly ProcuLinkDbContext _db;

    public CatalogRetrievalService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<bool> ShouldUseIndexedRetrievalAsync(
        Guid orgId, Guid supplierId, int inMemoryThreshold, CancellationToken ct)
    {
        // The indexed path only exists on Postgres (pg_trgm). On any other provider keep the
        // in-memory path so tests / non-Postgres dev are unaffected.
        if (!_db.Database.IsNpgsql())
            return false;

        // Cheap COUNT (the (org, supplier, is_active) index covers it). Small catalogs keep the
        // existing in-memory lexical retrieval so today's customers are byte-identical.
        var count = await _db.SupplierProducts
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive)
            .CountAsync(ct);

        return count > inMemoryThreshold;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SupplierProduct>?> RetrieveCandidatesAsync(
        Guid orgId,
        Guid supplierId,
        IReadOnlyList<CatalogRetrievalQuery> queries,
        int perQueryTopK,
        int overallCap,
        CancellationToken ct)
    {
        // Belt: never attempt the trigram path off Postgres (would throw on InMemory).
        if (!_db.Database.IsNpgsql())
            return null;

        if (queries.Count == 0 || overallCap <= 0)
            return new List<SupplierProduct>();

        try
        {
            // Result accumulator: code-keyed, insertion-ordered so exact matches (added first)
            // outrank fuzzy ones, mirroring the in-memory scorer's exact-before-fuzzy ordering.
            var ordered = new List<SupplierProduct>();
            var seen = new HashSet<string>(ItemCodeComparison.Comparer);

            // ── Pass 1 — EXACT code / barcode match (indexed) ─────────────────────────────
            // Buyer codes are the strong key: a buyer code may equal a supplier code OR a barcode.
            // The case rule is ItemCodeComparison — the SAME member the learned-mapping resolver and
            // BuildCatalogLookupAsync use. It was a hand-written OrdinalIgnoreCase here, which is
            // the same behaviour today but is exactly how the rule drifts: two spellings of one rule
            // in two files, and only one of them gets edited.
            var buyerCodes = queries
                .Select(q => (q.BuyerItemCode ?? string.Empty).Trim())
                .Where(c => c.Length > 0)
                .Distinct(ItemCodeComparison.Comparer)
                .ToList();

            if (buyerCodes.Count > 0)
            {
                var lowered = buyerCodes.Select(c => c.ToLower()).ToList();

                var exact = await _db.SupplierProducts
                    .AsNoTracking()
                    .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive
                        && (lowered.Contains(p.Code.ToLower())
                            || (p.Barcode != null && lowered.Contains(p.Barcode.ToLower()))))
                    .OrderBy(p => p.Code)
                    .Take(overallCap)
                    .Select(p => Project(p))
                    .ToListAsync(ct);

                foreach (var p in exact)
                {
                    var code = (p.Code ?? string.Empty).Trim();
                    if (code.Length == 0 || !seen.Add(code)) continue;
                    ordered.Add(p);
                    if (ordered.Count >= overallCap) return ordered;
                }
            }

            // ── Pass 1b — EXACT MANUFACTURER PART NUMBER match (indexed) ──────────────────
            // Compared on the normalised key (upper-cased, separators stripped) so the same part
            // written "LTQ2500-BK-BTK1" by the buyer and "ltq2500 bk btk1" by the distributor
            // still meets. Served by (org_id, supplier_id, manufacturer_part_number_normalized).
            var manufacturerKeys = queries
                .Select(q => ProductKeyNormalizer.Normalize(q.ManufacturerPartNumber))
                .Where(k => k is not null)
                .Select(k => k!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (manufacturerKeys.Count > 0 && ordered.Count < overallCap)
            {
                var byManufacturerPart = await _db.SupplierProducts
                    .AsNoTracking()
                    .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive
                        && p.ManufacturerPartNumberNormalized != null
                        && manufacturerKeys.Contains(p.ManufacturerPartNumberNormalized))
                    .OrderBy(p => p.Code)
                    .Take(overallCap)
                    .Select(p => Project(p))
                    .ToListAsync(ct);

                foreach (var p in byManufacturerPart)
                {
                    var code = (p.Code ?? string.Empty).Trim();
                    if (code.Length == 0 || !seen.Add(code)) continue;
                    ordered.Add(p);
                    if (ordered.Count >= overallCap) return ordered;
                }
            }

            // ── Pass 2 — TRIGRAM similarity ranking (pg_trgm GIN), per line ────────────────
            // For each unresolved line, rank the catalog by trigram similarity of the product
            // code+name against the line's "buyer code + description" query text, taking the top
            // few. A floor cuts near-zero-similarity noise.
            //
            // INDEX SERVICE — why the % pre-filter exists. A comment here used to claim
            // "similarity over indexed columns"; that was false. The gin_trgm_ops indexes serve
            // the % / LIKE / ILIKE operator family ONLY — a computed similarity() in a WHERE is
            // never index-served, so this pass was one sequential scan of the supplier's whole
            // catalog PER UNRESOLVED LINE, inside the parse job. The % pre-filter below
            // (EF.Functions.TrigramsAreSimilar) is what the index can serve; the similarity()
            // ranking and floor after it are unchanged.
            //
            // THRESHOLD — why SET LOCAL, and why 0.05 rather than 0.1. The % operator honours the
            // pg_trgm.similarity_threshold GUC, whose default is 0.3 — but this pass ranks at
            // >= 0.1, so a bare % pre-filter would silently DROP every candidate scoring in
            // [0.1, 0.3). The GUC is pinned strictly BELOW the floor (0.05) so the pre-filter is
            // provably a superset of the floor whatever pg_trgm's boundary comparison is (> vs >=
            // at exactly the threshold), and the explicit `Score >= similarityFloor` keeps the
            // exact old semantics. SET LOCAL is transaction-scoped, so the changed GUC can never
            // leak back into Npgsql's connection pool. Raw SQL is unavoidable for a GUC (EF cannot
            // express SET); it carries no user input. Pinned, against an independent SQL oracle
            // including a [0.1, 0.3) candidate, by CatalogTrigramIndexUsagePostgresTests.
            const double similarityFloor = 0.1;
            const string pinTrigramThreshold = "SET LOCAL pg_trgm.similarity_threshold = 0.05;";

            await using (var tx = await _db.Database.BeginTransactionAsync(ct))
            {
                await _db.Database.ExecuteSqlRawAsync(pinTrigramThreshold, ct);

                foreach (var q in queries)
                {
                    if (ordered.Count >= overallCap) break;

                    var queryText = $"{(q.BuyerItemCode ?? string.Empty).Trim()} {q.Description}".Trim();
                    if (queryText.Length == 0) continue;

                    var matches = await _db.SupplierProducts
                        .AsNoTracking()
                        .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive
                            // Index-served pre-filter: `%` on the columns the GIN indexes cover.
                            && (EF.Functions.TrigramsAreSimilar(p.Code, queryText)
                                || (p.Name != null && EF.Functions.TrigramsAreSimilar(p.Name, queryText))))
                        .Select(p => new
                        {
                            Product = p,
                            // similarity() over code and name; take the stronger of the two.
                            Score = Math.Max(
                                EF.Functions.TrigramsSimilarity(p.Code, queryText),
                                p.Name == null ? 0d : EF.Functions.TrigramsSimilarity(p.Name, queryText)),
                        })
                        .Where(x => x.Score >= similarityFloor)
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => x.Product.Code)
                        .Take(perQueryTopK)
                        .Select(x => Project(x.Product))
                        .ToListAsync(ct);

                    foreach (var p in matches)
                    {
                        var code = (p.Code ?? string.Empty).Trim();
                        if (code.Length == 0 || !seen.Add(code)) continue;
                        ordered.Add(p);
                        if (ordered.Count >= overallCap) break;
                    }
                }

                // Read-only work; commit just releases the SET LOCAL scope cleanly.
                await tx.CommitAsync(ct);
            }

            return ordered;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or NotSupportedException or NotImplementedException)
        {
            // Defence in depth: if the provider could not translate the trigram query (e.g. an
            // unexpected non-Postgres relational provider, or pg_trgm absent), signal the caller to
            // fall back to the in-memory path rather than failing the whole ingest.
            return null;
        }
    }

    // Project to a detached SupplierProduct carrying only the fields the grounding step needs —
    // matches the existing in-memory projection so downstream behaviour is identical.
    private static SupplierProduct Project(SupplierProduct p) => new()
    {
        Code = p.Code, Name = p.Name, Unit = p.Unit, Price = p.Price, Barcode = p.Barcode,
        // Carried so the grounded candidate can tell the model "this catalog product IS
        // manufacturer part X" — without it a manufacturer-code match is unexpressible.
        ManufacturerPartNumber = p.ManufacturerPartNumber,
        ManufacturerPartNumberNormalized = p.ManufacturerPartNumberNormalized,
        ManufacturerName = p.ManufacturerName,
    };
}
