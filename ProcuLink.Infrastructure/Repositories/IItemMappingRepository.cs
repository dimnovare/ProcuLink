using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public interface IItemMappingRepository
{
    /// <summary>
    /// Try to get a supplier item code for the given buyer item code (case-insensitive, trimmed)
    /// </summary>
    Task<string?> TryGetSupplierItemCodeAsync(string supplierName, string buyerItemCode, CancellationToken ct = default);

    /// <summary>
    /// List all mappings for a supplier, sorted by BuyerItemCode
    /// </summary>
    Task<IReadOnlyList<ItemCodeMapping>> ListAsync(string supplierName, CancellationToken ct = default);

    /// <summary>
    /// Insert or update a mapping for the given buyer item code (case-insensitive, trimmed)
    /// Returns the upserted mapping
    /// </summary>
    Task<ItemCodeMapping> UpsertAsync(string supplierName, string buyerItemCode, string supplierItemCode, CancellationToken ct = default);
}
