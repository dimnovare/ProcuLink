using System.Globalization;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class WorkerHealthAlertService : IWorkerHealthAlertService
{
    private readonly IOpsHealthService _health;
    private readonly IOperationalAlertProbe _probe;
    private readonly IWorkerAlertSink _sink;
    private readonly WorkerHealthAlertState _state;
    private readonly WorkerHealthAlertOptions _options;
    private readonly ILogger<WorkerHealthAlertService> _logger;
    private readonly Func<DateTime> _utcNow;

    public WorkerHealthAlertService(
        IOpsHealthService health,
        IOperationalAlertProbe probe,
        IWorkerAlertSink sink,
        WorkerHealthAlertState state,
        WorkerHealthAlertOptions options,
        ILogger<WorkerHealthAlertService> logger,
        Func<DateTime>? utcNow = null)
    {
        _health = health;
        _probe = probe;
        _sink = sink;
        _state = state;
        _options = options;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        var now = _utcNow();
        var snap = await _health.GetWorkerHealthSnapshotAsync(ct);
        var signals = await ReadSignalsAsync(ct);

        var conditions = new List<(string Key, bool IsBad, string Message)>(OperationalAlertKeys.All.Count)
        {
            EvaluateWorkerHeartbeat(snap),
            EvaluateDeadLetterBacklog(snap),
            EvaluateDeliveryFailureRate(signals.DeliveryFailureRate),
            EvaluatePullChannels(signals.PullChannels),
            EvaluateAiTokenLatch(signals.AiTokenLatch),
        };

        var anyBad = false;
        var alertedAny = false;

        foreach (var (key, isBad, message) in conditions)
        {
            var shouldAlert = _state.ShouldAlert(key, isBad, now, _options.MinAlertInterval);
            if (!isBad)
                continue;

            anyBad = true;

            if (!shouldAlert)
            {
                // Still bad but inside this condition's rate-limit window — log without re-alerting.
                _logger.LogWarning("WorkerHealthAlert (suppressed, rate-limited) [{AlertKey}]: {Message}",
                    key, message);
                continue;
            }

            _logger.LogError("WorkerHealthAlert [{AlertKey}]: {Message}", key, message);
            alertedAny |= await TryAlertAsync(key, message, ct);
        }

        if (!anyBad)
        {
            _logger.LogInformation(
                "WorkerHealthAlert: healthy — workers={ActiveWorkers}, deadLetter+failed={DeadLetter}, "
              + "deliveryAttempts={Attempts}/failures={Failures}, latchedAiOrgs={Latched}.",
                snap.ActiveWorkers, snap.DeadLetterOrFailed,
                signals.DeliveryFailureRate.Attempts, signals.DeliveryFailureRate.Failures,
                signals.AiTokenLatch.LatchedOrgs);
        }

        return alertedAny;
    }

    /// <summary>
    /// Reads the extra signals, degrading to "all clear" if the probe fails. A broken probe must
    /// cost the three conditions it feeds, never the worker-heartbeat and backlog conditions that
    /// do not depend on it — and never the sweep itself.
    /// </summary>
    private async Task<OperationalAlertSignals> ReadSignalsAsync(CancellationToken ct)
    {
        try
        {
            return await _probe.GetSignalsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WorkerHealthAlert: operational alert probe failed — delivery failure rate, "
              + "pull-channel freshness and AI token-cap latch are NOT evaluated this run.");
            return OperationalAlertSignals.Empty;
        }
    }

    /// <summary>
    /// Hands one alert to the sink. Sinks are contractually non-throwing, but this is the last line
    /// of defence: if one throws anyway, the remaining conditions in this sweep must still be
    /// offered, or a single bad transport silently suppresses every other alert in the same run.
    /// <para>
    /// The cooldown was already stamped before this call and is NOT rolled back on failure, so a
    /// throwing transport costs one condition up to one cooldown window rather than turning the
    /// 5-minute sweep into a retry storm against something already broken. The composite sink
    /// swallows per-transport failures, so reaching this catch means every transport failed.
    /// </para>
    /// </summary>
    private async Task<bool> TryAlertAsync(string key, string message, CancellationToken ct)
    {
        try
        {
            await _sink.AlertAsync(key, message, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WorkerHealthAlert: sink threw while raising {AlertKey}.", key);
            return false;
        }
    }

    // ── Conditions ───────────────────────────────────────────────────────────────

    private static (string, bool, string) EvaluateWorkerHeartbeat(WorkerHealthSnapshot snap)
    {
        var isBad = !snap.WorkerHealthy;
        var since = snap.SecondsSinceWorkerHeartbeat is { } s
            ? $"{s:F0}s since last heartbeat"
            : "no heartbeat seen";

        return (OperationalAlertKeys.WorkerHeartbeatLost, isBad,
            $"ProcuLink Worker health degraded: no healthy worker ({snap.ActiveWorkers} registered, {since}). "
          + "Background parse/transform/deliver jobs are not running; uploads will land and sit.");
    }

    private (string, bool, string) EvaluateDeadLetterBacklog(WorkerHealthSnapshot snap)
    {
        var threshold = _options.EffectiveDeadLetterThreshold;
        var isBad = snap.DeadLetterOrFailed >= threshold;

        return (OperationalAlertKeys.DeadLetterBacklog, isBad,
            $"ProcuLink delivery backlog: dead-letter+failed deliveries = {snap.DeadLetterOrFailed} "
          + $"(threshold {threshold}) [deadLetter={snap.DeadLetterOrders}, failedDelivery={snap.FailedDeliveryOrders}]. "
          + "Review and requeue from the operations health page.");
    }

    private (string, bool, string) EvaluateDeliveryFailureRate(DeliveryFailureRateSignal signal)
    {
        var minAttempts = _options.EffectiveDeliveryFailureMinAttempts;
        var tripPercent = _options.EffectiveDeliveryFailurePercent;

        // Integer comparison rather than a floating-point one, so a threshold of exactly 50% on
        // 5-of-10 trips deterministically instead of depending on binary rounding. Widened to
        // long because the ×100 would overflow int on a large enough attempt count.
        var isBad = signal.Attempts >= minAttempts
                 && (long)signal.Failures * 100 >= (long)tripPercent * signal.Attempts;

        var pct = signal.FailurePercent.ToString("F0", CultureInfo.InvariantCulture);

        return (OperationalAlertKeys.DeliveryFailureRate, isBad,
            $"ProcuLink delivery failure rate {pct}% over the last {signal.WindowMinutes} min "
          + $"({signal.Failures}/{signal.Attempts} concluded attempts failed; trips at {tripPercent}% "
          + $"with at least {minAttempts} attempts). Suppliers may be rejecting or unreachable.");
    }

    private (string, bool, string) EvaluatePullChannels(IReadOnlyList<PullChannelSignal> channels)
    {
        var staleMinutes = _options.EffectivePullChannelStaleMinutes;

        // A channel nobody has switched on cannot be a live incident, and a channel that has never
        // recorded a success has simply not run yet — paging on either would be a false alarm on
        // every fresh setup.
        var stale = channels
            .Where(c => c.EnabledOrgs > 0
                     && c.MinutesSinceLastSuccess is { } age
                     && age >= staleMinutes)
            .OrderByDescending(c => c.MinutesSinceLastSuccess)
            .ToList();

        var detail = stale.Count == 0
            ? "none"
            : string.Join(", ", stale.Select(c =>
                $"{c.Channel} (last success {c.MinutesSinceLastSuccess!.Value:F0} min ago, "
              + $"{c.EnabledOrgs} org(s) enabled)"));

        return (OperationalAlertKeys.PullChannelStalled, stale.Count > 0,
            $"ProcuLink inbound pull channel stalled: {detail}. Threshold {staleMinutes} min. "
          + "Purchase orders sent to that channel are not being picked up.");
    }

    private static (string, bool, string) EvaluateAiTokenLatch(AiTokenLatchSignal signal) =>
        (OperationalAlertKeys.AiTokenCapLatched, signal.LatchedOrgs > 0,
            $"ProcuLink AI token cap latched for {signal.LatchedOrgs} organisation(s) in good standing. "
          + "PDF extraction for them has silently degraded to the regex fallback until the month rolls "
          + "over or the limit is raised.");
}
