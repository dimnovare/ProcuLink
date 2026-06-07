using Hangfire;
using ProcuLink.Core.Services;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (every ~5 min): thin wrapper over <see cref="IWorkerHealthAlertService"/>
/// so the probe + alert-decision logic stays unit-tested in the Infrastructure test suite
/// (mirrors <see cref="StuckOrderDetectionJob"/> / <see cref="DeliverySlaSweepJob"/>). Raises an
/// operator alert (Sentry in production, a safe no-op without a DSN) when no worker is beating or
/// the dead-letter/failed-delivery backlog crosses the configured threshold. Idempotent.
/// </summary>
public sealed class WorkerHealthAlertJob
{
    private readonly IWorkerHealthAlertService _service;
    private readonly ILogger<WorkerHealthAlertJob> _logger;

    public WorkerHealthAlertJob(
        IWorkerHealthAlertService service,
        ILogger<WorkerHealthAlertJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var alerted = await _service.RunAsync(ct);
        if (alerted)
            _logger.LogWarning("WorkerHealthAlertJob raised a worker-health alert.");
        else
            _logger.LogInformation("WorkerHealthAlertJob run complete — no alert raised.");
    }
}
