using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public interface IItemMappingRepository
{
    /// <summary>
    /// Try to get a supplier item code for the given buyer item code
    /// </summary>
    Task<string?> TryGetSupplierItemCodeAsync(string supplierName, string buyerItemCode, CancellationToken ct = default);

    /// <summary>
    /// List all mappings for a supplier
    /// </summary>
    Task<IReadOnlyList<ItemCodeMapping>> ListAsync(string supplierName, CancellationToken ct = default);

    /// <summary>
    /// Insert or update a mapping for the given buyer item code
    /// </summary>
    Task UpsertAsync(string supplierName, string buyerItemCode, string supplierItemCode, CancellationToken ct = default);
}
