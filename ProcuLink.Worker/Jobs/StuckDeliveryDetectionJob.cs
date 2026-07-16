using Hangfire;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (every 15 min): recovers orders stranded in the transient
/// <c>delivering</c> status past <see cref="StuckThreshold"/>. Thin wrapper over
/// <see cref="IStuckDeliveryDetectionService"/> so the sweep logic stays unit-tested in the
/// Infrastructure test suite. Idempotent — a re-driven order is bumped out of the stuck window
/// and a dead-lettered order no longer matches.
/// </summary>
/// <remarks>
/// Companion to <c>StuckOrderDetectionJob</c> (which sweeps <c>parsing</c>/<c>transforming</c>)
/// and <c>DeliverySlaSweepJob</c> (which only flags SLA breaches). This job closes the gap where
/// an order committed to <c>delivering</c> never reaches a terminal status because the dispatch
/// crashed / hung between the status write and the terminal attempt persist.
/// </remarks>
public sealed class StuckDeliveryDetectionJob
{
    /// <summary>How long an order may sit in 'delivering' before it is re-driven / dead-lettered.</summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);

    private readonly IStuckDeliveryDetectionService _service;
    private readonly ILogger<StuckDeliveryDetectionJob> _logger;

    public StuckDeliveryDetectionJob(
        IStuckDeliveryDetectionService service,
        ILogger<StuckDeliveryDetectionJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    // DisableConcurrentExecution: two overlapping sweeps would both act on the same stranded
    // 'delivering' order — double-auditing it and racing its recovery bookkeeping. On OSS Hangfire
    // this keys the lock on TYPE + METHOD only (never on args) — correct for this argument-less
    // cross-tenant sweep. Timeout < the 15-min recurrence so a hung run can't block the next tick.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var acted = await _service.RunAsync(StuckThreshold, ct);
        if (acted > 0)
            _logger.LogWarning("StuckDeliveryDetectionJob acted on {Count} stuck delivering order(s).", acted);
        else
            _logger.LogInformation("StuckDeliveryDetectionJob run complete — no stuck delivering orders.");
    }
}
