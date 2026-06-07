namespace ProcuLink.Core.Services;

/// <summary>
/// Probes cross-tenant worker / dead-letter health (via <see cref="IOpsHealthService"/>) and
/// raises an operator alert (via <see cref="IWorkerAlertSink"/>) when health is degraded:
/// no worker has a recent heartbeat, OR the all-org dead-letter + failed-delivery backlog
/// crosses the configured threshold.
/// <para>
/// Anti-spam: alerts immediately on a healthy→bad transition, then at most once per the
/// configured interval while it stays bad; recovering re-arms the transition alert. Runs as a
/// recurring sweep. Idempotent — invoking it never changes durable system state.
/// </para>
/// </summary>
public interface IWorkerHealthAlertService
{
    /// <summary>
    /// Runs one health probe and emits an alert if warranted. Returns <c>true</c> when an alert
    /// was actually raised this run, <c>false</c> when healthy or when an in-window repeat was
    /// suppressed by the rate limit.
    /// </summary>
    Task<bool> RunAsync(CancellationToken ct);
}
