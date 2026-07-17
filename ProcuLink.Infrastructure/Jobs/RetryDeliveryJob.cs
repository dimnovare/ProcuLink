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
/// <b>Automatic retry queue:</b> when a retry fails for a transient reason (5xx / network —
/// not an explicit 4xx supplier rejection) and attempts remain below the cap, the job
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
/// So the queue stops on: delivered, a 4xx supplier rejection, the attempt cap, and NotRetryable.
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
            // Nothing was dispatched, no attempt row was written, and no later retry can change that
            // (gone / delivered / dead-lettered / held for billing / no artifact / past the cap).
            // The frozen attempt count means the cap guard below could never stop the chain, so
            // rescheduling would re-run this same no-op every backoff step, forever. Stop; whoever
            // owns the block (a billing reactivation re-drive, a re-transform, an operator) owns
            // restarting delivery.
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

        if (IsSupplierRejection(result.ResponseCode))
        {
            // 4xx: the supplier received and explicitly refused the payload. Retrying the same
            // bytes will not help — stop the automatic queue and leave it for operator review.
            _logger.LogWarning(
                "RetryDeliveryJob: order {OrderId} rejected by supplier (HTTP {Code}); not rescheduling.",
                orderId, result.ResponseCode);
            return;
        }

        if (attemptsMade >= maxAttempts)
        {
            // RetryDeliveryAsync already dead-lettered at the cap; nothing more to schedule.
            _logger.LogWarning(
                "RetryDeliveryJob: order {OrderId} reached the attempt cap ({Max}); dead-lettered.",
                orderId, maxAttempts);
            return;
        }

        var delay = _options.BackoffFor(attemptsMade);
        ScheduleRetry(_jobs, orderId, organisationId, delay);
        _logger.LogWarning(
            "RetryDeliveryJob: order {OrderId} delivery failed ({Error}); scheduled retry #{Next} in {Delay}.",
            orderId, result.ErrorMessage, attemptsMade + 1, delay);
    }

    private static bool IsSupplierRejection(int? responseCode) =>
        responseCode is >= 400 and <= 499;

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
