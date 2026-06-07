using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class WorkerHealthAlertService : IWorkerHealthAlertService
{
    private readonly IOpsHealthService _health;
    private readonly IWorkerAlertSink _sink;
    private readonly WorkerHealthAlertState _state;
    private readonly WorkerHealthAlertOptions _options;
    private readonly ILogger<WorkerHealthAlertService> _logger;
    private readonly Func<DateTime> _utcNow;

    public WorkerHealthAlertService(
        IOpsHealthService health,
        IWorkerAlertSink sink,
        WorkerHealthAlertState state,
        WorkerHealthAlertOptions options,
        ILogger<WorkerHealthAlertService> logger,
        Func<DateTime>? utcNow = null)
    {
        _health = health;
        _sink = sink;
        _state = state;
        _options = options;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        var snap = await _health.GetWorkerHealthSnapshotAsync(ct);

        var workerDown = !snap.WorkerHealthy;
        var deadLetterSpike = snap.DeadLetterOrFailed >= _options.EffectiveDeadLetterThreshold;
        var isBad = workerDown || deadLetterSpike;

        var shouldAlert = _state.ShouldAlert(isBad, _utcNow(), _options.MinAlertInterval);

        if (!isBad)
        {
            _logger.LogInformation(
                "WorkerHealthAlert: healthy — workers={ActiveWorkers}, deadLetter+failed={Count}.",
                snap.ActiveWorkers, snap.DeadLetterOrFailed);
            return false;
        }

        var message = BuildMessage(snap, workerDown, deadLetterSpike, _options.EffectiveDeadLetterThreshold);

        if (shouldAlert)
        {
            _sink.Alert(message);
            _logger.LogError("WorkerHealthAlert: {Message}", message);
            return true;
        }

        // Still bad but inside the rate-limit window — log without re-alerting.
        _logger.LogWarning("WorkerHealthAlert (suppressed, rate-limited): {Message}", message);
        return false;
    }

    private static string BuildMessage(
        WorkerHealthSnapshot snap, bool workerDown, bool deadLetterSpike, int threshold)
    {
        var reasons = new List<string>(2);
        if (workerDown)
        {
            var since = snap.SecondsSinceWorkerHeartbeat is { } s
                ? $"{s:F0}s since last heartbeat"
                : "no heartbeat seen";
            reasons.Add($"no healthy worker ({snap.ActiveWorkers} registered, {since})");
        }
        if (deadLetterSpike)
            reasons.Add($"dead-letter+failed deliveries = {snap.DeadLetterOrFailed} (threshold {threshold})");

        return $"ProcuLink Worker health degraded: {string.Join("; ", reasons)}. "
             + $"[workers={snap.ActiveWorkers}, deadLetter={snap.DeadLetterOrders}, failedDelivery={snap.FailedDeliveryOrders}]";
    }
}
