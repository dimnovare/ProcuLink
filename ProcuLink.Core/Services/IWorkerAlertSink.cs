namespace ProcuLink.Core.Services;

/// <summary>
/// Sink for operator-facing worker alerts (e.g. a dead worker or a dead-letter spike).
/// Abstracted from the transport (Sentry) so the alerting job stays unit-testable and the
/// concrete sink can be a safe no-op when no alerting backend is configured.
/// </summary>
public interface IWorkerAlertSink
{
    /// <summary>
    /// Raises an alert with the given message. Implementations MUST be safe to call when no
    /// backend is configured (e.g. empty Sentry DSN) — in that case it is a silent no-op.
    /// </summary>
    void Alert(string message);
}
