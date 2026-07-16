namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Decouples a background sweep that lives in <c>ProcuLink.Infrastructure</c> (e.g.
/// <c>StrandedReadyOrderDetectionService</c>) from the Hangfire-bound FIRST-delivery job
/// (<c>DeliverOrderJob</c>). The Api project supplies the concrete adapter that calls
/// <c>DeliverOrderJob.Enqueue(IBackgroundJobClient, orderId, orgId, artifactId)</c>; tests supply a
/// recording fake.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IRetryDeliveryEnqueuer"/>: that seam drives <c>RetryDeliveryJob</c>,
/// which BYPASSES the supplier's <c>AutoDeliver</c> flag (retry/redeliver semantics). THIS seam
/// drives the normal first-delivery path (<c>requireAutoDeliver: true</c>), which is the correct
/// recovery for an order stranded in <c>ready_to_deliver</c> whose delivery was never enqueued: it
/// respects <c>AutoDeliver</c> (a manual order is a benign no-op) rather than force-sending it.
/// </para>
/// <para>
/// Idempotent by construction — <c>DeliverOrderJob</c>'s own atomic <c>delivering</c> claim (+
/// per-order distributed mutex) prevents any double-send, so a duplicated enqueue is safe.
/// </para>
/// </remarks>
public interface IDeliveryDispatchEnqueuer
{
    /// <summary>Enqueues the normal (AutoDeliver-respecting) delivery job for the given order + artifact.</summary>
    Task EnqueueAsync(Guid orderId, Guid orgId, Guid artifactId, CancellationToken ct);
}
