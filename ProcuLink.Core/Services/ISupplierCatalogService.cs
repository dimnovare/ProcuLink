using ProcuLink.Core.Catalog;
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
    /// <param name="presentFields">
    /// The columns the import file carried (<c>CatalogFileParseResult.PresentFields</c>). On an
    /// EXISTING row only these fields are overwritten — a field absent from the file keeps its
    /// stored value, a field present with a blank cell is cleared. New rows are always written
    /// from the whole draft. Defaults to <see cref="CatalogFields.All"/>, which overwrites every
    /// field exactly as before presence was tracked.
    /// </param>
    Task<(int Created, int Updated)> UpsertManyAsync(
        Guid orgId, Guid supplierId, IEnumerable<SupplierProduct> products, CancellationToken ct,
        CatalogFields presentFields = CatalogFields.All);

    /// <summary>Count active catalog products for a supplier.</summary>
    Task<int> CountAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>Delete every catalog product for a supplier. Returns the number of rows removed.</summary>
    Task<int> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
