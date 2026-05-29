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

    /// <summary>
    /// Operator-triggered retry/replay of a failed delivery. Idempotent and org-scoped:
    /// counts prior attempts and moves the order to <c>delivery_dead_letter</c> once
    /// <paramref name="maxAttempts"/> is reached. Returns the latest dispatch result.
    /// </summary>
    Task<DeliveryResult> RetryDeliveryAsync(
        Guid orgId,
        Guid orderId,
        int maxAttempts,
        CancellationToken ct);
}
