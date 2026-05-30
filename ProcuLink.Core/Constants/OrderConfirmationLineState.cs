namespace ProcuLink.Core.Constants;

/// <summary>
/// Per-line outcome on a supplier order confirmation. Each confirmation line is matched
/// against its ordered purchase-order line and classified:
///
///   Confirmed ─ supplier confirmed qty, date, and price exactly as ordered.
///   Changed   ─ supplier altered at least one of qty / delivery date / unit price.
///   Rejected  ─ supplier declined this specific line.
///
/// The presence of any <see cref="Changed"/> or <see cref="Rejected"/> line drives the
/// parent confirmation toward <c>NeedsReview</c> / <c>AcceptedWithChanges</c> / <c>Rejected</c>.
/// </summary>
public static class OrderConfirmationLineState
{
    /// <summary>Supplier confirmed this line exactly as ordered.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Supplier changed quantity, delivery date, or unit price on this line.</summary>
    public const string Changed = "changed";

    /// <summary>Supplier rejected this specific line.</summary>
    public const string Rejected = "rejected";

    /// <summary>All recognised per-line states.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Confirmed, Changed, Rejected,
    };

    /// <summary>True when <paramref name="state"/> is a recognised line state.</summary>
    public static bool IsValid(string? state) =>
        state is not null && All.Contains(state);
}
