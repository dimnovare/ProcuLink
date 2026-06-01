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
}
