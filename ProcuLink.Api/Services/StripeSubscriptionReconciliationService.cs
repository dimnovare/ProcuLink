using System.Net;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Stripe;

namespace ProcuLink.Api.Services;

/// <summary>
/// Re-derives one org's plan + account_status from Stripe as source of truth. Idempotent and
/// org-scoped. Backstops missed <c>customer.subscription.*</c> webhooks and stale/test-mode
/// subscription ids that live webhooks never fire for.
///
/// <para><b>Safety.</b> The only access-REMOVING path (downgrade to frozen Pilot + read_only)
/// is triple-guarded: (1) a configured Stripe key is required; (2) a "missing" verdict requires
/// a 404 <c>resource_missing</c> SPECIFICALLY — a 401/429/5xx never downgrades; (3) a vanished
/// (404) subscription serves a 3-day confirmed-missing grace before downgrade, so a TRANSIENT
/// misread cannot strip a real customer's access. A resolving sub whose status is authoritatively
/// <c>canceled</c>/<c>incomplete_expired</c> downgrades immediately (no grace — that is not a
/// transient misread).</para>
///
/// <para><b>Systemic-fault guard.</b> The grace above only covers a TRANSIENT slip. A PERSISTENT
/// valid-but-wrong key (wrong live/test mode, or a live key on the wrong connected account)
/// authenticates (no 401) yet 404s EVERY live subscription, which would mass-downgrade the paying
/// base ~3 days later. That systemic case is caught by <c>BillingReconciliationJob</c>'s
/// mass-downgrade circuit breaker (it aborts + alerts when an abnormal number of orgs are
/// simultaneously past the grace window), NOT by this per-org service.</para>
///
/// <para><b>Worker-safe Stripe client.</b> The Worker process never sets
/// <c>StripeConfiguration.ApiKey</c>, so this service builds its OWN <see cref="StripeClient"/>
/// from <c>Stripe:SecretKey</c> rather than relying on the process-global client. An optional
/// injected <see cref="IStripeClient"/> is the test seam (unregistered in DI).</para>
/// </summary>
public sealed class StripeSubscriptionReconciliationService : IBillingReconciliationService
{
    /// <summary>
    /// Confirmed-missing grace before a vanished (404) subscription is downgraded. Public so the
    /// job's mass-downgrade circuit breaker can count orgs that are past this same window without
    /// duplicating the constant.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(3);

    private readonly ProcuLinkDbContext _db;
    private readonly IConfiguration _config;
    private readonly IBillingService _billing;   // EmitBillingCancelledAsync on downgrade
    private readonly Core.Services.Delivery.IDeliveryService _delivery; // release billing-held orders on heal
    private readonly ILogger<StripeSubscriptionReconciliationService> _logger;
    private readonly IStripeClient? _stripeClient; // test seam; null in prod → built from config

    public StripeSubscriptionReconciliationService(
        ProcuLinkDbContext db,
        IConfiguration config,
        IBillingService billing,
        Core.Services.Delivery.IDeliveryService delivery,
        ILogger<StripeSubscriptionReconciliationService> logger,
        IStripeClient? stripeClient = null)
    {
        _db = db;
        _config = config;
        _billing = billing;
        _delivery = delivery;
        _logger = logger;
        _stripeClient = stripeClient;
    }

    public async Task ReconcileOrgAsync(Guid orgId, CancellationToken ct = default)
    {
        var secret = _config["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret)) return; // never touch state without a configured key

        // The sweep shares ONE scoped DbContext across every org. Clear any entity left tracked by a
        // prior org whose SaveChanges threw — otherwise EF would batch that poisoned change into THIS
        // org's SaveChanges (cross-org contamination / cascade). Each org is loaded fresh below, so
        // starting from an empty tracker is always correct and also bounds change-tracker growth.
        _db.ChangeTracker.Clear();

        var org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null || string.IsNullOrWhiteSpace(org.StripeSubscriptionId)) return;

        // Build the client from config so this works in the Worker (which never sets the
        // process-global StripeConfiguration.ApiKey). The injected client is used only by tests.
        var client = _stripeClient ?? new StripeClient(secret);
        var subscriptions = new SubscriptionService(client);

        Subscription sub;
        try
        {
            sub = await subscriptions.GetAsync(org.StripeSubscriptionId, cancellationToken: ct);
        }
        catch (StripeException ex) when (IsResourceMissing(ex))
        {
            await HandleMissingAsync(org, subscriptions, ct);
            return;
        }
        catch (StripeException ex)
        {
            // 401 auth / 429 rate-limit / 5xx / network — transient or config, NOT a missing sub.
            // NEVER downgrade on these; a wrong/rotated key must not read as "subscription gone".
            _logger.LogError(ex,
                "Reconcile: non-404 Stripe error for org {OrgId} sub {SubId} (http {Http}); leaving state untouched.",
                org.Id, org.StripeSubscriptionId, ex.HttpStatusCode);
            return;
        }

        await ApplyResolvedAsync(org, sub, subscriptions, ct);
    }

