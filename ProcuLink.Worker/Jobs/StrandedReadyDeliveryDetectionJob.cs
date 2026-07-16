using Hangfire;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (every 15 min): recovers orders stranded in <c>ready_to_deliver</c> past
/// <see cref="AgedThreshold"/> whose delivery was never enqueued — the B1 lost-order gap. Thin wrapper
/// over <see cref="IStrandedReadyOrderDetectionService"/> so the sweep logic stays unit-tested in the
/// Infrastructure test suite. Idempotent — a recovered order is bumped out of the aged window and
/// <c>DeliverOrderJob</c>'s own claim prevents any double-send.
/// </summary>
/// <remarks>
/// Companion to <c>StuckOrderDetectionJob</c> (parsing/transforming) and
/// <c>StuckDeliveryDetectionJob</c> (delivering): those cover the other transient states, this closes
/// the gap where an order committed <c>ready_to_deliver</c> + artifact but the follow-on
/// <c>DeliverOrderJob.Enqueue</c> was lost (crash / DB blip). <c>TransformOrderJob</c>'s Skipped path
/// recovers the common case inline; this is the systemic backstop.
/// </remarks>
public sealed class StrandedReadyDeliveryDetectionJob
{
    /// <summary>How long an order may sit in 'ready_to_deliver' before it is treated as a stranded delivery.</summary>
    private static readonly TimeSpan AgedThreshold = TimeSpan.FromMinutes(30);

    private readonly IStrandedReadyOrderDetectionService _service;
    private readonly ILogger<StrandedReadyDeliveryDetectionJob> _logger;

    public StrandedReadyDeliveryDetectionJob(
        IStrandedReadyOrderDetectionService service,
        ILogger<StrandedReadyDeliveryDetectionJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    // DisableConcurrentExecution: two overlapping sweeps would double-audit the same strand and
    // double-enqueue its recovery (both harmless — the UpdatedAt bump + DeliverOrderJob's atomic claim
    // already prevent any double-send — but wasteful and noisy). A 15-min sweep should never overlap;
    // the mutex is cheap insurance against a manual re-trigger or a slow run. Timeout < the 15-min
    // recurrence so a hung run can't block the next tick indefinitely.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var acted = await _service.RunAsync(AgedThreshold, ct);
        if (acted > 0)
            _logger.LogWarning("StrandedReadyDeliveryDetectionJob recovered {Count} stranded ready_to_deliver order(s).", acted);
        else
            _logger.LogInformation("StrandedReadyDeliveryDetectionJob run complete — no stranded ready_to_deliver orders.");
    }
}
