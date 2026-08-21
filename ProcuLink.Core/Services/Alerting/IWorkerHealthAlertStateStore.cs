namespace ProcuLink.Core.Services.Alerting;

/// <summary>
/// The durable anti-spam state of ONE alert condition, as it crosses the store boundary.
/// </summary>
/// <param name="AlertKey">Condition identifier from <see cref="OperationalAlertKeys"/>.</param>
/// <param name="WasBad">Whether the condition was degraded at the end of the last sweep.</param>
/// <param name="LastAlertUtc">When it last alerted, or <c>null</c> if it never has.</param>
public sealed record WorkerHealthAlertConditionState(
    string AlertKey,
    bool WasBad,
    DateTime? LastAlertUtc);

/// <summary>
/// Durable backing for the alert sweep's per-condition cooldown.
///
/// <para><b>The contract is that it may fail, and must say so rather than pretend.</b> Both methods
/// are allowed to throw; the caller treats a throw as UNKNOWN state and raises
/// <c>OperationalAlertKeys.AlertSweepDegraded</c>, exactly as it does for the health snapshot and
/// the operational probe. What an implementation must never do is answer an unreadable store with
/// an empty list, because "no rows" and "cannot read" differ by the whole defect: the first means
/// every condition is freshly armed, the second means nothing is known.</para>
/// </summary>
public interface IWorkerHealthAlertStateStore
{
    /// <summary>Reads every stored condition. An empty result means genuinely no rows yet.</summary>
    Task<IReadOnlyList<WorkerHealthAlertConditionState>> LoadAsync(CancellationToken ct);

    /// <summary>Upserts the supplied conditions. Only conditions whose state changed are passed.</summary>
    Task SaveAsync(IReadOnlyCollection<WorkerHealthAlertConditionState> states, CancellationToken ct);
}
