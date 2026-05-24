// ProcuLink.Api/Services/StripeBillingService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Stripe;
using Stripe.Checkout;

namespace ProcuLink.Api.Services;

/// <summary>
/// Implements IBillingService using Stripe.net and EF Core.
/// Pilot order counts are cumulative since trial_started_at.
/// Paid-plan order counts use a rolling monthly window.
/// </summary>
public sealed class StripeBillingService : IBillingService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IConfiguration    _config;
    private readonly ILogger<StripeBillingService> _logger;

    public StripeBillingService(
        ProcuLinkDbContext            db,
        IConfiguration                config,
        ILogger<StripeBillingService> logger)
    {
        _db     = db;
        _config = config;
        _logger = logger;
    }

    // ── GetStatusAsync ────────────────────────────────────────────────────

    public async Task<BillingStatus> GetStatusAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, ct);
        var limits = PlanConstants.Limits[org.Plan];

        var ordersUsed      = await CountOrdersAsync(org, ct);
        var suppliersActive = await CountSuppliersAsync(orgId, ct);

        bool pilotExpired        = false;
        DateTime? pilotEndsAt    = null;
        bool extensionRequested  = org.PilotExtensionRequestedAt.HasValue;

        if (org.Plan == PlanConstants.Pilot)
        {
            var effectiveEnd = org.PilotExtendedUntil ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);
            pilotEndsAt  = effectiveEnd;
            pilotExpired = DateTime.UtcNow > effectiveEnd || ordersUsed >= PlanConstants.PilotOrderLimit;
        }

        var features = PlanConstants.FeaturesForPlan(org.Plan)
            .Select(f => f.ToString().ToLowerInvariant())
            .ToArray();

        return new BillingStatus(
            Plan:               org.Plan,
            OrdersUsed:         ordersUsed,
            OrderLimit:         limits.Orders,
            SuppliersActive:    suppliersActive,
            SupplierLimit:      limits.Suppliers,
            PilotEndsAt:        pilotEndsAt,
            PilotExpired:       pilotExpired,
            ExtensionRequested: extensionRequested,
            Features:           features
        );
    }

    // ── CheckOrderLimitAsync ──────────────────────────────────────────────

    public async Task<LimitCheckResult> CheckOrderLimitAsync(Guid orgId, CancellationToken ct = default)
    {
        var org    = await LoadOrgAsync(orgId, ct);
        var limits = PlanConstants.Limits[org.Plan];

        if (org.Plan == PlanConstants.Pilot)
        {
            var effectiveEnd = org.PilotExtendedUntil ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);
            var count        = await CountOrdersAsync(org, ct);
            var expired      = DateTime.UtcNow > effectiveEnd || count >= PlanConstants.PilotOrderLimit;
            return new LimitCheckResult(!expired, PilotExpired: expired, org.Plan, PlanConstants.PilotOrderLimit);
        }

        var monthlyCount = await CountOrdersAsync(org, ct);
        return new LimitCheckResult(monthlyCount < limits.Orders, PilotExpired: false, org.Plan, limits.Orders);
    }

    // ── CheckSupplierLimitAsync ───────────────────────────────────────────

    public async Task<LimitCheckResult> CheckSupplierLimitAsync(Guid orgId, CancellationToken ct = default)
    {
        var org    = await LoadOrgAsync(orgId, ct);
        var limits = PlanConstants.Limits[org.Plan];

        if (org.Plan == PlanConstants.Pilot)
        {
            var effectiveEnd = org.PilotExtendedUntil ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);
            var expired      = DateTime.UtcNow > effectiveEnd;
            if (expired) return new LimitCheckResult(false, PilotExpired: true, org.Plan, PlanConstants.PilotSupplierLimit);
        }

        var active  = await CountSuppliersAsync(orgId, ct);
        var allowed = active < limits.Suppliers;
        return new LimitCheckResult(allowed, PilotExpired: false, org.Plan, limits.Suppliers);
    }

    // ── HasFeatureAsync ───────────────────────────────────────────────────

    public async Task<bool> HasFeatureAsync(Guid orgId, BillingFeature feature, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, ct);
        return PlanConstants.PlanHasFeature(org.Plan, feature);
    }

    // ── CreateCheckoutSessionAsync ────────────────────────────────────────

    public async Task<string> CreateCheckoutSessionAsync(
        Guid orgId, string plan, string returnUrl, CancellationToken ct = default)
    {
        var priceId = plan switch
        {
            PlanConstants.Growth      => _config["Stripe:GrowthPriceId"],
            PlanConstants.Operations  => _config["Stripe:OperationsPriceId"],
            PlanConstants.Integration => _config["Stripe:IntegrationPriceId"],
            _ => throw new ArgumentException($"No Stripe price configured for plan '{plan}'.")
        };

        var org = await LoadOrgAsync(orgId, ct);

        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = new List<SessionLineItemOptions>
            {
                new() { Price = priceId, Quantity = 1 }
            },
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                TrialPeriodDays = 14,
                Metadata = new Dictionary<string, string> { ["plan"] = plan }
            },
            Metadata      = new Dictionary<string, string> { ["org_id"] = orgId.ToString(), ["plan"] = plan },
            SuccessUrl    = $"{returnUrl}?billing=success",
            CancelUrl     = returnUrl,
            AllowPromotionCodes = true,
        };

        if (org.StripeCustomerId is not null)
            options.Customer = org.StripeCustomerId;

        var service = new SessionService();
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    // ── CreatePortalSessionAsync ──────────────────────────────────────────

    public async Task<string> CreatePortalSessionAsync(
        Guid orgId, string returnUrl, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, ct);

        if (org.StripeCustomerId is null)
            throw new InvalidOperationException("No Stripe customer found for this organisation.");

        var options = new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer  = org.StripeCustomerId,
            ReturnUrl = returnUrl,
        };

        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    // ── RequestPilotExtensionAsync ────────────────────────────────────────

    public async Task RequestPilotExtensionAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");

        if (org.PilotExtensionRequestedAt.HasValue)
        {
            _logger.LogInformation("Pilot extension already requested for org {OrgId}", orgId);
            return; // idempotent
        }

        org.PilotExtensionRequestedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "SALES SIGNAL — Pilot extension requested. OrgId={OrgId} OrgName={OrgName} RequestedAt={At}",
            org.Id, org.Name, org.PilotExtensionRequestedAt);
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<Core.Entities.Organisation> LoadOrgAsync(Guid orgId, CancellationToken ct)
    {
        return await _db.Organisations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");
    }

    private async Task<int> CountOrdersAsync(Core.Entities.Organisation org, CancellationToken ct)
    {
        if (org.Plan == PlanConstants.Pilot)
        {
            // Cumulative count since trial start (no monthly reset)
            return await _db.PurchaseOrders
                .CountAsync(o => o.OrgId == org.Id && o.CreatedAt >= org.TrialStartedAt, ct);
        }

        // Rolling monthly window for paid plans
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == org.Id && o.CreatedAt >= monthStart, ct);
    }

    private Task<int> CountSuppliersAsync(Guid orgId, CancellationToken ct) =>
        _db.Suppliers.CountAsync(s => s.OrgId == orgId && s.DeletedAt == null, ct);
}
