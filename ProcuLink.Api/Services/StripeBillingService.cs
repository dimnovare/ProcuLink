using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
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
    /// <summary>
    /// Feature-flag configuration key for the DELIVERED-ONLY billing meter (founder-approved
    /// flip, 2026-06-11). Absent/false — the default — keeps the historical meter: every
    /// non-sample order counts against quota/overage the moment it enters processing
    /// (CreatedAt), which is exactly what the pricing FAQ and Terms currently describe, so
    /// flag OFF keeps the published copy true. True switches both the quota count
    /// (<see cref="CountOrdersAsync"/>) and the overage meter
    /// (<see cref="ComputePeriodOverageOrdersAsync"/>) to count only orders that REACHED
    /// the supplier — see those methods for the exact status set and the late-delivery
    /// semantics. Flipping this flag requires the matching FAQ/Terms copy change (kept in
    /// the introducing commit's message), never the other way round.
    /// </summary>
    public const string CountDeliveredOnlyFlagKey = "Billing:CountDeliveredOnly";

    private static readonly HashSet<string> ProcessingAllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        AccountStatusConstants.Trialing,
        AccountStatusConstants.Active,
    };

    private readonly ProcuLinkDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<StripeBillingService> _logger;
    private readonly IAnalyticsService _analytics;
    private readonly bool _countDeliveredOnly;
    private readonly IStripeClient? _stripeClient;

    public StripeBillingService(
        ProcuLinkDbContext db,
        IConfiguration config,
        ILogger<StripeBillingService> logger,
        IAnalyticsService analytics,
        // Optional Stripe transport override, used ONLY by tests to capture the
        // overage invoice-item request without HTTP. Unregistered in DI (the
        // default), so production resolves null and the Stripe.net services use
        // the global client exactly as before.
        IStripeClient? stripeClient = null)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _analytics = analytics;
        _stripeClient = stripeClient;
        // Raw read + bool.TryParse (no Binder dependency) — same pattern as the
        // Connections:RevisionAuthority flag. Anything but "true", including the key being
        // absent (the safe production default), leaves the flag OFF and the billing meter
        // byte-identical to the pre-flag created-at behaviour.
        _countDeliveredOnly = bool.TryParse(config[CountDeliveredOnlyFlagKey], out var deliveredOnly) && deliveredOnly;
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
        // Effective limits respect the per-org admin override (override ?? plan default).
        var orderLimit = PlanConstants.GetEffectiveOrderLimit(plan, org.OrderLimitOverride);
        var supplierLimit = PlanConstants.GetEffectiveSupplierLimit(plan, org.SupplierLimitOverride);
        var ordersUsed = await CountOrdersAsync(org, ct);
        var suppliersUsed = await CountSuppliersAsync(orgId, ct);
        var trialEndsAt = GetTrialEndsAt(org);
        // Pilot hard cap respects the effective order limit (so an admin can grant a beefier Pilot).
        var pilotOrderCap = PlanConstants.GetEffectiveOrderLimit(PlanConstants.Pilot, org.OrderLimitOverride);
        var isTrialExpired = plan == PlanConstants.Pilot &&
            (DateTime.UtcNow > trialEndsAt || ordersUsed >= pilotOrderCap);
        var isOrderLimitReached = ordersUsed >= orderLimit;
        var isSupplierLimitReached = suppliersUsed >= supplierLimit;
        var statusAllowsProcessing = ProcessingAllowedStatuses.Contains(org.AccountStatus);

        // ── #1 NEVER BLOCK a real order on an active paid plan due to volume ──
        // Over the monthly cap is ALLOWED (it accrues overage, surfaced below).
        // The only volume-style block is Pilot-EXPIRED (a trial-ended/read-only
        // state, NOT a volume block — that is intentionally kept). Paid plans are
        // blocked ONLY by a non-processing account status (past_due / read_only /
        // cancelled), never by hitting the order cap.
        var canProcessOrders = plan == PlanConstants.Enterprise
            ? !IsReadOnlyStatus(org.AccountStatus)
            : plan == PlanConstants.Pilot
                ? statusAllowsProcessing && !isTrialExpired && !isOrderLimitReached
                : statusAllowsProcessing; // active paid plan: never blocked by volume

        // Adding a supplier is a SETUP action (not a live order), so the supplier
        // cap may still gate it — but it must never block PROCESSING orders for
        // existing suppliers (that is canProcessOrders above, which ignores the
        // supplier cap). Admin override raises the effective supplier limit.
        var canAddSupplier = plan == PlanConstants.Enterprise
            ? !IsReadOnlyStatus(org.AccountStatus)
            : statusAllowsProcessing && !isTrialExpired && !isSupplierLimitReached;

        // ── Overage (active paid self-serve plans only) ──────────────────────
        // BEST-PRICE overage: orders over the effective cap accrue €0.50/order, but
        // the total is capped so a lower tier + overage NEVER costs more than the
        // cheapest higher self-serve tier whose included volume covers the usage —
        // this removes the upgrade-disincentivising "overage inversion". Only above
        // the TOP self-serve tier's included volume does pure per-order overage apply.
        // Pilot accrues no overage (hard trial cap); Enterprise is custom. See
        // PlanConstants.BestPriceOverageOrders for the formula.
        var chargesOverage = PlanConstants.IsPaidPlan(plan) && plan != PlanConstants.Enterprise;
        var overageOrders = chargesOverage
            ? PlanConstants.BestPriceOverageOrders(plan, orderLimit, ordersUsed)
            : 0;
        var overageAmountEur = overageOrders * PlanConstants.OveragePerOrderEur;
        var nearLimit = orderLimit > 0 && ordersUsed >= (int)Math.Ceiling(orderLimit * 0.8);
        var atLimit = ordersUsed >= orderLimit;

        // Enterprise SSO (SAML/OIDC) availability is pure plan metadata — true only
        // on Enterprise. It signals the Settings "Single sign-on" tab to show the
        // available/contact-us state instead of the upsell; it does NOT assert a
        // SAML connection is already configured (that is per-org in the Clerk
        // Dashboard). See docs/strategy/2026-06-08-sso-saml-implementation.md.
        var ssoAvailable = PlanConstants.PlanHasFeature(plan, BillingFeature.Sso);

        // Payment interval (monthly|yearly) derived from the persisted Stripe price id
        // — cheap (config compare, no Stripe HTTP). Informational only: the order
        // quota above is ALWAYS a calendar-month window regardless of interval.
        var billingInterval = GetBillingIntervalFromPriceId(org.StripePriceId);

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
            StripeSubscriptionId: org.StripeSubscriptionId,
            OverageOrders: overageOrders,
            OverageAmountEur: overageAmountEur,
            NearLimit: nearLimit,
            AtLimit: atLimit,
            SsoAvailable: ssoAvailable,
            BillingInterval: billingInterval);
    }

    // Config keys whose values are the YEARLY Stripe price ids. A persisted
    // org.StripePriceId matching any of these means the subscription is annual.
    private static readonly string[] YearlyPriceConfigKeys =
    {
        "Stripe:GrowthYearlyPriceId",
        "Stripe:OperationsYearlyPriceId",
        "Stripe:IntegrationYearlyPriceId",
        "Stripe:DistributorYearlyPriceId",
    };

    /// <summary>
    /// Maps a persisted Stripe price id to a billing interval: null when no price is
    /// on file (no Stripe subscription — Pilot / manual Enterprise), "yearly" when it
    /// matches a configured <c>Stripe:*YearlyPriceId</c>, otherwise "monthly".
    /// Pure config comparison — never calls Stripe.
    /// </summary>
    private string? GetBillingIntervalFromPriceId(string? stripePriceId)
    {
        if (string.IsNullOrWhiteSpace(stripePriceId)) return null;

        foreach (var key in YearlyPriceConfigKeys)
        {
            var configured = _config[key];
            if (!string.IsNullOrWhiteSpace(configured) && configured == stripePriceId)
                return "yearly";
        }
        return "monthly";
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

    // ── Overage billing at the period boundary ────────────────────────────
    /// <summary>
    /// Bills the per-order overage for an organisation as a Stripe INVOICE ITEM
    /// on the subscription customer. Called from the Stripe webhook flow (e.g.
    /// <c>invoice.created</c> / <c>invoice.upcoming</c>) so the overage from the
    /// orders processed over the cap is attached BEFORE the invoice finalises.
    ///
    /// <para>IDEMPOTENT: an <see cref="OverageBillingRecord"/> row keyed on
    /// (orgId, <paramref name="billingKey"/>) is inserted first; the unique index
    /// means a replayed webhook / Hangfire retry hits a duplicate-key violation
    /// that is swallowed — never a second invoice item. <paramref name="billingKey"/>
    /// is the BILLING-PERIOD key (<c>{orgId}:{periodStart:O}</c>), NOT the Stripe
    /// invoice id, so a re-issued invoice for the same period cannot double-bill.
    /// The same key is also passed to Stripe as the request <c>Idempotency-Key</c>
    /// for defense-in-depth on top of the DB ledger.</para>
    ///
    /// <para>Safe when Stripe is NOT configured: it records the computed overage
    /// (so the number is auditable) but creates no Stripe item and never throws.
    /// Always returns the computed overage regardless of Stripe state — the
    /// caller must never let this block an order.</para>
    /// </summary>
    /// <param name="orgId">Organisation to bill.</param>
    /// <param name="billingKey">Period-based idempotency key (<c>{orgId}:{periodStart:O}</c>).</param>
    /// <param name="overageOrders">Orders above the cap for the period being closed.</param>
    public Task<OverageBillingResult> BillOverageForInvoiceAsync(
        Guid orgId,
        string billingKey,
        int overageOrders,
        CancellationToken ct = default) =>
        BillOverageCoreAsync(orgId, billingKey, overageOrders, stripeInvoiceId: null, ct);

    /// <summary>
    /// Attach variant: identical money path, but the created invoice item carries
    /// <c>Invoice = stripeInvoiceId</c> so it lands ON the triggering DRAFT invoice
    /// (the period-close invoice) instead of sweeping to the customer's next one —
    /// which on a yearly subscription is ~a year of float, and is never issued at
    /// all after a cancellation. The caller (webhook) only passes an id when the
    /// event's invoice status is <c>draft</c>. Idempotency keys are unchanged.
    /// </summary>
    public Task<OverageBillingResult> BillOverageForInvoiceAsync(
        Guid orgId,
        string billingKey,
        int overageOrders,
        string stripeInvoiceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stripeInvoiceId))
            throw new ArgumentException("stripeInvoiceId is required on the attach overload.", nameof(stripeInvoiceId));
        return BillOverageCoreAsync(orgId, billingKey, overageOrders, stripeInvoiceId, ct);
    }

    private async Task<OverageBillingResult> BillOverageCoreAsync(
        Guid orgId,
        string billingKey,
        int overageOrders,
        string? stripeInvoiceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(billingKey))
            throw new ArgumentException("billingKey is required.", nameof(billingKey));

        var clamped = Math.Max(0, overageOrders);
        var amountCents = (long)Math.Round(clamped * PlanConstants.OveragePerOrderEur * 100m);

        // Nothing to bill — still idempotent (record a zero so repeats are no-ops),
        // but skip Stripe entirely.
        if (clamped == 0 || amountCents == 0)
            return new OverageBillingResult(orgId, billingKey, 0, 0, AlreadyBilled: false, StripeItemId: null);

        // ── Is Stripe the billing authority for this org right now? ──────────
        // Only when Stripe is configured AND the org has a Stripe customer do we
        // expect a real invoice item to be created. Otherwise the ledger row is the
        // ONLY record (auditable number, no Stripe side), and a missing
        // StripeInvoiceItemId is the expected terminal state — NOT a failure to retry.
        var org = await LoadOrgAsync(orgId, asTracking: true, ct);
        var stripeIsAuthority = IsStripeConfigured() && !string.IsNullOrWhiteSpace(org.StripeCustomerId);

        // ── Idempotency guard: examine any existing (orgId, billingKey) row ──
        // Terminal (idempotent no-op) when EITHER:
        //   • the row already carries a Stripe invoice-item id (fully billed), OR
        //   • Stripe is not the authority here (unconfigured / no customer) — the row
        //     is the only intended record, so a NULL item id is expected, not a retry.
        // A NULL-item row WHILE Stripe IS the authority is the poison case: a prior
        // attempt committed the ledger row (the row + SaveChanges happen BEFORE the
        // Stripe call) and then the Stripe call failed (transient 5xx / rate-limit /
        // network) → the webhook 500'd and Stripe will retry. Returning AlreadyBilled
        // there would let the NULL-item row permanently suppress the charge → silent
        // revenue loss. So in that case we DON'T early-return: we fall through and
        // (re)attempt the Stripe call. The Stripe Idempotency-Key (= billingKey)
        // makes the retry safe — if a prior attempt did secretly create the item,
        // Stripe returns the SAME item (never a duplicate).
        static bool IsTerminal(OverageBillingRecord r, bool stripeAuthority) =>
            !string.IsNullOrEmpty(r.StripeInvoiceItemId) || !stripeAuthority;

        var existing = await _db.OverageBillingRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.BillingKey == billingKey, ct);
        if (existing is not null && IsTerminal(existing, stripeIsAuthority))
            return new OverageBillingResult(
                orgId, billingKey, existing.OverageOrders, existing.AmountCents,
                AlreadyBilled: true, StripeItemId: existing.StripeInvoiceItemId);

        // Claim the slot only when there is no row yet. When a NULL-item row already
        // exists (and Stripe is the authority) we reuse it — skip the insert and go
        // straight to the Stripe (re)attempt below.
        if (existing is null)
        {
            var record = new OverageBillingRecord
            {
                Id            = Guid.NewGuid(),
                OrgId         = orgId,
                BillingKey    = billingKey,
                OverageOrders = clamped,
                AmountCents   = amountCents,
                CreatedAt     = DateTimeOffset.UtcNow,
            };
            _db.OverageBillingRecords.Add(record);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Lost the race — another worker already inserted this (orgId, billingKey).
                // Detach our attempt and re-read the winner.
                _db.Entry(record).State = EntityState.Detached;
                var winner = await _db.OverageBillingRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.OrgId == orgId && r.BillingKey == billingKey, ct);
                // Same terminal rule on the race-loser path: treat as already billed only
                // when the winner is terminal (has a Stripe item id, or Stripe is not the
                // authority); otherwise fall through and (re)attempt Stripe on the shared
                // period key (idempotent via the Stripe Idempotency-Key).
                if (winner is not null && IsTerminal(winner, stripeIsAuthority))
                    return new OverageBillingResult(
                        orgId, billingKey, winner.OverageOrders, winner.AmountCents,
                        AlreadyBilled: true, StripeItemId: winner.StripeInvoiceItemId);
            }
        }

        // We own the slot (or are recovering a prior NULL-item row). If Stripe is not
        // the authority (unconfigured / no customer) we still recorded the number
        // (auditable) but create no item and never throw.
        if (!stripeIsAuthority)
            return new OverageBillingResult(orgId, billingKey, clamped, amountCents, AlreadyBilled: false, StripeItemId: null);

        // The injected client exists only in tests; production always takes the
        // parameterless path (global Stripe client) — byte-identical behaviour.
        var itemService = _stripeClient is null ? new InvoiceItemService() : new InvoiceItemService(_stripeClient);
        var item = await itemService.CreateAsync(new InvoiceItemCreateOptions
        {
            Customer    = org.StripeCustomerId,
            // When the webhook saw a DRAFT period-close invoice, pin the item to it
            // (Stripe rejects attaching to a finalised invoice; the webhook never
            // passes an id for those). Null ⇒ property omitted ⇒ the historical
            // customer-sweep behaviour, unchanged.
            Invoice     = stripeInvoiceId,
            Currency    = "eur",
            Amount      = amountCents,
            Description = $"Order overage: {clamped} orders x EUR{PlanConstants.OveragePerOrderEur:0.00}",
            Metadata    = new Dictionary<string, string>
            {
                ["org_id"]      = orgId.ToString(),
                ["billing_key"] = billingKey,
                ["kind"]        = "order_overage",
            },
        },
        // Defense-in-depth on top of the DB ledger: make the Stripe call itself
        // idempotent on the same period-based billing key, so even a retry that
        // races past the ledger guard cannot create a duplicate invoice item.
        new RequestOptions { IdempotencyKey = billingKey },
        cancellationToken: ct);

        // Persist the Stripe item id back onto the (tracked) ledger row.
        var saved = await _db.OverageBillingRecords
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.BillingKey == billingKey, ct);
        if (saved is not null)
        {
            saved.StripeInvoiceItemId = item.Id;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Billed order overage for org {OrgId}: {Orders} orders, {Cents} cents (invoice item {ItemId}, key {Key})",
            orgId, clamped, amountCents, item.Id, billingKey);

        return new OverageBillingResult(orgId, billingKey, clamped, amountCents, AlreadyBilled: false, StripeItemId: item.Id);
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
        // ReadOnly is a paid-plan terminal state (e.g. cancelled) — not ours to flip here.
        if (org.AccountStatus is AccountStatusConstants.ReadOnly) return;

        org.TrialEndsAt ??= org.TrialStartedAt.Add(PlanConstants.PilotDuration);
        // Effective deadline + cap respect admin overrides (beefier/extended Pilot).
        var effectiveTrialEnd = GetTrialEndsAt(org);
        var pilotOrderCap = PlanConstants.GetEffectiveOrderLimit(PlanConstants.Pilot, org.OrderLimitOverride);
        var ordersUsed = await CountOrdersAsync(org, ct);
        var expired = DateTime.UtcNow > effectiveTrialEnd || ordersUsed >= pilotOrderCap;

        // Bidirectional: expire when past the effective deadline/cap, AND REACTIVATE a
        // previously-expired Pilot when an admin override (extended trial / raised cap)
        // has put it back inside its window. Without the reactivation branch, an admin
        // could extend a Pilot but the org would stay read-only forever (the early-return
        // bug this replaces). Only TrialExpired⇄Trialing are touched.
        if (expired && org.AccountStatus != AccountStatusConstants.TrialExpired)
        {
            org.AccountStatus = AccountStatusConstants.TrialExpired;
            await _db.SaveChangesAsync(ct);
        }
        else if (!expired && org.AccountStatus == AccountStatusConstants.TrialExpired)
        {
            org.AccountStatus = AccountStatusConstants.Trialing;
            await _db.SaveChangesAsync(ct);
        }
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

    /// <summary>
    /// Counts the org's quota-relevant orders: cumulative since trial start for Pilot,
    /// UTC-calendar-month-to-date for paid plans. Sample orders never count in either mode.
    ///
    /// <para><b>Delivered-only meter (<see cref="CountDeliveredOnlyFlagKey"/>, default OFF).</b>
    /// Flag OFF: every non-sample order counts when it enters processing (CreatedAt) —
    /// byte-identical to the historical meter. Flag ON: the window stays CreatedAt-based
    /// (CREATION-MONTH attribution — an order always belongs to the month it was created
    /// in), but an order is only COUNTED once its status — evaluated at count time — shows
    /// it reached the supplier: <c>delivered</c> OR <c>rejected_by_supplier</c>.
    /// Rejected-by-supplier counts because the full parse/transform/delivery work was
    /// performed and the document actually reached the supplier; the rejection is the
    /// supplier's business outcome, not a ProcuLink delivery failure. All undelivered
    /// states (pending/parsing/review/ready/transforming/delivering, plus the failure
    /// bucket <c>failed</c>/<c>transform_failed</c>/<c>delivery_failed</c>/
    /// <c>delivery_dead_letter</c>) do NOT count.</para>
    /// </summary>
    private async Task<int> CountOrdersAsync(Core.Entities.Organisation org, CancellationToken ct)
    {
        if (NormalizePlan(org.Plan) == PlanConstants.Pilot)
        {
            var pilotOrders = _db.PurchaseOrders
                .Where(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= org.TrialStartedAt);
            return await ApplyMeterStatusFilter(pilotOrders).CountAsync(ct);
        }

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthOrders = _db.PurchaseOrders
            .Where(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= monthStart);
        return await ApplyMeterStatusFilter(monthOrders).CountAsync(ct);
    }

    /// <summary>
    /// Applies the delivered-only status filter when <see cref="CountDeliveredOnlyFlagKey"/>
    /// is ON; a no-op pass-through (the historical count-at-creation meter) when OFF.
    /// Single source of truth for the billable status set: <c>delivered</c> +
    /// <c>rejected_by_supplier</c> (see <see cref="CountOrdersAsync"/> for the rationale).
    /// </summary>
    private IQueryable<PurchaseOrderEntity> ApplyMeterStatusFilter(IQueryable<PurchaseOrderEntity> orders) =>
        _countDeliveredOnly
            ? orders.Where(o => o.Status == OrderStatusConstants.Delivered ||
                                o.Status == OrderStatusConstants.RejectedBySupplier)
            : orders;

    private Task<int> CountSuppliersAsync(Guid orgId, CancellationToken ct) =>
        // Sample suppliers (the onboarding "__sample__" record) must never consume the plan's
        // supplier quota — otherwise a Pilot org that ran the practice flow shows "2 / 1 suppliers".
        _db.Suppliers.CountAsync(s => s.OrgId == orgId && s.DeletedAt == null && !s.IsSample, ct);

    /// <summary>
    /// Computes how many BILLABLE overage orders an org processed ABOVE its effective
    /// monthly cap within the given billing window [periodStart, periodEnd). Uses the
    /// BEST-PRICE cap (see <see cref="PlanConstants.BestPriceOverageOrders"/>) so the
    /// billed overage never makes a lower tier + overage cost more than the cheapest
    /// higher self-serve tier that would have covered the volume — pure per-order
    /// overage only applies above the top self-serve tier's included volume. Sample
    /// orders are excluded (they never count against quota). Pilot/Enterprise return 0
    /// (no overage applies). Used by the webhook to bill the just-closed period (each
    /// calendar-month window of it, for yearly subscriptions).
    ///
    /// <para><b>Delivered-only meter (<see cref="CountDeliveredOnlyFlagKey"/>, default OFF).</b>
    /// Same flag + same status filter as <see cref="CountOrdersAsync"/>: when ON, an order
    /// is attributed to the window containing its CreatedAt (creation-month attribution —
    /// the window predicate is unchanged) but only counted if, at invoice/count time, it
    /// has reached the supplier (<c>delivered</c> or <c>rejected_by_supplier</c>).</para>
    ///
    /// <para><b>Late-delivery semantics (flag ON) — customer-favorable, NO catch-up
    /// billing.</b> An order created in May but delivered in June AFTER the May invoice was
    /// issued is NEVER billed: at May-invoice time it was undelivered (excluded from the
    /// May window count), and every June-or-later window filters on CreatedAt inside that
    /// window (creation-month attribution), so the May-created order can never re-enter a
    /// later window. No job re-opens closed periods to catch late deliveries — that
    /// under-counting is deliberate. (A Stripe webhook replay for an already-billed period
    /// is additionally blocked by the period-based idempotency key in
    /// <c>BillingController.HandleInvoiceCreatedAsync</c>, which is unchanged.)</para>
    /// </summary>
    public async Task<int> ComputePeriodOverageOrdersAsync(
        Guid orgId,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, asTracking: false, ct);

        // ── AS-OF plan/override resolution (yearly-billing correctness) ──────
        // A yearly renewal invoice decomposes into ~12 PAST monthly windows; each
        // must be metered at the plan + admin order-limit override in force AT THAT
        // window's start, never at today's values — otherwise a mid-year DOWNGRADE
        // retroactively re-meters old months at the new lower cap (wrongful overage
        // charges) and an upgrade under-bills. The latest org_plan_history row at or
        // before the window start wins; an org with NO row at or before the window
        // (pre-feature orgs whose boot baseline seed has not run, or a window that
        // somehow precedes the org's first row) falls back to the org's CURRENT
        // values — exactly the pre-history behaviour, documented and pinned by tests.
        var windowStart = new DateTimeOffset(
            periodStart.Kind == DateTimeKind.Utc ? periodStart : DateTime.SpecifyKind(periodStart, DateTimeKind.Utc));
        var asOf = await _db.OrgPlanHistories
            .AsNoTracking()
            .Where(h => h.OrgId == orgId && h.EffectiveFrom <= windowStart)
            .OrderByDescending(h => h.EffectiveFrom)
            .ThenByDescending(h => h.Id) // deterministic tie-break (same-instant rows)
            .FirstOrDefaultAsync(ct);

        var plan = NormalizePlan(asOf?.Plan ?? org.Plan);
        var orderLimitOverride = asOf is not null ? asOf.OrderLimitOverride : org.OrderLimitOverride;
        if (!PlanConstants.IsPaidPlan(plan) || plan == PlanConstants.Enterprise)
            return 0;

        var limit = PlanConstants.GetEffectiveOrderLimit(plan, orderLimitOverride);
        var windowOrders = _db.PurchaseOrders
            .Where(o => o.OrgId == orgId && !o.IsSample &&
                        o.CreatedAt >= periodStart && o.CreatedAt < periodEnd);
        var used = await ApplyMeterStatusFilter(windowOrders).CountAsync(ct);
        return PlanConstants.BestPriceOverageOrders(plan, limit, used);
    }

    private static DateTime GetTrialEndsAt(Core.Entities.Organisation org) =>
        // Admin override wins, then the persisted trial end, then the computed default.
        org.TrialEndsAtOverride ?? org.TrialEndsAt ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);

    private static string NormalizePlan(string plan) =>
        PlanConstants.All.Contains(plan) ? plan : PlanConstants.Pilot;

    private static string NormalizeBillingInterval(string? billingInterval)
    {
        var value = (billingInterval ?? "monthly").Trim().ToLowerInvariant();
        return value == "yearly" ? "yearly" : "monthly";
    }

    // Single source of truth for the read-only status set (shared with the AI
    // token-budget clamp in AiUsageTracker) so the two never drift — the drift
    // between them was the AI cost-leak the 2026-07-10 audit flagged.
    private static bool IsReadOnlyStatus(string status) =>
        AccountStatusConstants.IsReadOnly(status);
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
