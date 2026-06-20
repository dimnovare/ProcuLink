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
/// A 4xx supplier rejection is terminal and is left for operator review.
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
            _logger.LogWarning(
                "DeliverOrderJob skipped for order {OrderId}: billing account cannot process orders",
                orderId);
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

        _logger.LogWarning(
            "DeliverOrderJob finished with delivery failure for order {OrderId}: {Error}",
            orderId,
            result.ErrorMessage);

        // A 4xx is an explicit supplier rejection — retrying the same payload won't help, so it
        // is left for operator review (status 'rejected_by_supplier'). Only transient failures
        // (5xx / network, no 4xx code) enter the automatic backoff queue.
        if (result.ResponseCode is >= 400 and <= 499)
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
