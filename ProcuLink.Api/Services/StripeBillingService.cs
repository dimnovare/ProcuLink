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
    private readonly IAnalyticsService _analytics;

    public StripeBillingService(
        ProcuLinkDbContext db,
        IConfiguration config,
        ILogger<StripeBillingService> logger,
        IAnalyticsService analytics)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _analytics = analytics;
    }

    /// <summary>
    /// Emits the <c>billing_upgraded</c> analytics event. Called from the Stripe
    /// <c>checkout.session.completed</c> webhook when an org moves to a paid plan.
    /// </summary>
    public Task EmitBillingUpgradedAsync(
        Guid orgId,
        string fromPlan,
        string toPlan,
        string stripeSessionId,
        CancellationToken ct = default) =>
        _analytics.CaptureAsync(
            organisationId: orgId,
            userId: null,
            eventName: "billing_upgraded",
            properties: new Dictionary<string, object?>
            {
                ["from_plan"]          = fromPlan,
                ["to_plan"]            = toPlan,
                ["stripe_session_id"]  = stripeSessionId,
            },
            ct: ct);

    /// <summary>
    /// Emits the <c>billing_downgraded</c> analytics event. Called from the Stripe
    /// <c>customer.subscription.updated</c> webhook when an org moves to a lower-tier plan.
    /// </summary>
    public Task EmitBillingDowngradedAsync(
        Guid orgId,
        string fromPlan,
        string toPlan,
        CancellationToken ct = default) =>
        _analytics.CaptureAsync(
            organisationId: orgId,
            userId: null,
            eventName: "billing_downgraded",
            properties: new Dictionary<string, object?>
            {
                ["from_plan"] = fromPlan,
                ["to_plan"]   = toPlan,
            },
            ct: ct);

    /// <summary>
    /// Emits the <c>billing_cancelled</c> analytics event. Called from the Stripe
    /// <c>customer.subscription.deleted</c> webhook. <paramref name="hadOrdersThisMonth"/>
    /// must reflect whether the org processed any orders in the current Stripe billing cycle.
    /// </summary>
    public Task EmitBillingCancelledAsync(
        Guid orgId,
        string previousPlan,
        bool hadOrdersThisMonth,
        CancellationToken ct = default) =>
        _analytics.CaptureAsync(
            organisationId: orgId,
            userId: null,
            eventName: "billing_cancelled",
            properties: new Dictionary<string, object?>
            {
                ["previous_plan"]         = previousPlan,
                ["had_orders_this_month"] = hadOrdersThisMonth,
            },
            ct: ct);

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
        string frontendUrl,
        string billingInterval,
        CancellationToken ct = default)
    {
        plan = plan.ToLowerInvariant();
        var interval = NormalizeBillingInterval(billingInterval);
        var priceId = (plan, interval) switch
        {
            (PlanConstants.Growth, "yearly") => _config["Stripe:GrowthYearlyPriceId"],
            (PlanConstants.Operations, "yearly") => _config["Stripe:OperationsYearlyPriceId"],
            (PlanConstants.Integration, "yearly") => _config["Stripe:IntegrationYearlyPriceId"],
            (PlanConstants.Distributor, "yearly") => _config["Stripe:DistributorYearlyPriceId"],
            (PlanConstants.Growth, _) => _config["Stripe:GrowthPriceId"],
            (PlanConstants.Operations, _) => _config["Stripe:OperationsPriceId"],
            (PlanConstants.Integration, _) => _config["Stripe:IntegrationPriceId"],
            (PlanConstants.Distributor, _) => _config["Stripe:DistributorPriceId"],
            _ => throw new ArgumentException($"No Stripe Checkout for plan '{plan}'.")
        };

        if (string.IsNullOrWhiteSpace(priceId))
            throw new InvalidOperationException($"Stripe {interval} price ID not configured for plan '{plan}'.");

        var org = await LoadOrgAsync(orgId, asTracking: false, ct);

        // success_url routes to /welcome so the frontend can show the upgraded banner.
        // {CHECKOUT_SESSION_ID} is a Stripe substitution token — C# {{ }} escaping produces
        // the literal braces that Stripe requires at runtime.
        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = new List<SessionLineItemOptions>
            {
                new() { Price = priceId, Quantity = 1 }
            },
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string>
                {
                    ["plan"] = plan,
                    ["billing_interval"] = interval,
                }
            },
            Metadata = new Dictionary<string, string>
            {
                ["org_id"] = orgId.ToString(),
                ["plan"] = plan,
                ["billing_interval"] = interval,
            },
            SuccessUrl = $"{frontendUrl}/welcome?upgraded={plan}&interval={interval}&session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl  = $"{frontendUrl}/settings",
            AllowPromotionCodes = true,
        };

        if (org.StripeCustomerId is not null)
            options.Customer = org.StripeCustomerId;

        var service = new SessionService();
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    // ── Admin: MRR reconciliation against Stripe ──────────────────────────
    /// <summary>
    /// Sums the monthly-normalised amount of all ACTIVE/trialing Stripe
    /// subscriptions across the whole account (this is cross-tenant; only the
    /// admin surface calls it). Yearly subscriptions are divided by 12.
    /// Returns null when Stripe is not configured (no SecretKey) — the caller
    /// must treat a null as "not reconciled" and fall back to the DB number.
    /// Never throws on the unconfigured path and never logs the key.
    /// </summary>
    public async Task<decimal?> GetStripeMrrAsync(CancellationToken ct = default)
    {
        if (!IsStripeConfigured()) return null;

        decimal totalMrrCents = 0m;
        var service = new SubscriptionService();
        var options = new SubscriptionListOptions
        {
            Status = "active",
            Limit  = 100,
        };

        // Page through every active subscription.
        await foreach (var sub in service.ListAutoPagingAsync(options, cancellationToken: ct))
        {
            foreach (var item in sub.Items?.Data ?? new List<SubscriptionItem>())
            {
                var price = item.Price;
                if (price?.UnitAmount is not { } unitAmount) continue;
                var quantity = item.Quantity > 0 ? item.Quantity : 1; // SubscriptionItem.Quantity is non-nullable Int64
                var lineCents = (decimal)unitAmount * quantity;

                // Normalise to a monthly figure.
                var interval      = price.Recurring?.Interval;          // "month" | "year" | "week" | "day"
                var intervalCount = price.Recurring?.IntervalCount ?? 1;
                var monthlyCents = interval switch
                {
                    "year"  => intervalCount > 0 ? lineCents / (12m * intervalCount) : lineCents / 12m,
                    "month" => intervalCount > 0 ? lineCents / intervalCount : lineCents,
                    "week"  => lineCents * 52m / 12m,
                    "day"   => lineCents * 365m / 12m,
                    _       => lineCents,
                };
                totalMrrCents += monthlyCents;
            }
        }

        return Math.Round(totalMrrCents / 100m, 2);
    }

    // ── Admin: create a one-off invoice ───────────────────────────────────
    /// <summary>
    /// Creates a one-off (manual) Stripe invoice for an org's Stripe customer:
    /// adds each line item, finalises the invoice (so Stripe generates the PDF,
    /// VAT, and hosted payment link), and returns its identifiers. Founder-led
    /// onboarding / higher-tier setup flows through this.
    ///
    /// Throws <see cref="BillingNotConfiguredException"/> when Stripe is not
    /// configured — the controller maps that to a clean 4xx, never a 500, and
    /// the Stripe secret is never logged.
    /// </summary>
    public async Task<InvoiceCreationResult> CreateInvoiceAsync(
        Guid orgId,
        IReadOnlyList<InvoiceLineItemInput> lineItems,
        string? currency = null,
        CancellationToken ct = default)
    {
        if (!IsStripeConfigured())
            throw new BillingNotConfiguredException("Stripe is not configured; cannot create an invoice.");

        if (lineItems is null || lineItems.Count == 0)
            throw new ArgumentException("At least one line item is required.", nameof(lineItems));

        var org = await LoadOrgAsync(orgId, asTracking: false, ct);
        if (string.IsNullOrWhiteSpace(org.StripeCustomerId))
            throw new InvalidOperationException("This organisation has no Stripe customer; cannot create an invoice.");

        var cur = string.IsNullOrWhiteSpace(currency) ? "eur" : currency.Trim().ToLowerInvariant();

        // 1) Create a draft invoice the items will attach to (auto_advance=false
        //    so we control finalisation explicitly).
        var invoiceService = new InvoiceService();
        var draft = await invoiceService.CreateAsync(new InvoiceCreateOptions
        {
            Customer            = org.StripeCustomerId,
            Currency            = cur,
            CollectionMethod    = "send_invoice",
            DaysUntilDue        = 14,
            AutoAdvance         = false,
            Metadata            = new Dictionary<string, string> { ["org_id"] = orgId.ToString() },
        }, cancellationToken: ct);

        // 2) Attach each line item to that specific invoice.
        var itemService = new InvoiceItemService();
        foreach (var li in lineItems)
        {
            // InvoiceItem.Amount is the TOTAL line amount (cents); compute it from
            // unit price × quantity ourselves, then record the quantity for display.
            var qty = li.Quantity <= 0 ? 1 : li.Quantity;
            await itemService.CreateAsync(new InvoiceItemCreateOptions
            {
                Customer    = org.StripeCustomerId,
                Invoice     = draft.Id,
                Currency    = cur,
                Amount      = li.AmountCents * qty,
                Description = li.Description,
            }, cancellationToken: ct);
        }

        // 3) Finalise so Stripe produces the PDF + hosted payment link.
        var finalised = await invoiceService.FinalizeInvoiceAsync(
            draft.Id, new InvoiceFinalizeOptions { AutoAdvance = true }, cancellationToken: ct);

        return new InvoiceCreationResult(
            InvoiceId:        finalised.Id,
            HostedInvoiceUrl: finalised.HostedInvoiceUrl,
            Status:           finalised.Status);
    }

    private bool IsStripeConfigured() =>
        !string.IsNullOrWhiteSpace(_config["Stripe:SecretKey"]);

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
                .CountAsync(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= org.TrialStartedAt, ct);
        }

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= monthStart, ct);
    }

    private Task<int> CountSuppliersAsync(Guid orgId, CancellationToken ct) =>
        _db.Suppliers.CountAsync(s => s.OrgId == orgId && s.DeletedAt == null, ct);

    private static DateTime GetTrialEndsAt(Core.Entities.Organisation org) =>
        org.TrialEndsAt ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);

    private static string NormalizePlan(string plan) =>
        PlanConstants.All.Contains(plan) ? plan : PlanConstants.Pilot;

    private static string NormalizeBillingInterval(string? billingInterval)
    {
        var value = (billingInterval ?? "monthly").Trim().ToLowerInvariant();
        return value == "yearly" ? "yearly" : "monthly";
    }

    private static bool IsReadOnlyStatus(string status) =>
        status is AccountStatusConstants.TrialExpired
            or AccountStatusConstants.ReadOnly
            or AccountStatusConstants.PastDue
            or AccountStatusConstants.Cancelled;
}

/// <summary>One line of a manual admin invoice. Amount is in the smallest currency unit (cents).</summary>
public sealed record InvoiceLineItemInput(string Description, long AmountCents, int Quantity);

/// <summary>Identifiers returned after a one-off Stripe invoice is finalised.</summary>
public sealed record InvoiceCreationResult(string InvoiceId, string? HostedInvoiceUrl, string Status);

/// <summary>
/// Thrown by billing operations that require a configured Stripe account when
/// no <c>Stripe:SecretKey</c> is set. Controllers map this to a clean 4xx
/// (never a 500); the secret is never included in the message or logged.
/// </summary>
public sealed class BillingNotConfiguredException : Exception
{
    public BillingNotConfiguredException(string message) : base(message) { }
}
