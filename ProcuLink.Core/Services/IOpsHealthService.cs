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
    bool      WorkerHealthy             = false)
{
    /// <summary>Sum of all problematic order counts (excludes OpenExceptions, which can overlap order states).</summary>
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
