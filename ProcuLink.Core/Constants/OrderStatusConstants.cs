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
    public const string DeliveryFailed = "delivery_failed";
    public const string TransformFailed = "transform_failed";
    public const string RejectedBySupplier = "rejected_by_supplier";
    public const string DeliveryDeadLetter = "delivery_dead_letter";
    public const string Failed = "failed";

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
