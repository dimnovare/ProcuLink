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
    /// <returns>
    /// <c>true</c> only when the alert was actually handed to a working transport. A sink that is
    /// unconfigured, that the provider refused, or that failed returns <c>false</c>.
    /// <para>
    /// This exists because "did not throw" is not "the operator was told". Every sink here is a
    /// deliberate no-op when unconfigured, so without this the sweep could report that it raised an
    /// alert while nothing left the process — the exact silence this whole component exists to
    /// prevent. Callers must treat <c>false</c> as "nobody has been notified".
    /// </para>
    /// </returns>
    Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct = default);
}
