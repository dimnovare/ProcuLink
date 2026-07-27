using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// One unresolved line's retrieval query: the buyer's item code (a strong exact-match /
/// barcode key) plus its free-text description (the fuzzy signal). Carries the
/// <see cref="LineNumber"/> only for callers that want to correlate; retrieval itself is
/// order-independent.
/// </summary>
/// <remarks>
/// <paramref name="ManufacturerPartNumber"/> is a THIRD exact key alongside the buyer code and
/// barcode. It is the only one that survives a punchout order, whose buyer code is the buying
/// network's internal id. Appended last + defaulted so existing positional constructions are
/// unaffected.
/// </remarks>
public sealed record CatalogRetrievalQuery(
    int LineNumber,
    string BuyerItemCode,
    string? Description,
    string? ManufacturerPartNumber = null);

/// <summary>
/// Group V10 — indexed catalog retrieval at scale. For large supplier catalogs (Baltic IT
/// distributors carry tens of thousands of SKUs), loading every <see cref="SupplierProduct"/>
/// into memory silently truncates at a hard cap. This service instead retrieves the closest
/// real products with INDEXED PostgreSQL queries: an EXACT code/barcode match first (served by
/// the unique / btree indexes), then trigram similarity ranking (pg_trgm GIN indexes) for the
/// fuzzy remainder — without ever materialising the whole catalog.
///
/// All queries are strictly scoped to (orgId, supplierId) — never cross-tenant.
///
/// IMPORTANT (provider safety): trigram ranking uses Postgres-only functions that DO NOT
/// translate on the EF InMemory provider used by the unit tests. Implementations MUST detect a
/// non-Postgres provider (and defensively catch a translation failure) and signal the caller to
/// fall back to the existing in-memory lexical path, so tests and non-Postgres dev keep working.
/// </summary>
public interface ICatalogRetrievalService
{
    /// <summary>
    /// True only when this service can serve indexed retrieval for the given context — i.e. the
    /// active catalog for (orgId, supplierId) EXCEEDS <paramref name="inMemoryThreshold"/> AND the
    /// underlying provider supports the indexed (trigram) path. When false, the caller keeps the
    /// existing bounded in-memory lexical retrieval, so small-catalog behaviour is byte-identical.
    /// </summary>
    Task<bool> ShouldUseIndexedRetrievalAsync(
        Guid orgId, Guid supplierId, int inMemoryThreshold, CancellationToken ct);

    /// <summary>
    /// Retrieve the top candidate products for the given unresolved lines using indexed queries:
    /// exact code/barcode matches first, then trigram-ranked fuzzy candidates. Returns a deduped,
    /// rank-ordered union (exact matches ahead of fuzzy) capped to <paramref name="overallCap"/>.
    /// Each line contributes at most <paramref name="perQueryTopK"/> fuzzy candidates.
    ///
    /// Returns <c>null</c> when the indexed path is unavailable (non-Postgres provider, or a
    /// translation failure) — the caller MUST then fall back to the in-memory path.
    /// </summary>
    Task<IReadOnlyList<SupplierProduct>?> RetrieveCandidatesAsync(
        Guid orgId,
        Guid supplierId,
        IReadOnlyList<CatalogRetrievalQuery> queries,
        int perQueryTopK,
        int overallCap,
        CancellationToken ct);
}
