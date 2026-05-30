namespace ProcuLink.Core.Constants;

/// <summary>
/// Lifecycle status of a supplier order confirmation (an order-level acknowledgement
/// of a purchase order). Answers: did the supplier accept, reject, change, or not respond?
///
/// Flow (typical):
///   Sent ──▶ Accepted              (supplier confirms exactly as ordered)
///   Sent ──▶ AcceptedWithChanges   (supplier accepts but altered qty/date/price on ≥1 line)
///   Sent ──▶ NeedsReview           (changes detected that require a human decision)
///   Sent ──▶ Rejected              (supplier declines the order)
///   Sent ──▶ NoResponse            (no confirmation received within the expected window)
///
/// A <see cref="Rejected"/> confirmation must prevent the underlying order from being
/// considered "completed" (see <c>IsBlockingCompletion</c>).
/// </summary>
public static class OrderConfirmationStatus
{
    /// <summary>Order was sent to the supplier; awaiting their confirmation.</summary>
    public const string Sent = "sent";

    /// <summary>Supplier confirmed the order exactly as ordered — no line changes.</summary>
    public const string Accepted = "accepted";

    /// <summary>Supplier accepted the order but changed quantity, date, or price on one or more lines.</summary>
    public const string AcceptedWithChanges = "accepted_with_changes";

    /// <summary>Changes were detected that require a human decision before the order can proceed.</summary>
    public const string NeedsReview = "needs_review";

    /// <summary>Supplier declined the order. Blocks the order from being considered completed.</summary>
    public const string Rejected = "rejected";

    /// <summary>No confirmation was received from the supplier within the expected window.</summary>
    public const string NoResponse = "no_response";

    /// <summary>All recognised confirmation statuses.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Sent, Accepted, AcceptedWithChanges, NeedsReview, Rejected, NoResponse,
    };

    /// <summary>True when <paramref name="status"/> is a recognised confirmation status.</summary>
    public static bool IsValid(string? status) =>
        status is not null && All.Contains(status);

    /// <summary>
    /// True when a confirmation in this status must block the order from being treated as
    /// "completed". Currently only <see cref="Rejected"/> blocks completion: a rejected order
    /// has not been fulfilled, so downstream completion logic must not mark it done.
    /// </summary>
    public static bool IsBlockingCompletion(string? status) =>
        status == Rejected;
}
