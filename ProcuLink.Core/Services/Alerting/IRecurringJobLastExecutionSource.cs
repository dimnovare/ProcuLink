namespace ProcuLink.Core.Services.Alerting;

/// <summary>
/// Reads when a named recurring job last executed. Abstracted away from Hangfire so the alert probe
/// stays unit-testable, and so a host without a scheduler (or with none registered yet) degrades to
/// "unknown" rather than throwing.
/// <para>
/// This exists because the SFTP and S3 pull channels persist no last-successful-poll timestamp of
/// their own — unlike IMAP, which stamps <c>EmailPollingConfig.LastPolledAt</c> after a successful
/// disconnect. Adding such a column is a schema change, and the migration slot is held elsewhere
/// this round, so the recurring dispatcher's own execution record is the migration-free stand-in.
/// It proves the channel is still being polled; it does NOT prove a given org's credentials work.
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
