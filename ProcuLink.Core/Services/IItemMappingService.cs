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
    /// A dictionary keyed by the <b>trimmed</b> buyer item code. Every requested,
    /// non-blank code is present as a key; the value is the resolved supplier item
    /// code, or <c>null</c> when no mapping exists. Blank/whitespace codes are skipped.
    /// Buyer-code matching is case-sensitive, identical to <see cref="ResolveAsync"/>.
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

    /// <summary>Create a new mapping. Returns the created entity.</summary>
    Task<ItemMapping> CreateAsync(
        Guid orgId, Guid supplierId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct);

    /// <summary>Update buyer and supplier codes by mapping ID. Returns null when not found.</summary>
    Task<ItemMapping?> UpdateByIdAsync(
        Guid orgId, Guid mappingId,
        string buyerItemCode, string supplierItemCode,
        MappingSource source, CancellationToken ct);
}
