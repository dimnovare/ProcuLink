using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Domain service for a supplier's product catalog — the authoritative set of real
/// codes a supplier can fulfil. All operations are scoped to (orgId, supplierId) to
/// maintain tenant isolation; no method ever reads or writes across that boundary.
/// </summary>
public interface ISupplierCatalogService
{
    /// <summary>
    /// List active catalog products for a supplier, ordered by code. When <paramref name="query"/>
    /// is non-blank, filters to products whose code, name, or barcode contains it
    /// (case-insensitive). <paramref name="take"/> caps the result size (defaults applied by impl).
    /// </summary>
    Task<IReadOnlyList<SupplierProduct>> ListAsync(
        Guid orgId, Guid supplierId, string? query, int? take, CancellationToken ct);

    /// <summary>
    /// Idempotent bulk upsert keyed on (orgId, supplierId, Code): an existing row is updated
    /// in place (and reactivated), an absent row is inserted. Returns (created, updated) counts.
    /// Blank-code drafts are skipped. Within the batch, the last draft for a given code wins.
    /// </summary>
    Task<(int Created, int Updated)> UpsertManyAsync(
        Guid orgId, Guid supplierId, IEnumerable<SupplierProduct> products, CancellationToken ct);

    /// <summary>Count active catalog products for a supplier.</summary>
    Task<int> CountAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>Delete every catalog product for a supplier. Returns the number of rows removed.</summary>
    Task<int> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
