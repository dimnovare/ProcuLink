using Hangfire.Storage;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class OpsHealthService : IOpsHealthService
{
    /// <summary>
    /// A heartbeat older than this is treated as a dead/missing worker.
    /// Hangfire's default HeartbeatInterval is 30 s; we allow 2x leeway.
    /// </summary>
    private static readonly TimeSpan WorkerHeartbeatDeadline = TimeSpan.FromSeconds(60);

    private readonly IMonitoringApi? _monitoring;

    public OpsHealthService(IMonitoringApi? monitoring = null)
    {
        _monitoring = monitoring;
    }

    public Task<WorkerHealthSnapshot> GetWorkerHealthSnapshotAsync(CancellationToken ct)
    {
        var (activeWorkers, secondsSince, workerHealthy) = GetWorkerHealth();

        return Task.FromResult(new WorkerHealthSnapshot(
            WorkerHealthy:               workerHealthy,
            ActiveWorkers:               activeWorkers,
            SecondsSinceWorkerHeartbeat: secondsSince));
    }

    /// <summary>
    /// Queries the Hangfire monitoring API for registered server heartbeats.
    /// Returns safe defaults when the monitoring API is unavailable or throws.
    /// </summary>
    private (int activeWorkers, double? secondsSince, bool healthy) GetWorkerHealth()
    {
        try
        {
            var api = _monitoring ?? Hangfire.JobStorage.Current?.GetMonitoringApi();
            if (api is null)
                return (0, null, false);

            var servers = api.Servers();
            if (servers is null || servers.Count == 0)
                return (0, null, false);

            var now           = DateTime.UtcNow;
            var lastHeartbeat = servers
                .Select(s => s.Heartbeat)
                .Where(h => h.HasValue)
                .Select(h => h!.Value)
                .DefaultIfEmpty()
                .Max();

            if (lastHeartbeat == default)
                return (servers.Count, null, false);

            var age     = (now - lastHeartbeat).TotalSeconds;
            var healthy = age <= WorkerHeartbeatDeadline.TotalSeconds;
            return (servers.Count, age, healthy);
        }
        catch
        {
            // Monitoring API unavailable (e.g. storage not yet ready, test environment).
            return (0, null, false);
        }
    }
}
