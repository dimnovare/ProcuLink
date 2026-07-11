namespace ProcuLink.Core.Services;

/// <summary>
/// Reconciles one organisation's persisted billing state (plan + account_status) against
/// Stripe as source of truth. The safety net for missed subscription webhooks and stale /
/// test-mode subscription ids that live webhooks never fire for. Org-scoped and idempotent;
/// a no-op when Stripe is not configured. Only ever grants/keeps access or downgrades a
/// vanished/dead subscription (after a grace window) — never blocks a healthy paying org.
/// </summary>
public interface IBillingReconciliationService
{
    /// <summary>
    /// Re-derives the org's plan + account_status from its Stripe subscription and persists any
    /// correction. No-op when the org has no <c>StripeSubscriptionId</c> or Stripe is unconfigured.
    /// </summary>
    Task ReconcileOrgAsync(Guid orgId, CancellationToken ct = default);
}
