namespace ProcuLink.Core.Services.Delivery;

public interface IDeliveryService
{
    Task<DeliveryResult> DispatchArtifactAsync(
        Guid orgId,
        Guid orderId,
        Guid artifactId,
        bool requireAutoDeliver,
        CancellationToken ct);

    Task<DeliveryTestResult> TestFireAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
