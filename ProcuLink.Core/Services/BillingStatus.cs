namespace ProcuLink.Core.Services;

/// <summary>
/// Snapshot of a tenant's billing state. Returned by GET /api/billing/status.
/// Pilot order usage is counted since trial_started_at; paid plans use month-to-date usage.
///
/// <para>
/// Overage model: an active PAID plan is NEVER blocked for going over its monthly
/// order limit. Over the cap, orders still process and the surplus accrues a
/// per-order fee (<c>OverageOrders</c> × €0.50 = <c>OverageAmountEur</c>) billed
/// via Stripe at the period boundary. <c>OrderLimit</c> here is the EFFECTIVE
/// limit (admin override ?? plan default). <c>NearLimit</c> is ≥80% usage,
/// <c>AtLimit</c> is ≥100% — both are warnings, not blocks.
/// </para>
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
    string? StripeSubscriptionId,
    // ── Overage surfacing (frontend warns; never blocks a paid plan) ──────
    int OverageOrders,
    decimal OverageAmountEur,
    bool NearLimit,
    bool AtLimit);
