using Hangfire;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (hourly): recovers orders stranded in <c>delivery_failed</c> whose
/// automatic next-retry was LOST — the B5 silent-no-more-retries gap. Thin wrapper over
/// <see cref="IStrandedFailedDeliveryDetectionService"/> so the sweep logic stays unit-tested in the
/// Infrastructure test suite. Idempotent — a recovered order is bumped out of the aged window and
/// <c>RetryDeliveryAsync</c>'s atomic claim + attempt-cap prevent any double-send.
/// </summary>
/// <remarks>
/// Companion to <c>StuckOrderDetectionJob</c> (parsing/transforming), <c>StuckDeliveryDetectionJob</c>
/// (delivering) and <c>StrandedReadyDeliveryDetectionJob</c> (ready_to_deliver): those cover the
/// other transient/stranded states, this closes the last gap where the failed-attempt write and the
/// backoff <c>BackgroundJob.Schedule</c> — separate units under <c>AutomaticRetry(0)</c> — are split
/// by a crash / lost enqueue, leaving the order in <c>delivery_failed</c> with attempts remaining and
/// nothing driving it.
/// </remarks>
public sealed class StrandedFailedDeliveryDetectionJob
{
    /// <summary>
    /// How long an order may sit in 'delivery_failed' before it is treated as a lost-retry strand.
    /// Deliberately well past the maximum retry backoff (BackoffMinutes {30,60,120} → ≤2h) so a
    /// legitimately-scheduled retry — which fires within minutes and moves the order out of
    /// delivery_failed — is NEVER raced; only a genuinely lost reschedule ages this long.
    /// </summary>
    private static readonly TimeSpan AgedThreshold = TimeSpan.FromHours(3);

    private readonly IStrandedFailedDeliveryDetectionService _service;
    private readonly ILogger<StrandedFailedDeliveryDetectionJob> _logger;

    public StrandedFailedDeliveryDetectionJob(
        IStrandedFailedDeliveryDetectionService service,
        ILogger<StrandedFailedDeliveryDetectionJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    // DisableConcurrentExecution: two overlapping sweeps would double-audit the same strand and
    // double-enqueue its recovery (both harmless — the UpdatedAt bump + RetryDeliveryAsync's atomic
    // claim prevent any double-send — but wasteful and noisy). Timeout < the hourly recurrence so a
    // hung run can't block the next tick indefinitely.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var acted = await _service.RunAsync(AgedThreshold, ct);
        if (acted > 0)
            _logger.LogWarning("StrandedFailedDeliveryDetectionJob recovered {Count} stranded delivery_failed order(s).", acted);
        else
            _logger.LogInformation("StrandedFailedDeliveryDetectionJob run complete — no stranded delivery_failed orders.");
    }
}
