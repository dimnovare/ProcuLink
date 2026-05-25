using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Delivery;

public interface IDeliveryConfigService
{
    Task<SupplierDeliveryConfig?> GetEntityAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    Task<DeliveryConfigResponse?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    Task<DeliveryConfigResponse> UpsertAsync(
        Guid orgId,
        Guid supplierId,
        UpsertDeliveryConfigRequest request,
        CancellationToken ct);

    Task DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
