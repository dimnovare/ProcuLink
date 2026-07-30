using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Domain service for buyer → supplier item code mappings.
/// All operations are scoped to (orgId, supplierId) to maintain tenant isolation.
/// </summary>
public interface IItemMappingService
{
    /// <summary>
    /// Exact-match lookup of a supplier item code for the given buyer item code.
    /// Returns null when no mapping exists — never throws for a missing mapping.
    /// </summary>
    Task<string?> ResolveAsync(
        Guid orgId, Guid supplierId, string buyerItemCode, CancellationToken ct);

    /// <summary>
    /// Batch exact-match lookup. Resolves every supplied buyer item code in a single
    /// org+supplier-scoped <c>WHERE BuyerItemCode IN (...)</c> query, avoiding N
    /// round-trips when auto-resolving a multi-line order.
    /// </summary>
    /// <returns>
    /// A dictionary keyed by the <b>trimmed, exactly-as-supplied</b> buyer item code, compared
    /// <see cref="StringComparer.Ordinal"/>. Every requested, non-blank code is present as its own
    /// key — two spellings that differ only in case are two keys, because they may be two different
    /// lines on the order and each must get its own answer. The value is the resolved supplier item
    /// code, or <c>null</c> when no mapping exists. Blank/whitespace codes are skipped.
    ///
    /// <para>Code MATCHING against stored rows is case-insensitive
    /// (<see cref="ProcuLink.Core.Catalog.ItemCodeComparison"/>) and identical to
    /// <see cref="ResolveAsync"/>, including the tie-break when case-variant rows exist. The case
    /// rule lives in the match, never in the keying — see the implementation for why folding the
    /// keys orders the wrong item.</para>
    /// </returns>
    Task<IReadOnlyDictionary<string, string?>> ResolveManyAsync(
        Guid orgId, Guid supplierId, IEnumerable<string> buyerItemCodes, CancellationToken ct);

    /// <summary>Insert or update a mapping. Updates confidence and source if the row exists.</summary>
    Task UpsertAsync(
        Guid orgId, Guid supplierId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct);

    /// <summary>All mappings for a supplier, ordered by buyer item code.</summary>
    Task<IReadOnlyList<ItemMapping>> GetForSupplierAsync(
        Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>Permanently delete a mapping by its primary key.</summary>
    Task DeleteAsync(Guid orgId, Guid mappingId, CancellationToken ct);

    /// <summary>
    /// Create a mapping. Applies the SAME case rule as <see cref="UpsertAsync"/>: when a row for
    /// the code already exists under a different spelling it is UPDATED and returned, so this can
    /// never create a case-variant twin. Returns the created-or-updated entity.
    /// </summary>
    Task<ItemMapping> CreateAsync(
        Guid orgId, Guid supplierId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct);

    /// <summary>
    /// Update buyer and supplier codes by mapping ID. Returns <c>null</c> when the mapping is not
    /// found, OR when the new buyer code collides — under the shared case rule — with a DIFFERENT
    /// mapping for the same supplier (merging would delete a row, which is a founder decision).
    /// Re-casing a mapping's own code is allowed.
    /// </summary>
    Task<ItemMapping?> UpdateByIdAsync(
        Guid orgId, Guid mappingId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct);

    /// <summary>
    /// DETECTION ONLY — the case-variant twins this organisation already holds, grouped by
    /// <c>(supplier_id, lower(buyer_item_code))</c> with more than one row. Never merges, never
    /// deletes: what to do with an existing twin is a decision about a customer's data.
    /// </summary>
    Task<IReadOnlyList<ItemCodeTwinGroup>> FindCaseVariantTwinsAsync(
        Guid orgId, CancellationToken ct);
}

/// <summary>
/// One group of learned mappings whose buyer item codes differ only in case — rows written before
/// the write paths shared a case rule. The resolver picks between them deterministically
/// (exact-case, then most recently updated, then id), so a twin is a data-hygiene report, not an
/// outage.
/// </summary>
/// <param name="SupplierId">The supplier the twins belong to.</param>
/// <param name="FoldedCode">The lower-cased code the rows collapse onto.</param>
/// <param name="RowCount">How many rows share it.</param>
/// <param name="Spellings">Every stored spelling, ordinal-sorted for a stable report.</param>
public sealed record ItemCodeTwinGroup(
    Guid SupplierId, string FoldedCode, int RowCount, IReadOnlyList<string> Spellings);
