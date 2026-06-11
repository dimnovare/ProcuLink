using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Services;

public interface IBillingService
{
    /// <summary>Returns full billing snapshot for the settings page.</summary>
    Task<BillingStatus> GetStatusAsync(Guid orgId, CancellationToken ct = default);

    Task<bool> CanProcessOrdersAsync(Guid orgId, CancellationToken ct = default);

    Task<bool> CanAddSupplierAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the org may process another order.
    /// Pilot: cumulative count vs PilotOrderLimit; also checks time window.
    /// Paid: monthly count vs plan limit.
    /// </summary>
    Task<LimitCheckResult> CheckOrderLimitAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>Checks whether the org may add another active supplier.</summary>
    Task<LimitCheckResult> CheckSupplierLimitAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>Returns true if the org's plan includes the requested feature.</summary>
    Task<bool> HasFeatureAsync(Guid orgId, BillingFeature feature, CancellationToken ct = default);

    /// <summary>
    /// Creates a Stripe Checkout session for the given plan. Returns the redirect URL.
    /// <paramref name="frontendUrl"/> is the bare frontend origin (e.g. https://app.proculink.com).
    /// The implementation builds success_url as {frontendUrl}/welcome?upgraded={plan}&amp;session_id={CHECKOUT_SESSION_ID}
    /// and cancel_url as {frontendUrl}/settings.
    /// </summary>
    Task<string> CreateCheckoutSessionAsync(
        Guid orgId,
        string plan,
        string frontendUrl,
        string billingInterval,
        CancellationToken ct = default);

    /// <summary>Creates a Stripe Customer Portal session. Returns the redirect URL.</summary>
    Task<string> CreatePortalSessionAsync(Guid orgId, string returnUrl, CancellationToken ct = default);

    Task MarkPilotStartedAsync(Guid orgId, CancellationToken ct = default);

    Task MarkPilotExpiredIfNeededAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>Records a pilot extension request and notifies sales. Idempotent.</summary>
    Task RequestPilotExtensionAsync(Guid orgId, CancellationToken ct = default);

    // ── Billing events + period overage (called from the Stripe webhook flow) ──
    // These are part of the billing contract so the webhook handler calls them
    // through this interface — never via a runtime downcast to a concrete impl.
    // A downcast would silently no-op (skipping analytics AND overage billing)
    // if IBillingService ever resolved to a different implementation.

    /// <summary>
    /// Emits the <c>billing_upgraded</c> analytics event. Called from the Stripe
    /// <c>checkout.session.completed</c> webhook when an org moves to a paid plan.
    /// </summary>
    Task EmitBillingUpgradedAsync(
        Guid orgId,
        string fromPlan,
        string toPlan,
        string stripeSessionId,
        CancellationToken ct = default);

    /// <summary>
    /// Emits the <c>billing_downgraded</c> analytics event. Called from the Stripe
    /// <c>customer.subscription.updated</c> webhook when an org moves to a lower-tier plan.
    /// </summary>
    Task EmitBillingDowngradedAsync(
        Guid orgId,
        string fromPlan,
        string toPlan,
        CancellationToken ct = default);

    /// <summary>
    /// Emits the <c>billing_cancelled</c> analytics event. Called from the Stripe
    /// <c>customer.subscription.deleted</c> webhook. <paramref name="hadOrdersThisMonth"/>
    /// must reflect whether the org processed any orders in the current Stripe billing cycle.
    /// </summary>
    Task EmitBillingCancelledAsync(
        Guid orgId,
        string previousPlan,
        bool hadOrdersThisMonth,
        CancellationToken ct = default);

    /// <summary>
    /// Computes how many orders an org processed ABOVE its effective monthly cap
    /// within the given billing window [periodStart, periodEnd). Pilot/Enterprise
    /// and unconfigured plans return 0. The cap is resolved AS OF the window start
    /// from the org's plan history (latest row ≤ periodStart), so PAST windows of a
    /// yearly renewal invoice are metered at the plan/override in force back then —
    /// never retroactively re-priced by a later plan change. Orgs with no history
    /// meter with their current values. Used by the webhook to bill the just-closed period.
    /// </summary>
    Task<int> ComputePeriodOverageOrdersAsync(
        Guid orgId,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken ct = default);

    /// <summary>
    /// Bills the per-order overage for an organisation as a Stripe invoice item
    /// on the customer (the item is swept into the customer's NEXT invoice).
    /// Idempotent on (orgId, billingKey). Safe (never throws, no Stripe item) when
    /// Stripe is not configured. Called from the <c>invoice.created</c> webhook
    /// when the triggering invoice is not attachable (not a draft).
    /// </summary>
    Task<OverageBillingResult> BillOverageForInvoiceAsync(
        Guid orgId,
        string billingKey,
        int overageOrders,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="BillOverageForInvoiceAsync(Guid, string, int, CancellationToken)"/>
    /// but ATTACHES the overage invoice item to the given DRAFT Stripe invoice (the
    /// one whose <c>invoice.created</c> event is being handled), so the charge lands
    /// on the invoice that closes the period instead of floating to the next one —
    /// on a yearly subscription "the next one" is ~a year away, and a cancellation
    /// would strand the item entirely. The caller must pass a draft invoice's id;
    /// idempotency semantics (ledger row + Stripe Idempotency-Key on the period
    /// billing key) are identical to the customer-sweep overload.
    /// </summary>
    Task<OverageBillingResult> BillOverageForInvoiceAsync(
        Guid orgId,
        string billingKey,
        int overageOrders,
        string stripeInvoiceId,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of an overage-billing attempt. <see cref="AlreadyBilled"/> is true when
/// the (orgId, billingKey) slot was already taken (idempotent no-op). When Stripe
/// is unconfigured or the org has no Stripe customer, <see cref="StripeItemId"/>
/// is null but the computed amount is still returned for auditing.
/// </summary>
public sealed record OverageBillingResult(
    Guid OrgId,
    string BillingKey,
    int OverageOrders,
    long AmountCents,
    bool AlreadyBilled,
    string? StripeItemId);
