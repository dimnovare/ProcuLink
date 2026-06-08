namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Decouples a background sweep that lives in <c>ProcuLink.Infrastructure</c> (e.g.
/// <c>StuckDeliveryDetectionService</c>) from the Hangfire-bound delivery retry job.
/// The Infrastructure project supplies the concrete adapter that calls
/// <c>RetryDeliveryJob.Enqueue(IBackgroundJobClient, orderId, orgId)</c>; tests supply a
/// recording fake.
/// </summary>
/// <remarks>
/// Mirrors <c>IParseJobEnqueuer</c> (used by <c>StuckOrderDetectionService</c> to re-drive
/// stuck <c>parsing</c> orders). The retry path is idempotent and attempt-capped inside
/// <c>IDeliveryService.RetryDeliveryAsync</c>, so a duplicated enqueue is safe — it sees the
/// higher attempt count and either dead-letters or no-ops.
/// </remarks>
public interface IRetryDeliveryEnqueuer
{
    /// <summary>Enqueues an immediate delivery-retry job for the given order.</summary>
    Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct);
}
