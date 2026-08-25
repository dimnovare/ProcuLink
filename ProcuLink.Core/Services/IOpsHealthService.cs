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
/// <param name="DeadLetterOrders">
/// All-org count of orders in <c>delivery_dead_letter</c>. ALL-TIME on purpose — the status has an
/// operator drain (<c>OrderStatusMachine.RequeueableFrom</c>), so the count falls when the incident
/// is handled rather than only when it ages out.
/// </param>
/// <param name="FailedDeliveryOrders">All-org count of orders in <c>delivery_failed</c>. All-time, same reason.</param>
/// <param name="FailedOrders">
/// All-org count of orders in <c>failed</c> (pipeline/parse failure) whose <c>UpdatedAt</c> falls
/// inside <paramref name="PipelineFailureWindowMinutes"/>. RECENT, not all-time — <c>failed</c> is
/// terminal with an empty transition set, so an all-time count can never fall.
/// </param>
/// <param name="TransformFailedOrders">
/// All-org count of orders in <c>transform_failed</c> inside the same trailing window.
/// </param>
/// <param name="PipelineFailureWindowMinutes">
/// The trailing window the two pipeline-failure counts were taken over, so an alert can state what
/// it actually measured. <c>0</c> means the counts are all-time (a snapshot produced without a
/// window — the shape that had no drain).
/// </param>
public sealed record WorkerHealthSnapshot(
    bool      WorkerHealthy,
    int       ActiveWorkers,
    double?   SecondsSinceWorkerHeartbeat,
    int       DeadLetterOrders,
    int       FailedDeliveryOrders,
    int       FailedOrders          = 0,
    int       TransformFailedOrders = 0,
    int       PipelineFailureWindowMinutes = 0)
{
    /// <summary>Combined count of orders stuck in a failed-delivery / dead-letter state, all orgs.</summary>
    public int DeadLetterOrFailed => DeadLetterOrders + FailedDeliveryOrders;

    /// <summary>
    /// Combined count of orders that failed BEFORE the delivery step — parse (<c>failed</c>) and
    /// transform (<c>transform_failed</c>) — all orgs, within
    /// <see cref="PipelineFailureWindowMinutes"/>. These never reach the dead-letter bucket, so
    /// without this count a broken parser surfaces as a customer email, not an alert.
    /// </summary>
    public int PipelineFailedOrders => FailedOrders + TransformFailedOrders;
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
    int       PendingReview             = 0,
    // INFORMATIONAL ONLY — orders parked unrouted, awaiting a USER action (assign a supplier).
    // Like PendingReview, a backlog not a fault → excluded from TotalProblemOrders.
    int       PendingRouting            = 0,
    // NEEDS ATTENTION, NOT A FAULT — orders paused at the delivery step because the org could not
    // process orders at that moment (billing lapsed: past_due / read_only / cancelled). The
    // transformed artifact is intact and DeliveryService.ReleaseBillingHeldOrdersAsync releases the
    // hold automatically on reactivation (re-driving it — or restoring a held park to
    // delivery_unconfirmed, where the count below and TotalProblemOrders pick it back up), so a
    // hold is DELIBERATE and self-resolving.
    //
    // Deliberately EXCLUDED from TotalProblemOrders, like PendingReview / PendingRouting (founder
    // call, 2026-07-16): that total is documented as "sum of all problematic order counts", and a
    // deliberate, auto-releasing pause is not a problem order — counting it there would contradict
    // the product rule that a hold is never rendered as a failure. It is surfaced as its own count
    // so an operator sees it, and the operations/health "All clear" gate checks deliveryHeld
    // DIRECTLY (opsHealthState.ts) rather than via this aggregate — so a paused PO still can never
    // read as "All clear". The render layer tones held amber (attention), never red (failure).
    int       DeliveryHeld              = 0,
    // Orders whose delivery outcome is unknown after a crash on a channel that cannot de-duplicate
    // a re-send. INCLUDED in TotalProblemOrders — the opposite call to DeliveryHeld above, and for
    // the reason that founder call turns on: a hold is deliberate and self-releasing, whereas a
    // park is a FAULT (a crash lost the outcome; the PO may never have reached the supplier) that
    // stays parked until a human resolves it. It is neither deliberate nor self-resolving, so the
    // PendingReview / PendingRouting backlog precedent does not cover it.
    int       DeliveryUnconfirmed       = 0)
{
    /// <summary>
    /// Sum of the order counts meaning "something is WRONG and needs an operator" — one input to the
    /// "All clear" gate. Excludes OpenExceptions (can overlap order states), PendingReview /
    /// PendingRouting (normal user-action backlogs), and DeliveryHeld (a deliberate, self-releasing
    /// billing pause — not a fault). The health gate must therefore check those counts directly and
    /// must NOT treat this aggregate as the whole truth; see the DeliveryHeld remarks.
    /// </summary>
    public int TotalProblemOrders =>
        ParsingStuck + DeliveringStuck + TransformFailed + DeliveryFailed +
        DeliveryDeadLetter + RejectedBySupplier + Failed + DeliveryUnconfirmed;
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
