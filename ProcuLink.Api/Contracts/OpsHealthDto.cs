namespace ProcuLink.Api.Contracts;

/// <summary>
/// Operator job-health summary for a single organisation. Aggregates the
/// problematic order states that otherwise sit silently in the pipeline so an
/// operator can see at a glance whether anything needs attention.
/// </summary>
/// <param name="ParsingStuck">Orders stuck in <c>parsing</c> older than the stuck threshold.</param>
/// <param name="DeliveringStuck">Orders stuck in <c>delivering</c> older than the stuck threshold.</param>
/// <param name="TransformFailed">Orders in <c>transform_failed</c>.</param>
/// <param name="DeliveryFailed">Orders in <c>delivery_failed</c> (retryable).</param>
/// <param name="DeliveryDeadLetter">Orders in <c>delivery_dead_letter</c> (retries exhausted).</param>
/// <param name="RejectedBySupplier">Orders in <c>rejected_by_supplier</c>.</param>
/// <param name="Failed">Orders in the generic <c>failed</c> state (e.g. stuck-timeout / parse failure).</param>
/// <param name="SlaBreached">Orders whose delivery SLA window has elapsed without confirmation.</param>
/// <param name="OpenExceptions">Total open <c>OrderException</c> rows for the org.</param>
/// <param name="StuckThresholdMinutes">The age threshold (minutes) used for the *Stuck counts.</param>
/// <param name="TotalProblemOrders">Sum of all problematic order counts above (excludes OpenExceptions).</param>
/// <param name="ActiveWorkers">Number of Hangfire server processes currently registered.</param>
/// <param name="LastWorkerHeartbeatUtc">Most-recent heartbeat timestamp across all Hangfire servers. Null if no servers registered.</param>
/// <param name="SecondsSinceWorkerHeartbeat">Seconds elapsed since the most-recent heartbeat. Null if no servers registered.</param>
/// <param name="WorkerHealthy">True iff at least one Hangfire server sent a heartbeat within the last 60 seconds.</param>
/// <param name="PendingReview">
/// INFORMATIONAL count of orders in <c>pending_review</c> — a USER-action backlog (orders waiting
/// for a human to resolve mappings/exceptions), NOT a system fault. Deliberately excluded from
/// <paramref name="TotalProblemOrders"/>; surfaced so an operator can see a large review backlog.
/// </param>
public record OpsHealthDto(
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
    int TotalProblemOrders,
    int        ActiveWorkers,
    DateTime?  LastWorkerHeartbeatUtc,
    double?    SecondsSinceWorkerHeartbeat,
    bool       WorkerHealthy,
    int        PendingReview
);

/// <summary>
/// A single order surfaced on the dead-letter / failed-delivery operator queue,
/// with the latest delivery-attempt error and timestamps so an operator can
/// review and decide whether to requeue.
/// </summary>
public record DeadLetterOrderDto(
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
    DateTime UpdatedAt
);
