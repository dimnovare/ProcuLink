using Hangfire;
using ProcuLink.Core.Services;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (every 15 min): recovers or fails orders that have been
/// stuck in a transient pipeline status ('parsing' / 'transforming') for longer than
/// <see cref="StuckThreshold"/>. A strand is first re-queued up to a cap; past the cap a
/// 'parsing' strand is dead-lettered to terminal 'failed', while a 'transforming' strand
/// is recovered to the re-sendable 'ready' state (never falsely failed). Thin wrapper over
/// <see cref="IStuckOrderDetectionService"/> so the sweep logic stays unit-tested in the
/// Infrastructure test suite. Idempotent — a recovered/failed order no longer matches.
/// </summary>
public sealed class StuckOrderDetectionJob
{
    /// <summary>How long an order may sit in parsing/transforming before it is recovered or failed.</summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);

    private readonly IStuckOrderDetectionService _service;
    private readonly ILogger<StuckOrderDetectionJob> _logger;

    public StuckOrderDetectionJob(
        IStuckOrderDetectionService service,
        ILogger<StuckOrderDetectionJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var marked = await _service.RunAsync(StuckThreshold, ct);
        if (marked > 0)
            _logger.LogWarning("StuckOrderDetectionJob acted on {Count} stuck order(s) (requeued, recovered, or failed).", marked);
        else
            _logger.LogInformation("StuckOrderDetectionJob run complete — no stuck orders.");
    }
}
