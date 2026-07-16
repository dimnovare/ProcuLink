namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Safety-net sweep (B5) that recovers orders stranded in <c>delivery_failed</c> whose automatic
/// next-retry was LOST.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> After a transient delivery failure, the failed-attempt write
/// (status → <c>delivery_failed</c>) and the <c>BackgroundJob.Schedule</c> of the next attempt run
/// as SEPARATE units under <c>AutomaticRetry(0)</c> (see <c>RetryDeliveryJob</c> /
/// <c>DeliverOrderJob</c>). A crash or a lost Hangfire enqueue BETWEEN them leaves the order in
/// <c>delivery_failed</c> with attempts still remaining but with NO scheduled or in-flight retry —
/// and nothing else covers <c>delivery_failed</c> (<c>StuckDeliveryDetectionService</c> = <c>delivering</c>,
/// <c>StrandedReadyOrderDetectionService</c> = <c>ready_to_deliver</c>, <c>DeliverySlaService</c>
/// only flags). Without this sweep the order silently stops retrying forever.
/// </para>
/// <para>
/// Re-drives ONLY orders past an <c>agedThreshold</c> that MUST exceed the maximum retry backoff
/// (so a legitimately-scheduled retry is never raced), that are routed, that still have delivery
/// attempts remaining (terminal attempt count below the cap — an order at the cap is already
/// dead-lettered, never re-driven), and that have NO in-flight <c>dispatching</c> attempt. It
/// enqueues the normal <c>RetryDeliveryJob</c> through the retry seam; idempotent —
/// <c>RetryDeliveryAsync</c>'s atomic <c>delivering</c> claim + attempt-cap prevent any double-send,
/// and the swept order's <c>UpdatedAt</c> is bumped so it leaves the aged window.
/// </para>
/// </remarks>
public interface IStrandedFailedDeliveryDetectionService
{
    /// <summary>The most orders one sweep will recover (oldest-stranded first); the rest wait for the next run.</summary>
    const int DefaultMaxBatch = 200;

    /// <summary>
    /// Re-enqueues delivery for orders older than <paramref name="agedThreshold"/> still in
    /// <c>delivery_failed</c> with attempts remaining and no in-flight attempt, writing a
    /// <c>StrandedFailedDeliveryRecovered</c> audit event per order. Bounded to at most
    /// <paramref name="maxBatch"/> orders per sweep (oldest first). Idempotent. Returns the number of
    /// orders acted on.
    /// </summary>
    Task<int> RunAsync(TimeSpan agedThreshold, CancellationToken ct, int maxBatch = DefaultMaxBatch);
}
