using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Stripe;
using Stripe.Checkout;

namespace ProcuLink.Api.Services;

/// <summary>
/// Implements billing state, limits, and Stripe Checkout/Portal integration.
/// Pilot is internal and does not use Stripe.
/// </summary>
public sealed class StripeBillingService : IBillingService
{
    private static readonly HashSet<string> ProcessingAllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        AccountStatusConstants.Trialing,
        AccountStatusConstants.Active,
    };

    private readonly ProcuLinkDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<StripeBillingService> _logger;

    public StripeBillingService(
        ProcuLinkDbContext db,
        IConfiguration config,
        ILogger<StripeBillingService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<BillingStatus> GetStatusAsync(Guid orgId, CancellationToken ct = default)
    {
        await MarkPilotExpiredIfNeededAsync(orgId, ct);

        var org = await LoadOrgAsync(orgId, asTracking: false, ct);
        var plan = NormalizePlan(org.Plan);
        var orderLimit = PlanConstants.GetOrderLimit(plan);
        var supplierLimit = PlanConstants.GetSupplierLimit(plan);
        var ordersUsed = await CountOrdersAsync(org, ct);
        var suppliersUsed = await CountSuppliersAsync(orgId, ct);
        var trialEndsAt = GetTrialEndsAt(org);
        var isTrialExpired = plan == PlanConstants.Pilot &&
            (DateTime.UtcNow > trialEndsAt || ordersUsed >= PlanConstants.PilotOrderLimit);
        var isOrderLimitReached = ordersUsed >= orderLimit;
        var isSupplierLimitReached = suppliersUsed >= supplierLimit;
        var statusAllowsProcessing = ProcessingAllowedStatuses.Contains(org.AccountStatus);

        var canProcessOrders = plan == PlanConstants.Enterprise
            ? !IsReadOnlyStatus(org.AccountStatus)
            : statusAllowsProcessing && !isTrialExpired && !isOrderLimitReached;

        var canAddSupplier = plan == PlanConstants.Enterprise
            ? !IsReadOnlyStatus(org.AccountStatus)
            : statusAllowsProcessing && !isTrialExpired && !isSupplierLimitReached;

        return new BillingStatus(
            Plan: plan,
            AccountStatus: org.AccountStatus,
            OrdersThisMonth: ordersUsed,
            OrderLimit: orderLimit,
            SuppliersUsed: suppliersUsed,
            SupplierLimit: supplierLimit,
            TrialStartedAt: org.TrialStartedAt,
            TrialEndsAt: plan == PlanConstants.Pilot ? trialEndsAt : org.TrialEndsAt,
            IsTrialExpired: isTrialExpired,
            IsOrderLimitReached: isOrderLimitReached,
            IsSupplierLimitReached: isSupplierLimitReached,
            CanProcessOrders: canProcessOrders,
            CanAddSupplier: canAddSupplier,
            StripeCustomerId: org.StripeCustomerId,
            StripeSubscriptionId: org.StripeSubscriptionId);
    }

    public async Task<bool> CanProcessOrdersAsync(Guid orgId, CancellationToken ct = default) =>
        (await GetStatusAsync(orgId, ct)).CanProcessOrders;

    public async Task<bool> CanAddSupplierAsync(Guid orgId, CancellationToken ct = default) =>
        (await GetStatusAsync(orgId, ct)).CanAddSupplier;

    public async Task<LimitCheckResult> CheckOrderLimitAsync(Guid orgId, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(orgId, ct);
        return new LimitCheckResult(
            status.CanProcessOrders,
            status.Plan == PlanConstants.Pilot && status.IsTrialExpired,
            status.Plan,
            status.OrderLimit);
    }

    public async Task<LimitCheckResult> CheckSupplierLimitAsync(Guid orgId, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(orgId, ct);
        return new LimitCheckResult(
            status.CanAddSupplier,
            status.Plan == PlanConstants.Pilot && status.IsTrialExpired,
            status.Plan,
            status.SupplierLimit);
    }

    public async Task<bool> HasFeatureAsync(Guid orgId, BillingFeature feature, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, asTracking: false, ct);
        if (IsReadOnlyStatus(org.AccountStatus)) return false;
        return PlanConstants.PlanHasFeature(NormalizePlan(org.Plan), feature);
    }

    public async Task<string> CreateCheckoutSessionAsync(
        Guid orgId,
        string plan,
        string returnUrl,
        CancellationToken ct = default)
    {
        plan = plan.ToLowerInvariant();
        var priceId = plan switch
        {
            PlanConstants.Growth => _config["Stripe:GrowthPriceId"],
            PlanConstants.Operations => _config["Stripe:OperationsPriceId"],
            PlanConstants.Integration => _config["Stripe:IntegrationPriceId"],
            _ => throw new ArgumentException($"No Stripe Checkout for plan '{plan}'.")
        };

        if (string.IsNullOrWhiteSpace(priceId))
            throw new InvalidOperationException($"Stripe price ID not configured for plan '{plan}'.");

        var org = await LoadOrgAsync(orgId, asTracking: false, ct);

        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = new List<SessionLineItemOptions>
            {
                new() { Price = priceId, Quantity = 1 }
            },
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string> { ["plan"] = plan }
            },
            Metadata = new Dictionary<string, string>
            {
                ["org_id"] = orgId.ToString(),
                ["plan"] = plan
            },
            SuccessUrl = $"{returnUrl}?billing=success",
            CancelUrl = returnUrl,
            AllowPromotionCodes = true,
        };

        if (org.StripeCustomerId is not null)
            options.Customer = org.StripeCustomerId;

        var service = new SessionService();
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    public async Task<string> CreatePortalSessionAsync(
        Guid orgId,
        string returnUrl,
        CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, asTracking: false, ct);

        if (org.StripeCustomerId is null)
            throw new InvalidOperationException("No Stripe customer found for this organisation.");

        var options = new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = org.StripeCustomerId,
            ReturnUrl = returnUrl,
        };

        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    public async Task MarkPilotStartedAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, asTracking: true, ct);
        if (org.TrialStartedAt == default)
            org.TrialStartedAt = DateTime.UtcNow;
        org.TrialEndsAt ??= org.TrialStartedAt.Add(PlanConstants.PilotDuration);
        org.Plan = PlanConstants.Pilot;
        org.AccountStatus = AccountStatusConstants.Trialing;
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkPilotExpiredIfNeededAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, asTracking: true, ct);
        if (org.Plan != PlanConstants.Pilot) return;
        if (org.AccountStatus is AccountStatusConstants.TrialExpired or AccountStatusConstants.ReadOnly) return;

        org.TrialEndsAt ??= org.TrialStartedAt.Add(PlanConstants.PilotDuration);
        var ordersUsed = await CountOrdersAsync(org, ct);
        var expired = DateTime.UtcNow > org.TrialEndsAt.Value || ordersUsed >= PlanConstants.PilotOrderLimit;
        if (!expired) return;

        org.AccountStatus = AccountStatusConstants.TrialExpired;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RequestPilotExtensionAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, asTracking: true, ct);

        if (org.PilotExtensionRequestedAt.HasValue)
        {
            _logger.LogInformation("Pilot extension already requested for org {OrgId}", orgId);
            return;
        }

        org.PilotExtensionRequestedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "SALES SIGNAL - Pilot extension requested. OrgId={OrgId} OrgName={OrgName} RequestedAt={At}",
            org.Id, org.Name, org.PilotExtensionRequestedAt);
    }

    private async Task<Core.Entities.Organisation> LoadOrgAsync(Guid orgId, bool asTracking, CancellationToken ct)
    {
        var query = _db.Organisations.AsQueryable();
        if (!asTracking) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");
    }

    private async Task<int> CountOrdersAsync(Core.Entities.Organisation org, CancellationToken ct)
    {
        if (NormalizePlan(org.Plan) == PlanConstants.Pilot)
        {
            return await _db.PurchaseOrders
                .CountAsync(o => o.OrgId == org.Id && o.CreatedAt >= org.TrialStartedAt, ct);
        }

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == org.Id && o.CreatedAt >= monthStart, ct);
    }

    private Task<int> CountSuppliersAsync(Guid orgId, CancellationToken ct) =>
        _db.Suppliers.CountAsync(s => s.OrgId == orgId && s.DeletedAt == null, ct);

    private static DateTime GetTrialEndsAt(Core.Entities.Organisation org) =>
        org.TrialEndsAt ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);

    private static string NormalizePlan(string plan) =>
        PlanConstants.All.Contains(plan) ? plan : PlanConstants.Pilot;

    private static bool IsReadOnlyStatus(string status) =>
        status is AccountStatusConstants.TrialExpired
            or AccountStatusConstants.ReadOnly
            or AccountStatusConstants.PastDue
            or AccountStatusConstants.Cancelled;
}
