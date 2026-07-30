using Hangfire;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: delivery retry/replay with an automatic exponential-backoff queue and
/// dead-letter escalation. Delegates to <see cref="IDeliveryService.RetryDeliveryAsync"/>,
/// which is idempotent and org-scoped and owns the attempt-count / dead-letter state
/// transitions. After <see cref="DeliveryReliabilityOptions.MaxAttempts"/> failed delivery
/// attempts the order is moved to <c>delivery_dead_letter</c> and is no longer retried.
///
/// <para>
/// <b>Automatic retry queue:</b> when a retry fails for any reason the supplier did not attribute
/// to the ORDER (5xx, network, or a 4xx that refuses the REQUEST — bad credentials, moved endpoint,
/// rate limit) and attempts remain below the cap, the job
/// <see cref="BackgroundJob.Schedule(System.Linq.Expressions.Expression{Func{System.Threading.Tasks.Task}}, TimeSpan)">schedules</see>
/// the next attempt after the configured exponential backoff (~30 → 60 → 120 min). The
/// attempt-count guard inside <c>RetryDeliveryAsync</c> makes the whole chain idempotent:
/// a duplicated job sees the higher attempt count and either dead-letters or no-ops.
/// </para>
///
/// <para>
/// <b>The queue terminates on <see cref="DeliveryOutcome.NotRetryable"/>, never on a bare
/// non-dispatch.</b> A NotRetryable result wrote no attempt row AND can never be helped by a later
/// attempt, so the count driving BOTH the backoff step and the cap is frozen — rescheduling it is an
/// unbounded ~30-min loop, not a retry. A <see cref="DeliveryOutcome.ClaimLost"/> result also wrote
/// no attempt row, but it MUST still be rescheduled: it is the only thing that carries a crashed
/// holder's order past the reclaim window (see <c>CrashedHolderRecoveryCompositionPostgresTests</c>).
/// So the queue stops on: delivered, a genuine BUSINESS rejection, the attempt cap, and NotRetryable.
/// </para>
/// </summary>
public class RetryDeliveryJob
{
    /// <summary>Total delivery attempts allowed before dead-lettering. Mirrors <see cref="DeliveryReliabilityOptions.MaxAttempts"/>.</summary>
    public const int MaxAttempts = 3;

    private readonly IDeliveryService _delivery;
    private readonly IBackgroundJobClient _jobs;
    private readonly DeliveryReliabilityOptions _options;
    private readonly ILogger<RetryDeliveryJob> _logger;

    public RetryDeliveryJob(
        IDeliveryService delivery,
        IBackgroundJobClient jobs,
        ILogger<RetryDeliveryJob> logger,
        DeliveryReliabilityOptions? options = null)
    {
        _delivery = delivery;
        _jobs = jobs;
        _options = options ?? new DeliveryReliabilityOptions { MaxAttempts = MaxAttempts };
        _logger = logger;
    }

