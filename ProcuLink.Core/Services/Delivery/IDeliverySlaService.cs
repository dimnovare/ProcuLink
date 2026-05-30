namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// SLA timers: flags orders whose delivery confirmation window
/// (<c>PurchaseOrderEntity.DeliveryDueAt</c>) has elapsed without a confirmed delivery.
/// Runs as a recurring cross-tenant Hangfire sweep; each flagged order and its audit event
/// carry that order's own OrgId, so tenant isolation is preserved.
/// </summary>
public interface IDeliverySlaService
{
    /// <summary>
    /// Sets <c>SlaBreached = true</c> on every order whose <c>DeliveryDueAt</c> is in the past,
    /// that is not yet flagged, and that is still awaiting confirmation (not delivered / dead-lettered),
    /// writing a <c>DeliverySlaBreached</c> audit event per order. Idempotent — an already-flagged
    /// order no longer matches. Returns the number newly flagged.
    /// </summary>
    Task<int> RunAsync(CancellationToken ct);
}
