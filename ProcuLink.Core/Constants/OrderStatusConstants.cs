namespace ProcuLink.Core.Constants;

public static class OrderStatusConstants
{
    public const string PendingParse = "pending_parse";
    public const string Parsing = "parsing";
    public const string PendingReview = "pending_review";
    public const string Ready = "ready";
    public const string Transforming = "transforming";
    public const string ReadyToDeliver = "ready_to_deliver";
    public const string Delivering = "delivering";
    public const string Delivered = "delivered";

    /// <summary>
    /// Delivery paused because the org cannot currently process orders (billing: past_due /
    /// read_only / trial_expired / cancelled) at the moment its transform-ready order reached the
    /// delivery job. NOT a failure and NOT lost: the transformed artifact is intact. The order is
    /// automatically released back to <see cref="ReadyToDeliver"/> and re-driven when the org
    /// returns to good standing (see <c>IDeliveryService.ReleaseBillingHeldOrdersAsync</c>), so a
    /// mid-pipeline billing flip can never SILENTLY strand a paid, transformed order.
    /// </summary>
    public const string DeliveryHeld = "delivery_held";
    public const string DeliveryFailed = "delivery_failed";

    /// <summary>
    /// A send happened but its outcome is unknown (a crash lost the ACK) on a channel that cannot
    /// de-duplicate a re-send — ERP, email, legacy SMTP. Re-sending could hand the supplier a
    /// duplicate PO, so the order waits for a human: "Send again" or "Mark as delivered".
    /// Deliberately NOT <see cref="DeliveryFailed"/> — we do not know that it failed. Non-billable
    /// until an operator confirms delivery (the meter counts only delivered/rejected_by_supplier).
    /// </summary>
    public const string DeliveryUnconfirmed = "delivery_unconfirmed";
    public const string TransformFailed = "transform_failed";
    public const string RejectedBySupplier = "rejected_by_supplier";
    public const string DeliveryDeadLetter = "delivery_dead_letter";
    public const string Failed = "failed";

    /// <summary>
    /// Routing hold state: the source file was extracted but no supplier could be determined,
    /// so the order is parked awaiting a human (or content-matcher) to assign one. NOT a failure —
    /// a user-action backlog, resolvable by assigning a supplier, which re-enters the parse flow.
    /// Introduced by the supplier-routing track (Phase 0). No order reaches this state until the
    /// content-routing ingest paths are wired (Phase 1).
    /// </summary>
    public const string Unrouted = "unrouted";

    /// <summary>
    /// Every status the UI renders as the single red "Failed" pill. When an order list
    /// is filtered by <see cref="Failed"/>, it must match this whole bucket — otherwise
    /// the filter silently drops the four non-<c>failed</c> failure states.
    /// </summary>
    public static readonly IReadOnlySet<string> FailureBucket = new HashSet<string>(StringComparer.Ordinal)
    {
        Failed,
        TransformFailed,
        DeliveryFailed,
        DeliveryDeadLetter,
        RejectedBySupplier,
    };
}
