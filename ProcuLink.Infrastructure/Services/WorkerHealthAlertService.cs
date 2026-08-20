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

    /// <summary>
    /// Runs one sweep.
    /// <para>
    /// <b>Fail closed, never all-clear.</b> Both inputs are read defensively and INDEPENDENTLY. A
    /// source that cannot be read yields no conditions at all rather than zeroed ones: the
    /// conditions it feeds are skipped as UNKNOWN, and the fact that the sweep was partially blind
    /// is itself raised as <see cref="OperationalAlertKeys.AlertSweepDegraded"/> through the same
    /// sink and the same per-condition cooldown as any other alert. The all-clear log is suppressed
    /// in that run, because a sweep that could not evaluate something has no business reporting that
    /// everything is fine.
    /// </para>
    /// <para>
    /// The failure of one input costs only the conditions it feeds — a broken probe must not take
    /// the heartbeat and backlog conditions with it, and a timed-out snapshot query must not take
    /// the probe's three with it.
    /// </para>
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        var now = _utcNow();

        var snapshot = await ReadSnapshotAsync(ct);
        var signals = await ReadSignalsAsync(ct);

        var conditions = new List<(string Key, bool IsBad, string Message)>(OperationalAlertKeys.All.Count);
        var blindSources = new List<string>();

        if (snapshot is { } snap)
        {
            conditions.Add(EvaluateWorkerHeartbeat(snap));
            conditions.Add(EvaluateDeadLetterBacklog(snap));
            conditions.Add(EvaluatePipelineFailureBacklog(snap));
        }
        else
        {
            blindSources.Add(
                "the worker health snapshot query (worker heartbeat, dead-letter backlog, "
              + "pipeline failure backlog)");
        }

        if (signals is { } sig)
        {
            conditions.Add(EvaluateDeliveryFailureRate(sig.DeliveryFailureRate));
            conditions.Add(EvaluatePullChannels(sig.PullChannels));
            conditions.Add(EvaluateAiTokenLatch(sig.AiTokenLatch));
        }
        else
        {
            blindSources.Add(
                "the operational probe (delivery failure rate, pull-channel freshness, AI token cap)");
        }

        conditions.Add(EvaluateSweepDegraded(blindSources));

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

            var delivered = await TryAlertAsync(key, message, ct);
            alertedAny |= delivered;

            if (!delivered)
            {
                // Every sink is a deliberate no-op when unconfigured, so an alert can be "raised"
                // and reach nobody. This is the floor of what can be done about it from inside the
                // process; StartupConfigurationValidator refuses to boot Production with no alert
                // destination at all, which is the only place the gap can be closed rather than
                // reported.
                _logger.LogError(
                    "WorkerHealthAlert [{AlertKey}]: the alert reached no configured transport — "
                  + "NOBODY has been notified. Set Alerting:Email:To (with Email:Postmark:ServerToken) "
                  + "or Sentry:Dsn on the Worker.", key);
            }
        }

        if (!anyBad && snapshot is { } okSnap && signals is { } okSignals)
        {
            _logger.LogInformation(
                "WorkerHealthAlert: healthy — workers={ActiveWorkers}, deadLetter+failed={DeadLetter}, "
              + "pipelineFailed={PipelineFailed}, "
              + "deliveryAttempts={Attempts}/failures={Failures}, latchedAiOrgs={Latched}.",
                okSnap.ActiveWorkers, okSnap.DeadLetterOrFailed, okSnap.PipelineFailedOrders,
                okSignals.DeliveryFailureRate.Attempts, okSignals.DeliveryFailureRate.Failures,
                okSignals.AiTokenLatch.LatchedOrgs);
        }

        return alertedAny;
    }

    /// <summary>
    /// Reads the worker-health snapshot, returning <c>null</c> when it cannot be read.
    /// <para>
    /// This was previously unguarded and ran FIRST, so with <c>[AutomaticRetry(Attempts = 0)]</c> on
    /// the job a single Postgres timeout killed all five conditions for that cycle in silence — the
    /// two oldest conditions could take down the three newest, guarded ones.
    /// </para>
    /// </summary>
    private async Task<WorkerHealthSnapshot?> ReadSnapshotAsync(CancellationToken ct)
    {
        try
        {
            return await _health.GetWorkerHealthSnapshotAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown / job cancellation is not an incident. Swallowing it here would page
            // the operator on every deploy, which is exactly the false alarm these thresholds
            // were all designed to avoid.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WorkerHealthAlert: worker health snapshot query failed — worker heartbeat and "
              + "dead-letter backlog are NOT evaluated this run.");
            return null;
        }
    }

    /// <summary>
    /// Reads the extra signals, returning <c>null</c> when the probe cannot be read.
    /// <para>
    /// Deliberately NOT an all-clear fallback. A zeroed snapshot — no attempts, no channels, no
    /// latched orgs — evaluates as "not bad" on all three conditions it feeds, so a permanently
    /// broken probe query used to make three of the five conditions report healthy forever while
    /// the same run logged "healthy". Unknown is not healthy.
    /// </para>
    /// </summary>
    private async Task<OperationalAlertSignals?> ReadSignalsAsync(CancellationToken ct)
    {
        try
        {
            return await _probe.GetSignalsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WorkerHealthAlert: operational alert probe failed — delivery failure rate, "
              + "pull-channel freshness and AI token-cap latch are NOT evaluated this run.");
            return null;
        }
    }

    /// <summary>
    /// Hands one alert to the sink and reports whether any transport actually delivered it. Sinks
    /// are contractually non-throwing, but this is the last line of defence: if one throws anyway,
    /// the remaining conditions in this sweep must still be offered, or a single bad transport
    /// silently suppresses every other alert in the same run.
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
            return await _sink.AlertAsync(key, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    private (string, bool, string) EvaluatePipelineFailureBacklog(WorkerHealthSnapshot snap)
    {
        var threshold = _options.EffectivePipelineFailureThreshold;
        var isBad = snap.PipelineFailedOrders >= threshold;

        return (OperationalAlertKeys.PipelineFailureBacklog, isBad,
            $"ProcuLink pipeline failure backlog: failed+transform_failed orders = {snap.PipelineFailedOrders} "
          + $"(threshold {threshold}) [failed={snap.FailedOrders}, transformFailed={snap.TransformFailedOrders}]. "
          + "These orders failed BEFORE the delivery step, so no delivery alert covers them — "
          + "check parser and output-template errors in the Worker logs.");
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
            .Where(c => c.EnabledSources > 0
                     && c.MinutesSinceLastSuccess is { } age
                     && age >= staleMinutes)
            .OrderByDescending(c => c.MinutesSinceLastSuccess)
            .ToList();

        var detail = stale.Count == 0
            ? "none"
            : string.Join(", ", stale.Select(c =>
                $"{c.Channel} (last success {c.MinutesSinceLastSuccess!.Value:F0} min ago, "
              + $"{c.EnabledSources} enabled source(s))"));

        return (OperationalAlertKeys.PullChannelStalled, stale.Count > 0,
            $"ProcuLink inbound pull channel stalled: {detail}. Threshold {staleMinutes} min. "
          + "Purchase orders sent to that channel are not being picked up.");
    }

    private static (string, bool, string) EvaluateAiTokenLatch(AiTokenLatchSignal signal) =>
        (OperationalAlertKeys.AiTokenCapLatched, signal.LatchedOrgs > 0,
            $"ProcuLink AI token cap latched for {signal.LatchedOrgs} organisation(s) in good standing. "
          + "PDF extraction for them has silently degraded to the regex fallback until the month rolls "
          + "over or the limit is raised.");

    /// <summary>
    /// The meta-condition: the sweep could not read one or more of its own inputs, so the conditions
    /// those inputs feed are UNKNOWN this run.
    /// <para>
    /// It runs through the same sink and the same per-condition cooldown as everything else, so a
    /// permanently broken input pages once per window rather than every five minutes. When nothing
    /// is blind it reports healthy, which re-arms the transition alert for the next outage.
    /// </para>
    /// <para>
    /// ONE condition covers every input rather than one per input: the operator's action is the
    /// same whichever failed, and the message names each blind source, so a single database outage
    /// is one page carrying both names instead of two pages saying the same thing.
    /// </para>
    /// </summary>
    private static (string, bool, string) EvaluateSweepDegraded(IReadOnlyList<string> blindSources) =>
        (OperationalAlertKeys.AlertSweepDegraded, blindSources.Count > 0,
            $"ProcuLink alert sweep is partially blind: {string.Join(" and ", blindSources)} could not "
          + "be read this run, so the conditions they feed were NOT evaluated — unknown, not healthy. "
          + "Monitoring is degraded until this clears; check Worker logs and database connectivity.");
}
