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
    /// automatically released when the org returns to good standing (see
    /// <c>IDeliveryService.ReleaseBillingHeldOrdersAsync</c>) — back to <see cref="ReadyToDeliver"/>
    /// and re-driven, except a held PARK (<c>PurchaseOrderEntity.HeldFromStatus</c> ==
    /// <see cref="DeliveryUnconfirmed"/>), which is restored to its park for a human instead of
    /// being auto re-sent. Either way a mid-pipeline billing flip can never SILENTLY strand a
    /// paid, transformed order.
    /// </summary>
    public const string DeliveryHeld = "delivery_held";
    public const string DeliveryFailed = "delivery_failed";

    /// <summary>
    /// A send was ATTEMPTED and its outcome is unknown (a crash lost the ACK — or struck before the
    /// send, which looks identical from here) on a channel that cannot de-duplicate a re-send —
    /// ERP, email, legacy SMTP. Re-sending could hand the supplier a duplicate PO, so the order
    /// waits for a human: "Send again" or "Mark as delivered". Deliberately NOT
    /// <see cref="DeliveryFailed"/> — we do not know that it failed, and equally not
    /// <see cref="Delivered"/> — we never saw it arrive.
    /// <para>
    /// Billing: whether a parked order is metered depends on the <c>Billing:CountDeliveredOnly</c>
    /// flag. ON, the meter counts only delivered/rejected_by_supplier, so a park is non-billable
    /// until an operator confirms it. The flag DEFAULTS OFF (<c>StripeBillingService</c>) and is in
    /// no checked-in config, so the SHIPPED default is the historical count-at-creation meter, which
    /// counts every order whatever its status — a parked order included. Not a park-specific charge:
    /// <see cref="DeliveryFailed"/> meters identically today.
    /// </para>
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
    /// <para>
    /// REACHABLE IN PRODUCTION TODAY — do not treat this as a future state. The single writer is
    /// <c>OrderIngestionService.cs</c> (<c>if (entity.SupplierId is null) newStatus = Unrouted</c>) on
    /// the main parse path, fed by four ingress channels when an org has no valid default supplier
    /// (unset, or soft-deleted after ingress was enabled): the three pull channels
    /// <c>SftpIngressService</c>, <c>S3IngressService</c> and <c>EmailPollOrgJob</c>, plus the
    /// PUSH channel <c>InboundEmailRouter</c> (the Postmark inbound webhook — added 2026-07-24;
    /// it previously answered 422 and dropped the mail). Each imports such files via
    /// <c>IOrderService.CreateUnroutedStubAsync</c> rather than dropping them. Corroborating live
    /// consumers: <c>OrdersController</c>'s assign-supplier endpoint is gated on this status,
    /// <c>OpsHealthService</c> reports it as <c>PendingRouting</c>, and <c>OrderExceptionService</c>
    /// opens an <c>unrouted_order</c> exception for it with precedence over unresolved lines.
    /// </para>
    /// <para>
    /// This doc previously read "No order reaches this state until the content-routing ingest paths
    /// are wired (Phase 1)". That was true when written and became false when Phase 1b shipped, and
    /// nothing failed — a frontend author then trusted it and shipped a status map that renders a
    /// parked, awaiting-assignment order as brand-new "New" at stage 0, hiding the one order in the
    /// inbox that needs a human. If you change who writes this status, change this paragraph: it is
    /// read as a reachability contract by code in the other repo.
    /// </para>
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
