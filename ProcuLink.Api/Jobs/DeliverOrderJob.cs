using Hangfire;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Jobs;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: dispatches a transformed outbound artifact through
/// the supplier delivery workflow. The workflow owns delivery state transitions.
///
/// <para>
/// On a transient delivery failure (5xx / network / thrown storage error — <c>DispatchArtifactAsync</c>
/// always returns a failed result without throwing) this job hands the order to the automatic
/// retry queue (<see cref="RetryDeliveryJob"/>) with the first exponential-backoff delay.
/// A genuine BUSINESS rejection — the supplier read the document and refused it — is terminal and
/// left for operator review; a 4xx that refuses the REQUEST (bad credentials, moved endpoint, rate
/// limit) is an ordinary failure and keeps the queue running. See
/// <see cref="SupplierResponseClassification"/>.
/// </para>
/// </summary>
public class DeliverOrderJob
{
    private readonly IDeliveryService _deliveryService;
    private readonly IBillingService _billingService;
    private readonly IBackgroundJobClient _jobs;
    private readonly DeliveryReliabilityOptions _reliability;
    private readonly ILogger<DeliverOrderJob> _logger;

    public DeliverOrderJob(
        IDeliveryService deliveryService,
        IBillingService billingService,
        IBackgroundJobClient jobs,
        ILogger<DeliverOrderJob> logger,
        DeliveryReliabilityOptions? reliability = null)
    {
        _deliveryService = deliveryService;
        _billingService = billingService;
        _jobs = jobs;
        _reliability = reliability ?? new DeliveryReliabilityOptions();
        _logger = logger;
    }

    // No Hangfire AutomaticRetry (mirrors RetryDeliveryJob): DeliveryService turns every
    // failure — including a thrown storage download error — into a failed DeliveryResult,
    // and the single retry authority is the RetryDeliveryJob backoff queue scheduled below.
    // A Hangfire-level retry would re-dispatch on top of that queue (double-delivery risk)
    // and double-count attempts past the dead-letter cap.
    //
    // Attempts = 0 does NOT make this job run-once. It governs only the exception -> FailedState
    // transition; process DEATH throws no exception, so it is not the path it covers. A dead
    // Worker's in-flight job stays invisible for Hangfire.PostgreSql's InvisibilityTimeout
    // (default 30 min — Program.cs configures a bare UseNpgsqlConnection, so the default stands,
    // non-sliding) and is then REFETCHED and re-executed. The per-order mutex below does not stop
    // that either: the dead process released its lock. Re-dispatch after a crash is therefore
    // expected and by design — delivery here is AT-LEAST-ONCE, and duplicate-ORDER suppression
    // rests on the deterministic idempotency key the re-send carries (see DispatchArtifactAsync)
    // plus supplier-side de-duplication, NOT on this attribute. One carve-out: if the stuck sweep's
    // re-drive PARKED the order (delivery_unconfirmed) before the refetch runs, a refetched
    // AUTOMATIC activation (requireAutoDeliver: true) is refused by the dispatch claim — the parked
    // row is terminal, so the re-adopt park guard could not have caught the refetch, and re-sending
    // a park is the operator's call alone (DispatchArtifactAsync's claim comment).
    //
    // PerOrderDistributedMutex (D-1) serialises activations PER ORDER (storage-backed distributed
    // lock keyed on the orderId argument, index 0). Two DeliverOrderJob activations for the SAME
    // order — a double-clicked Redeliver, a Redeliver racing an ops Requeue — cannot run this body
    // concurrently and double-dispatch. It shares the SAME lock resource key as RetryDeliveryJob
    // ("retry-delivery:order:{orderId}"), so a DeliverOrderJob also can't interleave with a
    // scheduled RetryDeliveryJob for that order. This is the OUTER guard; DispatchArtifactAsync's
    // atomic 'delivering' claim is the INNER, cross-process-correct guard (defence in depth).
    [Queue("critical")]
    [AutomaticRetry(Attempts = 0)]
    [PerOrderDistributedMutex(orderArgumentIndex: 0)]
    public async Task ExecuteAsync(
        Guid orderId,
        Guid organisationId,
        Guid artifactId,
        bool requireAutoDeliver,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "DeliverOrderJob starting for order {OrderId}, artifact {ArtifactId}, requireAutoDeliver={RequireAutoDeliver}",
            orderId,
            artifactId,
            requireAutoDeliver);

