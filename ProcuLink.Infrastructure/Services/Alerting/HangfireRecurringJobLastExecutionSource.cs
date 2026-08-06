using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Alerting;

namespace ProcuLink.Infrastructure.Services.Alerting;

/// <summary>
/// Reads a recurring job's last execution out of Hangfire's own storage. No new table: the
/// scheduler already records this, exactly as the worker heartbeat reuses Hangfire's server
/// heartbeat instead of adding a heartbeat table.
/// <para>
/// Every failure path returns <c>null</c> ("unknown"), never a throw and never a fake timestamp —
/// an unreadable scheduler must not page, and must not be mistaken for a fresh poll either.
/// </para>
/// </summary>
public sealed class HangfireRecurringJobLastExecutionSource : IRecurringJobLastExecutionSource
{
    private readonly JobStorage? _storage;
    private readonly ILogger<HangfireRecurringJobLastExecutionSource> _logger;

    /// <summary>
    /// <paramref name="storage"/> is OPTIONAL and falls back to <see cref="JobStorage.Current"/>,
    /// mirroring <c>OpsHealthService</c>'s handling of <c>IMonitoringApi</c>. Whether
    /// <c>AddHangfire</c> puts <see cref="JobStorage"/> in the container is a Hangfire packaging
    /// detail; the static is set by <c>AddHangfire</c> regardless, so depending on the container
    /// entry alone would make this component fail to construct — and take the whole alert sweep
    /// with it — on a Hangfire upgrade.
    /// </summary>
    public HangfireRecurringJobLastExecutionSource(
        ILogger<HangfireRecurringJobLastExecutionSource> logger,
        JobStorage? storage = null)
    {
        _storage = storage;
        _logger = logger;
    }

    public DateTime? GetLastExecutionUtc(string recurringJobId)
    {
        try
        {
            var storage = _storage ?? JobStorage.Current;
            if (storage is null)
                return null;

            using var connection = storage.GetConnection();
            var job = connection.GetRecurringJobs([recurringJobId]).FirstOrDefault();
            return job?.LastExecution;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read the last execution of recurring job {JobId} from Hangfire storage.",
                recurringJobId);
            return null;
        }
    }
}

/// <summary>
/// Null-object <see cref="IRecurringJobLastExecutionSource"/> for hosts with no scheduler storage
/// (the API, tests). Always "unknown", which the alert rules treat as not-alertable.
/// </summary>
public sealed class NullRecurringJobLastExecutionSource : IRecurringJobLastExecutionSource
{
    public DateTime? GetLastExecutionUtc(string recurringJobId) => null;
}
