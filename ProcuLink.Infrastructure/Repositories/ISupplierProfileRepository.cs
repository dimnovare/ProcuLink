using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public interface ISupplierProfileRepository
{
    /// <summary>
    /// List all supplier profiles
    /// </summary>
    Task<IReadOnlyList<SupplierProfile>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Get a supplier profile by name (case-insensitive)
    /// </summary>
    Task<SupplierProfile?> GetByNameAsync(string supplierName, CancellationToken ct = default);

    /// <summary>
    /// Create or update a supplier profile. Returns the saved profile.
    /// </summary>
    Task<SupplierProfile> UpsertAsync(SupplierProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Delete a supplier profile by name. Returns true if deleted, false if not found.
    /// </summary>
    Task<bool> DeleteAsync(string supplierName, CancellationToken ct = default);
}
