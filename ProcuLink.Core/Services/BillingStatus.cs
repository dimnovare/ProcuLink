namespace ProcuLink.Core.Services;

/// <summary>
/// Snapshot of a tenant's billing state. Returned by GET /api/billing/status.
/// Pilot order usage is counted since trial_started_at; paid plans use month-to-date usage.
/// </summary>
public sealed record BillingStatus(
    string Plan,
    string AccountStatus,
    int OrdersThisMonth,
    int OrderLimit,
    int SuppliersUsed,
    int SupplierLimit,
    DateTime? TrialStartedAt,
    DateTime? TrialEndsAt,
    bool IsTrialExpired,
    bool IsOrderLimitReached,
    bool IsSupplierLimitReached,
    bool CanProcessOrders,
    bool CanAddSupplier,
    string? StripeCustomerId,
    string? StripeSubscriptionId);