    // No Hangfire AutomaticRetry: retry/backoff semantics live inside RetryDeliveryAsync
    // (attempt cap + dead-letter) and the explicit BackoffFor() schedule below, not in
    // Hangfire's own retry queue. A Hangfire-level retry would double-count attempts.
    //
    // PerOrderDistributedMutex serialises activations PER ORDER (storage-backed distributed lock
    // keyed on the orderId argument): two jobs for the SAME order — a duplicated activation, or a
    // backoff-scheduled run racing the operator "Retry now" button — cannot run this body
    // concurrently and double-dispatch. This is the OUTER guard; the atomic delivering-claim
    // inside RetryDeliveryAsync is the INNER, cross-process-correct guard (defence in depth).
    // Distinct orders still retry fully in parallel.
    [Queue("delivery-retry")]
    [AutomaticRetry(Attempts = 0)]
    [PerOrderDistributedMutex(orderArgumentIndex: 0, timeoutSeconds: 60)]
    public async Task ExecuteAsync(Guid orderId, Guid organisationId, CancellationToken ct)
    {
        var maxAttempts = _options.MaxAttempts > 0 ? _options.MaxAttempts : MaxAttempts;

        _logger.LogInformation(
            "RetryDeliveryJob starting for order {OrderId}, org {OrgId}", orderId, organisationId);

        var result = await _delivery.RetryDeliveryAsync(organisationId, orderId, maxAttempts, ct);

        if (result.Success)
        {
            _logger.LogInformation("RetryDeliveryJob delivered order {OrderId}", orderId);
            return;
        }

        if (result.Outcome == DeliveryOutcome.NotRetryable)
        {
            // Nothing was dispatched and no later retry can change that (gone / delivered /
            // dead-lettered / held for billing / no artifact / past the cap / parked with an outcome
            // nobody observed). On every one of those but the park no attempt row was written, so the
            // frozen count means the cap guard below could never stop the chain and rescheduling
            // would re-run this same no-op every backoff step, forever. The park stops here for a
            // stronger reason — re-sending it risks a duplicate PO, so only a human may re-drive it.
            // Either way: stop; whoever owns the block (a billing reactivation re-drive, a
            // re-transform, an operator) owns restarting delivery.
            //
            // ClaimLost deliberately does NOT return here: it is transient, and its reschedule is
            // the crash-recovery net (see DeliveryOutcome.ClaimLost) — it falls through to the
            // backoff below.
            _logger.LogInformation(
                "RetryDeliveryJob: order {OrderId} not dispatched and not retryable ({Error}); not rescheduling.",
                orderId, result.ErrorMessage);
            return;
        }

        // Count attempts already made so we know which backoff step is next and whether
        // the cap has been reached. RetryDeliveryAsync has just persisted this attempt.
        var attemptsMade = await _delivery.CountDeliveryAttemptsAsync(organisationId, orderId, ct);

        if (IsBusinessRejection(result))
        {
            // The supplier READ the document and refused it (422, or a 400 carrying their reason).
            // Retrying the same bytes cannot help — stop the queue; the cure is a CORRECTED
            // document, which the operator drives via resolve / re-transform.
            _logger.LogWarning(
                "RetryDeliveryJob: order {OrderId} rejected by supplier (HTTP {Code}); not rescheduling.",
                orderId, result.ResponseCode);
            return;
        }

        if (attemptsMade >= maxAttempts)
        {
            // Do NOT claim the order was dead-lettered here: RetryDeliveryAsync dead-letters at the
            // cap only on the paths that reach its own cap check, and returns earlier (leaving the
            // status untouched) for e.g. a lost claim or a missing artifact. Report what this job
            // actually decided — the status is the order's own record.
            _logger.LogWarning(
                "RetryDeliveryJob: order {OrderId} is at the attempt cap ({Max}); no further retry scheduled. Last error: {Error}",
                orderId, maxAttempts, result.ErrorMessage);
            return;
        }

        // Jittered, and never sooner than a Retry-After the supplier sent — the burst-breaking half
        // of the backoff. See DeliveryReliabilityOptions.NextRetryDelay for why a fixed schedule
        // re-creates the thundering herd that caused the failure.
        var delay = _options.NextRetryDelay(attemptsMade, result.RetryAfter, Random.Shared.NextDouble());
        ScheduleRetry(_jobs, orderId, organisationId, delay);
        _logger.LogWarning(
            "RetryDeliveryJob: order {OrderId} delivery failed ({Error}); scheduled retry #{Next} in {Delay}.",
            orderId, result.ErrorMessage, attemptsMade + 1, delay);
    }

    /// <summary>
    /// Read `responseCode is >= 400 and <= 499` until WP-19, which is how an expired API key, a
    /// moved endpoint or a rate limit abandoned an order the queue could still have delivered — in
    /// a status (<c>rejected_by_supplier</c>) that had no exit either. The predicate now comes from
    /// the one table, shared with <c>DeliveryService</c>'s status decision and
    /// <c>DeliverOrderJob</c>'s identical first-failure gate, so the three cannot disagree.
    /// </summary>
    private static bool IsBusinessRejection(DeliveryResult result) =>
        SupplierResponseClassification.SuppressesAutomaticRetry(result);

    /// <summary>Enqueue an immediate operator-triggered retry.</summary>
    public static void Enqueue(IBackgroundJobClient jobs, Guid orderId, Guid organisationId)
    {
        jobs.Enqueue<RetryDeliveryJob>(j => j.ExecuteAsync(orderId, organisationId, CancellationToken.None));
    }

    /// <summary>Schedule the next automatic retry after the given backoff delay.</summary>
    public static void ScheduleRetry(
        IBackgroundJobClient jobs, Guid orderId, Guid organisationId, TimeSpan delay)
    {
        jobs.Schedule<RetryDeliveryJob>(
            j => j.ExecuteAsync(orderId, organisationId, CancellationToken.None), delay);
    }
}
