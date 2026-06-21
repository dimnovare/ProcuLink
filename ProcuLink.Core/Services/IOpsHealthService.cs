namespace ProcuLink.Core.Services;

/// <summary>
/// Org-scoped operator job-health surface. Aggregates the problematic order
/// states (stuck / failed / dead-letter / rejected) plus open-exception counts
/// that are otherwise scattered across the pipeline, and lists the dead-letter
/// queue so an operator can review and requeue. Read-mostly: the one mutating
/// action (requeue) lives on the controller next to the existing delivery jobs.
/// </summary>
public interface IOpsHealthService
{
    /// <summary>The age threshold above which a transient status counts as "stuck". Defaults to 30 min.</summary>
    TimeSpan StuckThreshold { get; }

    /// <summary>Aggregate job-health summary counts for the organisation.</summary>
    Task<OpsHealthSummary> GetHealthAsync(Guid organisationId, CancellationToken ct);

    /// <summary>
    /// List orders currently in <c>delivery_dead_letter</c> (and, when
    /// <paramref name="includeFailed"/> is true, <c>delivery_failed</c>), newest first,
    /// each with its latest delivery-attempt error and timestamps.
    /// </summary>
    Task<IReadOnlyList<DeadLetterOrder>> ListDeadLetterAsync(
        Guid organisationId, bool includeFailed, CancellationToken ct);

    /// <summary>
    /// CROSS-TENANT worker / dead-letter health for the alerting sweep. Combines the Hangfire
    /// server-heartbeat health (is any worker alive and recently beating?) with all-org counts
    /// of dead-lettered and failed-delivery orders. Used by the recurring health-alert job to
    /// decide whether to raise an operator alert. Not org-scoped on purpose — it is a system
    /// health probe, not a tenant view.
    /// </summary>
    Task<WorkerHealthSnapshot> GetWorkerHealthSnapshotAsync(CancellationToken ct);
}

/// <summary>
/// System-wide (cross-tenant) worker + dead-letter health snapshot for the alerting sweep.
/// </summary>
/// <param name="WorkerHealthy">True when at least one Hangfire server has a recent heartbeat.</param>
/// <param name="ActiveWorkers">Number of registered Hangfire servers.</param>
/// <param name="SecondsSinceWorkerHeartbeat">Seconds since the most recent heartbeat, or null if unknown.</param>
/// <param name="DeadLetterOrders">All-org count of orders in <c>delivery_dead_letter</c>.</param>
/// <param name="FailedDeliveryOrders">All-org count of orders in <c>delivery_failed</c>.</param>
public sealed record WorkerHealthSnapshot(
    bool      WorkerHealthy,
    int       ActiveWorkers,
    double?   SecondsSinceWorkerHeartbeat,
    int       DeadLetterOrders,
    int       FailedDeliveryOrders)
{
    /// <summary>Combined count of orders stuck in a failed-delivery / dead-letter state, all orgs.</summary>
    public int DeadLetterOrFailed => DeadLetterOrders + FailedDeliveryOrders;
}

/// <summary>Aggregate job-health counts for one organisation. All counts are org-scoped.</summary>
public sealed record OpsHealthSummary(
    int ParsingStuck,
    int DeliveringStuck,
    int TransformFailed,
    int DeliveryFailed,
    int DeliveryDeadLetter,
    int RejectedBySupplier,
    int Failed,
    int SlaBreached,
    int OpenExceptions,
    int StuckThresholdMinutes,
    int       ActiveWorkers             = 0,
    DateTime? LastWorkerHeartbeatUtc    = null,
    double?   SecondsSinceWorkerHeartbeat = null,
    bool      WorkerHealthy             = false,
    // INFORMATIONAL ONLY — orders awaiting a USER action (manual review), NOT a system fault.
    // Deliberately excluded from TotalProblemOrders so a normal review backlog is never
    // mislabelled as a fault; surfaced so an operator can still see a large backlog building up.
    int       PendingReview             = 0)
{
    /// <summary>
    /// Sum of all problematic order counts (excludes OpenExceptions, which can overlap order
    /// states, AND PendingReview, which is a user-action backlog, not a system fault).
    /// </summary>
    public int TotalProblemOrders =>
        ParsingStuck + DeliveringStuck + TransformFailed + DeliveryFailed +
        DeliveryDeadLetter + RejectedBySupplier + Failed;
}

/// <summary>One row on the dead-letter / failed-delivery operator queue.</summary>
public sealed record DeadLetterOrder(
    Guid     OrderId,
    string   PoNumber,
    Guid     SupplierId,
    string   SupplierName,
    string   Status,
    int      DeliveryAttempts,
    string?  LastError,
    int?     LastResponseCode,
    DateTime? LastAttemptAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);
