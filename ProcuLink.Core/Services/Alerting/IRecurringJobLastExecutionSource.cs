namespace ProcuLink.Core.Services.Alerting;

/// <summary>
/// Reads when a named recurring job last executed. Abstracted away from Hangfire so the alert probe
/// stays unit-testable, and so a host without a scheduler (or with none registered yet) degrades to
/// "unknown" rather than throwing.
/// <para>
/// The recurring dispatcher's own execution record is the migration-free evidence that a job is
/// still being run; the API health check reads it to prove the scheduler is firing.
/// </para>
/// </summary>
public interface IRecurringJobLastExecutionSource
{
    /// <summary>
    /// UTC time the named recurring job last ran, or <c>null</c> when unknown (job not registered,
    /// never run, or no scheduler storage available). Implementations MUST NOT throw.
    /// </summary>
    DateTime? GetLastExecutionUtc(string recurringJobId);
}
