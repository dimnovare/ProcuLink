namespace ProcuLink.Core.Services.Mapping;

public interface IPoMappingService
{
    Task<PoMappingConfig?> GetAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default);
    Task<PoMappingConfig> UpsertAsync(Guid organisationId, Guid supplierId, PoMappingConfig config, CancellationToken ct = default);
    Task DeleteAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default);
}