    /// <summary>True only for a 404 that Stripe attributes to a missing object (not a 401/5xx).</summary>
    private static bool IsResourceMissing(StripeException ex) =>
        ex.HttpStatusCode == HttpStatusCode.NotFound
        && string.Equals(ex.StripeError?.Code, "resource_missing", StringComparison.Ordinal);

    private async Task ApplyResolvedAsync(Organisation org, Subscription sub, SubscriptionService subscriptions, CancellationToken ct)
    {
        // Capture BEFORE any mutation so we can release billing-held orders when a MISSED
        // reactivation webhook is healed here (read_only/past_due → active/trialing). Also covers
        // the re-subscribe adoption path, which recurses into this method with the live sub.
        var wasReadOnly = AccountStatusConstants.IsReadOnly(org.AccountStatus);

        var status = sub.Status ?? string.Empty;
        var priceId = sub.Items?.Data?.FirstOrDefault()?.Price?.Id;

        // DEAD — the subscription resolves but is authoritatively over. Before downgrading, check
        // whether the customer has a NEWER active/trialing subscription (a re-subscribe whose
        // checkout webhook we also missed): adopt it rather than freezing a paying customer. Only
        // when there is none do we downgrade — immediately (a resolving 'canceled' is not a
        // transient misread, so no grace).
        if (status is "canceled" or "incomplete_expired")
        {
            if (await TryAdoptActiveSubscriptionAsync(org, subscriptions, ct)) return;
            await DowngradeAsync(org, org.StripeSubscriptionId!, ct);
            return;
        }

        var targetPlan = org.Plan;
        var targetStatus = org.AccountStatus;

        if (status is "active" or "trialing")
        {
            var mapped = StripeBillingMapping.MapPriceIdToPlan(_config, priceId);
            if (!string.IsNullOrEmpty(mapped)) targetPlan = mapped; // keep existing when unrecognised (manual Enterprise)
            targetStatus = StripeBillingMapping.MapStatusToAccountStatus(status, org.AccountStatus);
        }
        else if (status is "past_due" or "unpaid" or "paused")
        {
            // Dunning (past_due/unpaid → past_due) or paused billing (paused → read_only): plan kept,
            // account_status re-derived via the shared mapping so the webhook and reconciliation agree.
            targetStatus = StripeBillingMapping.MapStatusToAccountStatus(status, org.AccountStatus);
        }
        else
        {
            // incomplete / unknown — resolves but no actionable mapping. Leave plan+status; the sub
            // is present, so any stale missing marker is cleared below.
            _logger.LogInformation("Reconcile: org {OrgId} sub status '{Status}' unmapped — plan/status unchanged.", org.Id, status);
        }

        var changed = false;
        // Adopting a re-subscribe replaces the stored (dead) id with the live one; a no-op in the
        // normal path where sub.Id already equals the stored id.
        if (!string.IsNullOrEmpty(sub.Id) && org.StripeSubscriptionId != sub.Id) { org.StripeSubscriptionId = sub.Id; changed = true; }
        if (org.Plan != targetPlan) { org.Plan = targetPlan; changed = true; }
        if (org.AccountStatus != targetStatus) { org.AccountStatus = targetStatus; changed = true; }
        // Only ever overwrite the price id with a real value — never null out a live sub's price.
        if (!string.IsNullOrEmpty(priceId) && org.StripePriceId != priceId) { org.StripePriceId = priceId; changed = true; }
        if (org.StripeSubscriptionStatus != status) { org.StripeSubscriptionStatus = status; changed = true; }
        if (org.StripeReconciliationMissingSince is not null) { org.StripeReconciliationMissingSince = null; changed = true; } // self-heal

        if (changed)
        {
            org.BillingUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Reconcile: org {OrgId} → plan={Plan}, status={AccountStatus} (stripe {SubStatus}).",
                org.Id, org.Plan, org.AccountStatus, status);
        }

