namespace ProcuLink.Core.Services;

/// <summary>
/// Sink for operator-facing alerts (a dead worker, a dead-letter spike, a delivery failure-rate
/// spike, a stalled pull channel, a latched AI token cap). Abstracted from the transport so the
/// alerting sweep stays unit-testable and the concrete sink can be a safe no-op when no alerting
/// backend is configured.
/// </summary>
public interface IWorkerAlertSink
{
    /// <summary>
    /// Raises an alert. Implementations MUST be safe to call when no backend is configured (empty
    /// Sentry DSN, no Postmark token, no recipient address) — in that case it is a silent no-op.
    /// Implementations MUST NOT throw: an alerting failure must never take down the Worker, and a
    /// throw here would abort the rest of the sweep and so suppress the other conditions.
    /// </summary>
    /// <param name="alertKey">
    /// Stable condition identifier from <c>OperationalAlertKeys</c>. Carried to the transport so an
    /// operator can filter and route on it.
    /// </param>
    /// <param name="message">Human-readable description of what is wrong, with the numbers in it.</param>
    /// <param name="ct">Cancellation for transports that perform I/O.</param>
    Task AlertAsync(string alertKey, string message, CancellationToken ct = default);
}
