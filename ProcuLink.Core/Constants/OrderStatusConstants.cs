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
    /// delivery job. NOT a failure: the transformed artifact is intact. Stays held: the archive
    /// never sends. (Until 2026-11-13 the order was released automatically when the org returned to
    /// good standing; plan 13b deleted that release with the order pipeline.)
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
    /// <see cref="DeliveryFailed"/> meters identically today ON A PAID PLAN. It no longer does on
    /// the PILOT hard cap, which forgives <see cref="FailureBucket"/> minus
    /// <see cref="RejectedBySupplier"/> in both flag positions (<c>StripeBillingService</c>
    /// <c>PilotForgivenFailureStatuses</c>); a park is NOT forgiven there, because a human can
    /// still resolve it.
    /// </para>
    /// </summary>
    public const string DeliveryUnconfirmed = "delivery_unconfirmed";
    public const string TransformFailed = "transform_failed";
    public const string RejectedBySupplier = "rejected_by_supplier";
    public const string DeliveryDeadLetter = "delivery_dead_letter";
    public const string Failed = "failed";

    /// <summary>
    /// Routing hold state: the source file was extracted but no supplier could be determined, so the
    /// order was parked awaiting a human to assign one. NOT a failure.
    /// <para>
    /// ARCHIVE-ONLY since 2026-11-13: plan 13b deleted the order ingest path, so nothing writes this
    /// status any more and no endpoint resolves it. Rows written earlier remain readable, and
    /// <c>OrderExceptionService</c> still opens an <c>unrouted_order</c> exception for one, with
    /// precedence over unresolved lines. Frontend code reads this as a status contract: if you change
    /// who writes it, change this paragraph.
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

    /// <summary>
    /// Every status where the order is PARKED: it has stopped moving and no job will move it —
    /// only a human can. Distinct from <see cref="FailureBucket"/>, which is "we know this went
    /// wrong"; a parked order may be perfectly fine, but nothing is happening to it.
    ///
    /// <para><b>Declared, not derived — and the omission this closes was real.</b>
    /// <c>DashboardController</c> kept a private "exceptions" copy documented as "everything that
    /// failed, plus the orders parked awaiting a human", and it listed exactly ONE parked status
    /// (<see cref="PendingReview"/>). <see cref="Unrouted"/> and <see cref="DeliveryUnconfirmed"/>
    /// were both missing, so a supplier whose every order was parked after a crash rendered a green
    /// wire and, on the old <c>(total - failed) / total</c> formula, a 100% delivery success rate.
    /// A hand-written list is what dropped them; this one is walked by an EXHAUSTIVE PARTITION test
    /// (<c>OrderStatusBucketPartitionTests</c>) over <c>OrderStatusMachine.AllStatuses</c>, so a new
    /// status that lands in no bucket fails the build instead of defaulting to "healthy".</para>
    ///
    /// <para><b><see cref="DeliveryUnconfirmed"/> belongs here, and that resolves a real
    /// disagreement.</b> The two subsystems classified it oppositely: <c>OpsHealthSummary</c>
    /// counts it in <c>TotalProblemOrders</c> ("a park is a FAULT"), while the dashboard counted it
    /// as neither failure nor exception — i.e. as healthy. Ops health is right. It is NOT a
    /// <see cref="FailureBucket"/> member either, though: the whole point of the status is that we
    /// do not know the send failed. It is parked, which is what this bucket means.</para>
    ///
    /// <para><b><see cref="DeliveryHeld"/> is deliberately EXCLUDED — do not "fix" that.</b> A
    /// billing hold is not waiting on a human acting ON THE ORDER. The founder call recorded on
    /// <c>OpsHealthSummary.DeliveryHeld</c> (2026-07-16) is that a deliberate billing pause must never
    /// render as a failure; it is surfaced as its own count instead. Stays held: the archive never
    /// sends (plan 13b deleted the automatic release with the order pipeline, 2026-11-13).</para>
    /// </summary>
    public static readonly IReadOnlySet<string> AwaitingHumanBucket = new HashSet<string>(StringComparer.Ordinal)
    {
        PendingReview,
        Unrouted,
        DeliveryUnconfirmed,
    };

    /// <summary>
    /// Every status where the pipeline is still WORKING on the order: a job holds it, or a job is
    /// about to pick it up, and no human is needed. Nothing here is an outcome — an order in this
    /// bucket has neither succeeded nor failed, and must never be counted as either.
    ///
    /// <para>Declared so the five buckets form an EXHAUSTIVE PARTITION of every known status
    /// (<c>OrderStatusBucketPartitionTests</c>): failure, parked, in-flight, <see cref="Delivered"/>,
    /// <see cref="DeliveryHeld"/>. Without a declared in-flight set, "everything else" is the
    /// default arm — and a new parked status would silently land in it and read as healthy, which
    /// is exactly the failure this packet fixed. With the partition, a status that no bucket claims
    /// fails the build.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> InFlightBucket = new HashSet<string>(StringComparer.Ordinal)
    {
        PendingParse,
        Parsing,
        Ready,
        Transforming,
        ReadyToDeliver,
        Delivering,
    };

    /// <summary>
    /// Every status whose DELIVERY OUTCOME IS KNOWN — <see cref="Delivered"/> plus every member of
    /// <see cref="FailureBucket"/>. This is the only population a delivery success rate may be
    /// computed over.
    ///
    /// <para>The dashboard's supplier score was <c>(total - failed) / total</c>, which has only two
    /// buckets and therefore counts every order whose outcome is NOT YET KNOWN as a success: an
    /// order still parsing, an order parked awaiting a human, an order held for billing. A supplier
    /// with ten orders and no outcomes scored 100%. Scoring over this set instead means an unknown
    /// outcome is neither a success nor a failure — it is simply not yet measurable, and when
    /// nothing is measurable the score is <c>null</c> rather than a fabricated 100.</para>
    ///
    /// <para>Derived from <see cref="FailureBucket"/>, so a sixth failure status joins the
    /// denominator on the day it is declared rather than silently counting as a success.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> SettledDeliveryBucket =
        new HashSet<string>(FailureBucket.Append(Delivered), StringComparer.Ordinal);
}
