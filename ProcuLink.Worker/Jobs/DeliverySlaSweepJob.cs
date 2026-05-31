using Hangfire;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (every 15 min): flags orders whose delivery confirmation window
/// (<c>DeliveryDueAt</c>) has elapsed without a confirmed delivery. Thin wrapper over
/// <see cref="IDeliverySlaService"/> so the sweep logic stays unit-tested in the Infrastructure
/// test suite. Idempotent — an already-flagged order no longer matches.
/// </summary>
public sealed class DeliverySlaSweepJob
{
    private readonly IDeliverySlaService _service;
    private readonly ILogger<DeliverySlaSweepJob> _logger;

    public DeliverySlaSweepJob(IDeliverySlaService service, ILogger<DeliverySlaSweepJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var flagged = await _service.RunAsync(ct);
        if (flagged > 0)
            _logger.LogWarning("DeliverySlaSweepJob flagged {Count} SLA-breached order(s).", flagged);
        else
            _logger.LogInformation("DeliverySlaSweepJob run complete — no SLA breaches.");
    }
}