        if (!await _billingService.CanProcessOrdersAsync(organisationId, ct))
        {
            // Correct NOT to deliver for a non-paying org — but do NOT silently strand the order
            // in ready_to_deliver (invisible, never re-driven). Move it to the explicit
            // 'delivery_held' status; it is auto-released + re-driven when the org returns to
            // good standing (BillingController reactivation → ReleaseBillingHeldOrdersAsync).
            var held = await _deliveryService.HoldForBillingAsync(organisationId, orderId, ct);
            _logger.LogWarning(
                "DeliverOrderJob held order {OrderId}: billing account cannot process orders (held={Held}).",
                orderId, held);
            return;
        }

        var result = await _deliveryService.DispatchArtifactAsync(
            organisationId,
            orderId,
            artifactId,
            requireAutoDeliver,
            ct);

        if (result.Success)
            return;

        // Nothing was dispatched and no later attempt can help (order/artifact gone, terminal, held,
        // or parked with an outcome nobody observed). Seeding the backoff queue here hands
        // RetryDeliveryJob an order it can only bow out of. On every case but the park no attempt row
        // exists either, so the count is frozen at 0 and neither job's cap guard can ever end the
        // chain; the park instead must never be re-sent automatically at all, because a duplicate PO
        // is the one thing it exists to prevent. Whoever owns the block — an operator, for the park —
        // owns re-driving it. Mirrors RetryDeliveryJob's identical guard.
        //
        // A lost claim never reaches this branch: DispatchArtifactAsync returns Success=true for it
        // (a benign no-op), so the Success check above already returned.
        //
        // Checked BEFORE the failure log below, not after: nothing was dispatched on ANY of these
        // paths, so "finished with delivery failure" is false for all of them — and actively
        // misleading for a park, which is a deferral to a human, not a failure. This is the log an
        // operator reads during the incident.
        if (result.Outcome == DeliveryOutcome.NotRetryable)
        {
            _logger.LogWarning(
                "DeliverOrderJob: order {OrderId} was not dispatched and no retry can move it ({Error}); "
                + "not scheduling one. Whoever owns the block — an operator, for a parked order — owns re-driving it.",
                orderId,
                result.ErrorMessage);
            return;
        }

        _logger.LogWarning(
            "DeliverOrderJob finished with delivery failure for order {OrderId}: {Error}",
            orderId,
            result.ErrorMessage);

        // A BUSINESS rejection — the supplier read the document and refused it (422, or a 400
        // carrying their reason) — cannot be helped by re-sending the same bytes. It stops here and
        // waits for a CORRECTED document, which the operator drives (resolve / re-transform).
        //
        // Everything else keeps the backoff queue running, including the 4xx codes that refuse the
        // REQUEST rather than the order. This gate read `result.ResponseCode is >= 400 and <= 499`
        // until WP-19: an expired API key, a moved endpoint or a rate limit abandoned an order that
        // a key rotation would have delivered — in a status (rejected_by_supplier) that had no exit
        // either. The predicate is shared with DeliveryService's status decision and
        // RetryDeliveryJob's identical gate, so the three cannot disagree.
        if (SupplierResponseClassification.SuppressesAutomaticRetry(result.ResponseCode, result.ResponseBody))
            return;

        var maxAttempts = _reliability.MaxAttempts > 0 ? _reliability.MaxAttempts : RetryDeliveryJob.MaxAttempts;
        var attemptsMade = await _deliveryService.CountDeliveryAttemptsAsync(organisationId, orderId, ct);
        if (attemptsMade >= maxAttempts)
        {
            _logger.LogWarning(
                "DeliverOrderJob: order {OrderId} already at attempt cap ({Max}); not scheduling auto-retry.",
                orderId, maxAttempts);
            return;
        }

        var delay = _reliability.BackoffFor(attemptsMade);
        RetryDeliveryJob.ScheduleRetry(_jobs, orderId, organisationId, delay);
        _logger.LogInformation(
            "DeliverOrderJob: scheduled automatic retry #{Next} for order {OrderId} in {Delay}.",
            attemptsMade + 1, orderId, delay);
    }

    /// <summary>Enqueue automatic post-transform delivery (respects AutoDeliver flag).</summary>
    public static void Enqueue(
        IBackgroundJobClient jobs,
        Guid orderId,
        Guid organisationId,
        Guid artifactId)
    {
        jobs.Enqueue<DeliverOrderJob>(j =>
            j.ExecuteAsync(orderId, organisationId, artifactId, true, CancellationToken.None));
    }

    /// <summary>Enqueue a forced redeliver (bypasses AutoDeliver flag).</summary>
    public static void EnqueueRedeliver(
        IBackgroundJobClient jobs,
        Guid orderId,
        Guid organisationId,
        Guid artifactId)
    {
        jobs.Enqueue<DeliverOrderJob>(j =>
            j.ExecuteAsync(orderId, organisationId, artifactId, false, CancellationToken.None));
    }
}
