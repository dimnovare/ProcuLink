namespace ProcuLink.Core.Services;

/// <summary>
/// Cross-tenant worker-liveness probe for the alerting sweep. Reads the Hangfire server heartbeats
/// and answers "is any worker alive and recently beating?". Order-pipeline job health (stuck /
/// failed / dead-letter counts, the operator dead-letter queue) went with the order pipeline.
/// </summary>
public interface IOpsHealthService
{
    /// <summary>
    /// CROSS-TENANT worker health for the alerting sweep. Used by the recurring health-alert job to
    /// decide whether to raise an operator alert. Not org-scoped on purpose: it is a system health
    /// probe, not a tenant view, and it reads no tenant data.
    /// </summary>
    Task<WorkerHealthSnapshot> GetWorkerHealthSnapshotAsync(CancellationToken ct);
}

/// <summary>
/// System-wide (cross-tenant) worker health snapshot for the alerting sweep.
/// </summary>
/// <param name="WorkerHealthy">True when at least one Hangfire server has a recent heartbeat.</param>
/// <param name="ActiveWorkers">Number of registered Hangfire servers.</param>
/// <param name="SecondsSinceWorkerHeartbeat">Seconds since the most recent heartbeat, or null if unknown.</param>
public sealed record WorkerHealthSnapshot(
    bool    WorkerHealthy,
    int     ActiveWorkers,
    double? SecondsSinceWorkerHeartbeat);
