namespace ProcuLink.Core.Constants;

public static class AccountStatusConstants
{
    public const string Trialing = "trialing";
    public const string Active = "active";
    public const string TrialExpired = "trial_expired";
    public const string PastDue = "past_due";
    public const string ReadOnly = "read_only";
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Statuses that put an org into a READ-ONLY / non-processing state: the trial
    /// ended, a payment is past due, the account was frozen, or the subscription was
    /// cancelled. In every one of these the org cannot process orders and loses gated
    /// features (see <c>StripeBillingService.CanProcessOrders</c> / <c>HasFeatureAsync</c>).
    /// Kept here as the single source of truth so every "is this org in good standing?"
    /// decision — order processing, feature gating, AND the AI token-budget clamp —
    /// agrees on the same set (case-insensitive; account_status is persisted lowercase).
    /// </summary>
    public static readonly IReadOnlySet<string> ReadOnlyStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TrialExpired,
            PastDue,
            ReadOnly,
            Cancelled,
        };

    /// <summary>True when <paramref name="status"/> is a read-only / delinquent state
    /// (see <see cref="ReadOnlyStatuses"/>). Null or unknown statuses are NOT read-only.</summary>
    public static bool IsReadOnly(string? status) =>
        status is not null && ReadOnlyStatuses.Contains(status);
}
