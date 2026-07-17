namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Safety-net sweep that recovers orders stranded in <c>ready_to_deliver</c> whose delivery was
/// NEVER enqueued — the B1 lost-order gap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>TransformOrderJob</c> commits <c>ready_to_deliver</c> + the artifact,
/// then enqueues <c>DeliverOrderJob</c> AFTER that commit. A crash / DB blip in that gap leaves the
/// order in <c>ready_to_deliver</c> with the delivery never enqueued; a Hangfire retry's transform
/// claim then matches 0 rows and returns Skipped. No other sweep covers <c>ready_to_deliver</c>
/// (<c>StuckOrderDetectionService</c> = parsing/transforming, <c>StuckDeliveryDetectionService</c> =
/// delivering, <c>DeliverySlaService</c> needs <c>DeliveryDueAt</c>), so the PO is stranded forever,
/// invisibly. <c>TransformOrderJob</c>'s Skipped path re-enqueues the common case; this sweep is the
/// systemic backstop for ANY path that leaves such a strand.
/// </para>
/// <para>
/// Recovers ONLY orders configured to AUTO-deliver (a manual order legitimately rests in
/// <c>ready_to_deliver</c> awaiting a manual send and must never be force-dispatched) whose CURRENT
/// artifact has no delivery attempt against it (the "this payload was never dispatched" signature).
/// Attempts against EARLIER artifacts are deliberately ignored: an attempt row proves neither that a
/// send happened (four pre-dispatch paths persist rows having sent zero bytes) nor, if one did, that
/// it sent the current payload — a re-transform mints a new artifact and nothing deletes the old rows.
/// Re-drives them via the normal first-delivery job. Idempotent — <c>DeliverOrderJob</c>'s atomic claim
/// prevents any double-send, and the swept order's <c>UpdatedAt</c> is bumped so it leaves the aged window.
/// </para>
/// </remarks>
public interface IStrandedReadyOrderDetectionService
{
    /// <summary>The most orders one sweep will recover (oldest-stranded first); the rest wait for the next run.</summary>
    const int DefaultMaxBatch = 200;

    /// <summary>
    /// Re-enqueues delivery for orders older than <paramref name="agedThreshold"/> still in
    /// <c>ready_to_deliver</c> that are configured to auto-deliver and whose current artifact has no
    /// delivery attempt against it (attempts against earlier artifacts do not block recovery),
    /// writing a <c>StrandedReadyDeliveryRecovered</c> audit event per order. Bounded to at most
    /// <paramref name="maxBatch"/> orders per sweep (oldest first). Idempotent. Returns the number of
    /// orders acted on.
    /// </summary>
    Task<int> RunAsync(TimeSpan agedThreshold, CancellationToken ct, int maxBatch = DefaultMaxBatch);
}
