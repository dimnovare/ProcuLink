namespace ProcuLink.Core.Services;

/// <summary>
/// Snapshot of a tenant's billing state. Returned by GET /api/billing/status.
/// Pilot order usage is counted since trial_started_at; paid plans use month-to-date usage.
///
/// <para>
/// Overage model: an active PAID plan is NEVER blocked for going over its monthly
/// order limit. Over the cap, orders still process and the surplus accrues a
/// per-order fee (<c>OverageOrders</c> × €0.50 = <c>OverageAmountEur</c>) billed
/// via Stripe at the period boundary. The billable <c>OverageOrders</c> is
/// BEST-PRICE capped (see <c>PlanConstants.BestPriceOverageOrders</c>): a lower
/// tier + overage is NEVER charged more than the cheapest higher self-serve tier
/// whose included volume covers the usage, so upgrading is always at least as cheap.
/// Pure per-order overage applies only above the top self-serve tier's included
/// volume. <c>OrderLimit</c> here is the EFFECTIVE limit (admin override ?? plan
/// default). <c>NearLimit</c> is ≥80% usage, <c>AtLimit</c> is ≥100% — both are
/// warnings, not blocks.
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
    bool AtLimit,
    // ── Capability flag: whether the plan includes Enterprise SSO (SAML/OIDC) ──
    // Computed from PlanConstants.PlanHasFeature(plan, BillingFeature.Sso); pure
    // presentation/policy metadata so the Settings "Single sign-on" tab can show
    // the gated upsell (false) vs the available/contact-us state (true) WITHOUT a
    // Clerk Backend API round-trip. true does NOT mean a SAML connection is already
    // configured for the org — that is provisioned per-org in the Clerk Dashboard
    // (see docs/strategy/2026-06-08-sso-saml-implementation.md). Defaults to false
    // so existing positional construction sites stay correct.
    bool SsoAvailable = false);
