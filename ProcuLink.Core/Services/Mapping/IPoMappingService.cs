namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// CRUD service for per-supplier PO field mapping templates stored as JSONB.
/// </summary>
public interface IPoMappingService
{
    /// <summary>Returns the mapping config for the given supplier, or null if none exists.</summary>
    Task<PoMappingConfig?> GetAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default);

    /// <summary>Creates or replaces the mapping config for the given supplier.</summary>
    Task<PoMappingConfig> UpsertAsync(Guid organisationId, Guid supplierId, PoMappingConfig config, CancellationToken ct = default);

    /// <summary>Deletes the mapping config for the given supplier. No-op if none exists.</summary>
    Task DeleteAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default);
}
