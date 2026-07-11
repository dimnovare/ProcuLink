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

    /// <summary>
    /// Org-scoped count of real (order-linked) delivery attempts for an order. Used by the
    /// automatic retry queue to pick the next exponential-backoff step and detect the cap.
    /// </summary>
    Task<int> CountDeliveryAttemptsAsync(Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// Move a transform-ready order to the explicit <c>delivery_held</c> status because the org
    /// cannot currently process orders (a billing flip between transform and the delivery job).
    /// Idempotent + org-scoped: only a <c>ready_to_deliver</c> order is held (any other status is
    /// a no-op), and an audit event records the hold so it is VISIBLE — never a silent drop.
    /// Returns true when the order was newly held.
    /// </summary>
    Task<bool> HoldForBillingAsync(Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// Release every <c>delivery_held</c> order for an org back to <c>ready_to_deliver</c> and
    /// re-drive delivery (via the retry seam), called when the org returns to a processing state.
    /// Idempotent + org-scoped; returns the number of orders released.
    /// </summary>
    Task<int> ReleaseBillingHeldOrdersAsync(Guid orgId, CancellationToken ct);
}