        // A MISSED reactivation webhook is exactly why this service exists — so it, too, must
        // re-drive orders that were billing-held at delivery time. Without this, an org healed
        // read_only→active here (not via the live webhook) would strand its delivery_held orders
        // FOREVER (no sweep watches delivery_held; RetryDeliveryAsync rejects it). Mirrors
        // BillingController's reactivation gate.
        await ReleaseHeldOrdersIfReactivatedAsync(org, wasReadOnly, ct);
    }

    /// <summary>
    /// When reconciliation heals an org from a read-only/delinquent status back into a processing
    /// status, release any billing-held orders back into delivery. Best-effort: a release failure
    /// must never fail the reconcile (which would leave the org's billing state un-reconciled).
    /// </summary>
    private async Task ReleaseHeldOrdersIfReactivatedAsync(Organisation org, bool wasReadOnly, CancellationToken ct)
    {
        if (!wasReadOnly || AccountStatusConstants.IsReadOnly(org.AccountStatus))
            return;

        try
        {
            var released = await _delivery.ReleaseBillingHeldOrdersAsync(org.Id, ct);
            if (released > 0)
                _logger.LogInformation(
                    "Reconcile: org {OrgId} healed read_only→processing — released {Count} billing-held order(s) into delivery.",
                    org.Id, released);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Reconcile: org {OrgId} healed to processing but releasing billing-held orders failed (non-fatal).",
                org.Id);
        }
    }

    private async Task HandleMissingAsync(Organisation org, SubscriptionService subscriptions, CancellationToken ct)
    {
        var deadSubId = org.StripeSubscriptionId;

        if (org.StripeReconciliationMissingSince is null)
        {
            // First observation — start the grace clock, do NOT downgrade. Set only when null so
            // the marker ages correctly across daily runs (idempotent).
            org.StripeReconciliationMissingSince = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Reconcile: org {OrgId} subscription {SubId} not found in Stripe (resource_missing) — grace window started ({Days}d).",
                org.Id, deadSubId, GracePeriod.TotalDays);
            return;
        }

        if (DateTime.UtcNow - org.StripeReconciliationMissingSince.Value >= GracePeriod)
        {
            // Past grace — but a customer who re-subscribed (with a webhook we also missed) has a
            // NEW active sub under the same customer; adopt it rather than freeze them.
            if (await TryAdoptActiveSubscriptionAsync(org, subscriptions, ct)) return;
            await DowngradeAsync(org, deadSubId!, ct);
        }
        else
        {
            _logger.LogInformation("Reconcile: org {OrgId} subscription still missing but within grace (since {Since:o}).",
                org.Id, org.StripeReconciliationMissingSince);
        }
    }

    /// <summary>
    /// Before downgrading a dead/vanished subscription, check whether the customer has a NEWER
    /// active/trialing subscription (a re-subscribe whose <c>checkout.completed</c> webhook we also
    /// missed). If so, adopt it — reconcile the org to that live sub, replacing the stored dead id —
    /// and return true, so a customer who is actually paying again is never frozen. Returns false
    /// (caller then downgrades) when there is no customer id on file, the list call fails, or the
    /// customer has no active/trialing subscription.
    /// </summary>
    private async Task<bool> TryAdoptActiveSubscriptionAsync(Organisation org, SubscriptionService subscriptions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(org.StripeCustomerId)) return false;

        StripeList<Subscription> subs;
        try
        {
            subs = await subscriptions.ListAsync(new SubscriptionListOptions
            {
                Customer = org.StripeCustomerId,
                Limit = 10,
            }, cancellationToken: ct);
        }
        catch (StripeException ex)
        {
            // Cannot confirm the customer's subscriptions — do NOT adopt, and do not let this throw
            // out of the per-org step; the caller proceeds to downgrade the confirmed-dead sub.
            _logger.LogError(ex,
                "Reconcile: could not list subscriptions for org {OrgId} customer {Cus}; skipping re-subscribe adoption.",
                org.Id, org.StripeCustomerId);
            return false;
        }

        var live = subs?.Data?.FirstOrDefault(s => s.Status is "active" or "trialing");
        if (live is null) return false;

        _logger.LogWarning(
            "Reconcile: org {OrgId} had a dead/missing subscription but customer {Cus} has an ACTIVE subscription {NewSub} — adopting it instead of downgrading.",
            org.Id, org.StripeCustomerId, live.Id);
        // Reconcile to the live sub — ApplyResolvedAsync writes the new id (its change detection),
        // plan, status, and clears the missing marker. A live sub can never re-enter this method.
        await ApplyResolvedAsync(org, live, subscriptions, ct);
        return true;
    }

    private async Task DowngradeAsync(Organisation org, string deadSubId, CancellationToken ct)
    {
        var previousPlan = org.Plan;
        org.Plan = PlanConstants.Pilot;
        org.AccountStatus = AccountStatusConstants.ReadOnly;
        org.StripeSubscriptionId = null;             // clear — org drops out of the sweep next run (self-terminating)
        org.StripePriceId = null;                     // clear
        org.StripeSubscriptionStatus = "canceled";    // forensic marker
        org.StripeReconciliationMissingSince = null;  // downgrade applied
        org.BillingUpdatedAt = DateTime.UtcNow;       // StripeCustomerId intentionally kept (portal / re-subscribe)
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Reconcile: downgraded org {OrgId} to frozen Pilot + read_only — dead/vanished subscription {DeadSubId}.",
            org.Id, deadSubId);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var hadOrders = await _db.PurchaseOrders
            .AnyAsync(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= monthStart, ct);
        try
        {
            await _billing.EmitBillingCancelledAsync(org.Id, previousPlan, hadOrders, ct);
        }
        catch (Exception ex)
        {
            // Analytics is best-effort — a PostHog hiccup must never fail (or roll back) the
            // already-committed downgrade, nor propagate out of the per-org sweep step.
            _logger.LogError(ex,
                "Reconcile: billing_cancelled analytics emit failed for org {OrgId} (downgrade already persisted).", org.Id);
        }
    }
}
