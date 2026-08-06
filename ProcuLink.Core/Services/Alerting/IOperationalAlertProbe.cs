namespace ProcuLink.Core.Services.Alerting;

/// <summary>
/// CROSS-TENANT probe for the three alert conditions that <see cref="IOpsHealthService"/> does not
/// already cover (delivery failure rate, pull-channel staleness, AI token-cap latch). Deliberately
/// NOT org-scoped: this is a system health probe for the operator, exactly like
/// <c>IOpsHealthService.GetWorkerHealthSnapshotAsync</c>, not a tenant-facing view. Nothing here is
/// ever returned through an org-scoped API surface.
/// <para>
/// Kept separate from <see cref="IOpsHealthService"/> so the org-scoped operator surface (consumed
/// by <c>OpsController</c>) does not grow more cross-tenant aggregates; the two feed the SAME
/// alert service, job and sink — there is one alerting path, not two.
/// </para>
/// </summary>
public interface IOperationalAlertProbe
{
    /// <summary>Reads every extra alert signal in one pass.</summary>
    Task<OperationalAlertSignals> GetSignalsAsync(CancellationToken ct);
}

/// <summary>The three extra signals, read together so one sweep is one round of queries.</summary>
public sealed record OperationalAlertSignals(
    DeliveryFailureRateSignal DeliveryFailureRate,
    IReadOnlyList<PullChannelSignal> PullChannels,
    AiTokenLatchSignal AiTokenLatch)
{
    /// <summary>All-clear snapshot — used as the fallback when the probe itself fails.</summary>
    public static OperationalAlertSignals Empty { get; } = new(
        new DeliveryFailureRateSignal(0, 0, 0),
        Array.Empty<PullChannelSignal>(),
        new AiTokenLatchSignal(0));
}

/// <summary>
/// Delivery attempts in the trailing window, all orgs. Only CONCLUDED attempts count:
/// <c>dispatching</c> (in flight) and <c>unconfirmed</c> (outcome unknown, parked for a human)
/// are excluded, so an in-progress send can never be scored as a failure.
/// </summary>
/// <param name="WindowMinutes">Length of the trailing window the counts were taken over.</param>
/// <param name="Attempts">Concluded attempts in the window (successes + failures).</param>
/// <param name="Failures">Of those, the ones that failed.</param>
public sealed record DeliveryFailureRateSignal(int WindowMinutes, int Attempts, int Failures)
{
    /// <summary>Failure share of concluded attempts, 0–100. Zero when nothing concluded.</summary>
    public double FailurePercent => Attempts <= 0 ? 0d : Failures * 100d / Attempts;
}

/// <summary>
/// Freshness of one inbound pull channel.
/// </summary>
/// <param name="Channel">Channel name as an operator knows it: <c>email</c>, <c>sftp</c>, <c>s3</c>.</param>
/// <param name="EnabledOrgs">
/// How many orgs currently have this channel switched on. Zero means nobody is using the channel,
/// and a stale timestamp on an unused channel is not an incident.
/// </param>
/// <param name="MinutesSinceLastSuccess">
/// Age of the most recent observed success, or <c>null</c> when no success has ever been observed.
/// Null is deliberately NOT alertable — a channel configured minutes ago has not polled yet, and
/// paging on that would be a false alarm on every new setup.
/// </param>
public sealed record PullChannelSignal(string Channel, int EnabledOrgs, double? MinutesSinceLastSuccess);

/// <summary>
/// Orgs whose monthly AI token budget is exhausted. A latched cap silently degrades PDF extraction
/// to the regex fallback for that org, which is a known incident pattern that surfaces nowhere else.
/// </summary>
/// <param name="LatchedOrgs">
/// Count of GOOD-STANDING orgs at or over their resolved monthly limit. Delinquent (read-only /
/// cancelled / past-due) orgs are excluded on purpose: their AI budget is clamped deliberately by
/// the billing rules, so reporting them would be a permanent, unactionable page.
/// </param>
public sealed record AiTokenLatchSignal(int LatchedOrgs);
