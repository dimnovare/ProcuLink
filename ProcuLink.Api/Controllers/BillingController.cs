// ProcuLink.Api/Controllers/BillingController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/billing")]
public sealed class BillingController : ControllerBase
{
    private readonly IBillingService                    _billing;
    private readonly ICurrentTenantService              _tenant;
    private readonly IConfiguration                     _config;
    private readonly ILogger<BillingController>         _logger;
    private readonly ProcuLinkDbContext                 _db;
    private readonly IAiUsageTracker                    _aiUsage;

    public BillingController(
        IBillingService            billing,
        ICurrentTenantService      tenant,
        IConfiguration             config,
        ILogger<BillingController> logger,
        ProcuLinkDbContext         db,
        IAiUsageTracker            aiUsage)
    {
        _billing = billing;
        _tenant  = tenant;
        _config  = config;
        _logger  = logger;
        _db      = db;
        _aiUsage = aiUsage;
    }

    // ── Plan rank — used to detect downgrades in HandleSubscriptionUpdatedAsync ───────────
    private static readonly Dictionary<string, int> PlanRank = new(StringComparer.OrdinalIgnoreCase)
    {
        [PlanConstants.Pilot]       = 0,
        [PlanConstants.Growth]      = 1,
        [PlanConstants.Operations]  = 2,
        [PlanConstants.Integration] = 3,
        [PlanConstants.Distributor] = 4,
        [PlanConstants.Enterprise]  = 5,
    };

    private static bool IsPlanDowngrade(string? fromPlan, string toPlan) =>
        !string.IsNullOrEmpty(fromPlan)
        && PlanRank.TryGetValue(fromPlan, out var fromRank)
        && PlanRank.TryGetValue(toPlan,   out var toRank)
        && toRank < fromRank;

    // ── GET /api/billing/status ───────────────────────────────────────────

    [HttpGet("status")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var status = await _billing.GetStatusAsync(_tenant.OrganisationId, ct);
        return Ok(status);
    }

    // ── POST /api/billing/checkout ────────────────────────────────────────

    [HttpPost("checkout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCheckout(
        [FromBody] CheckoutRequest request,
        CancellationToken ct)
    {
        var validPlans = new[] { PlanConstants.Growth, PlanConstants.Operations, PlanConstants.Integration, PlanConstants.Distributor };
        if (!validPlans.Contains(request.Plan, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Invalid plan '{request.Plan}'. Valid: growth, operations, integration, distributor." });

        var frontendUrl = GetPrimaryFrontendUrl();
        var billingInterval = NormalizeBillingInterval(request.BillingInterval);
        try
        {
            var url = await _billing.CreateCheckoutSessionAsync(
                _tenant.OrganisationId,
                request.Plan.ToLowerInvariant(),
                frontendUrl,
                billingInterval,
                ct);
            return Ok(new { url });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── POST /api/billing/portal ──────────────────────────────────────────

    [HttpPost("portal")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePortal(CancellationToken ct)
    {
        var returnUrl = $"{GetPrimaryFrontendUrl()}/settings";
        try
        {
            var url = await _billing.CreatePortalSessionAsync(_tenant.OrganisationId, returnUrl, ct);
            return Ok(new { url });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── POST /api/billing/pilot/request-extension ─────────────────────────

    [HttpPost("pilot/request-extension")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestExtension(CancellationToken ct)
    {
        await _billing.RequestPilotExtensionAsync(_tenant.OrganisationId, ct);
        return Ok(new { message = "Extension request received. Our team will be in touch within 1 business day." });
    }

    // ── GET /api/billing/ai-usage ─────────────────────────────────────────

    /// <summary>
    /// Returns the current calendar month's OpenAI token usage for the caller's
    /// organisation, plus the org's RESOLVED monthly cap (the global config
    /// override <c>Ai:OpenAI:MonthlyTokenLimitPerOrg</c> when set, otherwise the
    /// org's plan default from <c>PlanConstants.GetAiMonthlyTokenLimit</c>).
    /// Used by the settings UI and by support to diagnose AI no-op behaviour.
    /// </summary>
    [HttpGet("ai-usage")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAiUsage(CancellationToken ct)
    {
        var snapshot = await _aiUsage.GetCurrentAsync(_tenant.OrganisationId, ct);
        return Ok(new
        {
            tokensUsed  = snapshot.TokensUsed,
            tokensLimit = snapshot.TokensLimit,
            year        = snapshot.Year,
            month       = snapshot.Month,
        });
    }

    // ── POST /api/billing/webhook ─────────────────────────────────────────

    [HttpPost("webhook")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync(ct);
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        var secret    = _config["Stripe:WebhookSecret"] ?? string.Empty;

        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogWarning("Stripe webhook rejected: missing Stripe-Signature header.");
            return BadRequest(new { error = "Missing signature." });
        }

        Stripe.Event stripeEvent;
        try
        {
            // throwOnApiVersionMismatch:false — decouple webhook ingest from the exact
            // Stripe API version the dashboard endpoint emits. Stripe.net pins the SDK to
            // a specific API version (51.1.0 → 2026-04-22.dahlia); the default (true) makes
            // ConstructEvent THROW when a delivered event's api_version differs, which the
            // catch below turns into a 400 → Stripe retries forever → plans never unlock
            // (silent billing failure) if the endpoint/account version ever drifts off the
            // SDK pin. The version string check is a separate step DOWNSTREAM of HMAC +
            // timestamp signature verification, so disabling it does NOT weaken signature
            // security — forged/mistimed events are still rejected (400). The fields we read
            // (session/subscription/invoice primitives + our own metadata) are stable across
            // dahlia versions; the one structurally-sensitive field
            // (invoice.Parent.SubscriptionDetails.SubscriptionId) is already null-guarded.
            stripeEvent = Stripe.EventUtility.ConstructEvent(
                json, signature, secret, throwOnApiVersionMismatch: false);
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogWarning("Stripe webhook signature validation failed: {Msg}", ex.Message);
            return BadRequest(new { error = "Invalid signature." });
        }

        // We no longer 400 on an api_version mismatch (see above), but a drift off the
        // SDK pin still signals a real misconfiguration (the dashboard endpoint or the
        // account-default API version moved away from what this build deserializes
        // against). Surface it as an actionable warning instead of swallowing it, so a
        // version drift is visible BEFORE some future nested-field shape change silently
        // breaks a handler. Events keep flowing either way.
        if (!string.Equals(stripeEvent.ApiVersion, Stripe.StripeConfiguration.ApiVersion,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Stripe event {EventId} ({Type}) api_version {EventVersion} differs from the SDK " +
                "pin {SdkVersion} — the webhook endpoint/account API version has drifted; verify " +
                "handler field compatibility and realign the dashboard endpoint version.",
                stripeEvent.Id, stripeEvent.Type, stripeEvent.ApiVersion,
                Stripe.StripeConfiguration.ApiVersion);
        }

        try
        {
            await HandleStripeEventAsync(stripeEvent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing Stripe event {EventId} ({Type})",
                stripeEvent.Id, stripeEvent.Type);
            return StatusCode(500);
        }

        return Ok();
    }

    // ── Webhook event dispatcher ──────────────────────────────────────────

    private async Task HandleStripeEventAsync(Stripe.Event e, CancellationToken ct)
    {
        switch (e.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutCompletedAsync(e.Data.Object as Stripe.Checkout.Session, ct);
                break;

            case "customer.subscription.updated":
                // e.Created (Stripe's clock) is the event-ordering key: a stale updated event
                // delivered out of order must not overwrite fresher state — see the handler.
                await HandleSubscriptionUpdatedAsync(e.Data.Object as Stripe.Subscription, e.Created, ct);
                break;

            case "customer.subscription.deleted":
                await HandleSubscriptionDeletedAsync(e.Data.Object as Stripe.Subscription, ct);
                break;

            case "invoice.created":
                // Period boundary: attach the just-closed period's order overage as
                // an invoice item BEFORE the invoice finalises. Idempotent per invoice.
                await HandleInvoiceCreatedAsync(e.Data.Object as Stripe.Invoice, ct);
                break;

            default:
                _logger.LogDebug("Ignored Stripe event {Type}", e.Type);
                break;
        }
    }

    internal async Task HandleCheckoutCompletedAsync(Stripe.Checkout.Session? session, CancellationToken ct)
    {
        if (session is null) return;

        session.Metadata.TryGetValue("org_id", out var orgIdStr);
        session.Metadata.TryGetValue("plan", out var plan);

        if (!Guid.TryParse(orgIdStr, out var orgId) || string.IsNullOrEmpty(plan))
        {
            _logger.LogWarning("checkout.session.completed: missing/invalid metadata (org_id={OrgId}, plan={Plan}) on session {SessionId}", orgIdStr, plan, session.Id);
            return;
        }

        var org = await _db.Organisations.FindAsync(new object[] { orgId }, ct);
        if (org is null)
        {
            _logger.LogWarning("checkout.session.completed: org {OrgId} not found — upgrade lost for session {SessionId}", orgId, session.Id);
            return;
        }

        var subscriptionStatus = string.IsNullOrWhiteSpace(session.SubscriptionId)
            ? null
            : await GetSubscriptionStatusAsync(session.SubscriptionId, ct);
        var priceId = string.IsNullOrWhiteSpace(session.SubscriptionId)
            ? null
            : await GetSubscriptionPriceIdAsync(session.SubscriptionId, ct);
        var mappedPlan = StripeBillingMapping.MapPriceIdToPlan(_config, priceId) ?? plan;

        var fromPlan = org.Plan; // capture before mutation for analytics
        org.Plan = mappedPlan;
        org.AccountStatus = subscriptionStatus == "trialing"
            ? AccountStatusConstants.Trialing
            : AccountStatusConstants.Active;
        org.StripeCustomerId = session.CustomerId;
        org.StripeSubscriptionId = session.SubscriptionId;
        org.StripePriceId = priceId;
        org.StripeSubscriptionStatus = subscriptionStatus;
        org.BillingEmail = session.CustomerDetails?.Email;
        org.BillingUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Org {OrgId} upgraded to {Plan} via Stripe checkout {SessionId}",
            orgId, mappedPlan, session.Id);

        await _billing.EmitBillingUpgradedAsync(
            orgId:           orgId,
            fromPlan:        fromPlan ?? PlanConstants.Pilot,
            toPlan:          mappedPlan,
            stripeSessionId: session.Id,
            ct:              ct);
    }

    internal async Task HandleSubscriptionUpdatedAsync(Stripe.Subscription? sub, DateTime eventCreatedUtc, CancellationToken ct)
    {
        if (sub is null) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == sub.CustomerId, ct);
        if (org is null) return;

        // ── Event-ordering guard ─────────────────────────────────────────────
        // Stripe does not guarantee webhook delivery order. Ignore any updated event whose
        // Stripe `created` is STRICTLY older than the last-applied one — otherwise a stale
        // past_due delivered after a newer active would overwrite Active with read-only
        // PastDue, freezing a paying, now-current customer until the daily reconcile heals
        // it (~24h). Equal timestamps (a redelivery of the SAME event) fall through and
        // re-apply idempotently. Compared in Stripe's clock (created vs created), so this is
        // orthogonal to signature-based webhook idempotency, which stays intact.
        if (org.LastStripeEventAt is DateTime lastApplied && eventCreatedUtc < lastApplied)
        {
            _logger.LogInformation(
                "Org {OrgId} subscription {SubId} update IGNORED: event created {EventCreated:o} predates last-applied " +
                "{LastApplied:o} (out-of-order Stripe delivery) — fresher state preserved.",
                org.Id, sub.Id, eventCreatedUtc, lastApplied);
            return;
        }

        var priceId = sub.Items.Data.FirstOrDefault()?.Price?.Id;
        var mappedPlan = StripeBillingMapping.MapPriceIdToPlan(_config, priceId);

        var fromPlan = org.Plan; // capture before mutation for analytics
        if (!string.IsNullOrEmpty(mappedPlan))
            org.Plan = mappedPlan;

        org.StripeSubscriptionId = sub.Id;
        org.StripePriceId = priceId;
        org.StripeSubscriptionStatus = sub.Status;
        org.AccountStatus = StripeBillingMapping.MapStatusToAccountStatus(sub.Status, org.AccountStatus);
        org.LastStripeEventAt = eventCreatedUtc; // advance the ordering watermark to this applied event
        org.BillingUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Org {OrgId} subscription {SubId} updated: status={Status}, plan={Plan}",
            org.Id, sub.Id, sub.Status, org.Plan);

        if (!string.IsNullOrEmpty(mappedPlan) && IsPlanDowngrade(fromPlan, mappedPlan))
        {
            await _billing.EmitBillingDowngradedAsync(
                orgId:    org.Id,
                fromPlan: fromPlan ?? PlanConstants.Pilot,
                toPlan:   mappedPlan,
                ct:       ct);
        }
    }

    internal async Task HandleSubscriptionDeletedAsync(Stripe.Subscription? sub, CancellationToken ct)
    {
        if (sub is null) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == sub.CustomerId, ct);
        if (org is null) return;

        if (org.Plan == PlanConstants.Pilot && org.StripeSubscriptionId is null) return;

        var previousPlan = org.Plan; // capture before mutation for analytics
        org.Plan = PlanConstants.Pilot;
        org.AccountStatus = AccountStatusConstants.ReadOnly;
        org.StripeSubscriptionId = null;
        org.StripeSubscriptionStatus = "canceled";
        // Clear any reconcile-404 grace marker in lock-step with nulling the subscription id
        // (matches the reconciliation DowngradeAsync path). Leaving it set would create a
        // "phantom" org — no subscription id yet a stale past-grace flag — that the mass-
        // downgrade circuit breaker's numerator would keep counting every run.
        org.StripeReconciliationMissingSince = null;
        org.BillingUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Org {OrgId} subscription cancelled — reverted to frozen Pilot.", org.Id);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var hadOrders = await _db.PurchaseOrders
            .AnyAsync(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= monthStart, ct);

        await _billing.EmitBillingCancelledAsync(
            orgId:              org.Id,
            previousPlan:       previousPlan,
            hadOrdersThisMonth: hadOrders,
            ct:                 ct);
    }

    /// <summary>
    /// On <c>invoice.created</c> (the period boundary for a subscription), bill the
    /// just-closed period's per-order overage as a Stripe invoice item, keyed on the
    /// BILLING PERIOD (not the invoice id) so a webhook replay — or a voided/re-issued
    /// invoice with a NEW id covering the same period — never double-bills. Only
    /// subscription invoices for an org we recognise are processed; all others are
    /// ignored. Never throws on the unconfigured/no-customer path and never blocks
    /// anything.
    /// </summary>
    internal async Task HandleInvoiceCreatedAsync(Stripe.Invoice? invoice, CancellationToken ct)
    {
        if (invoice is null) return;

        // Only meter subscription invoices (the recurring period close). One-off
        // (manual founder) invoices carry no subscription and must be skipped.
        // In Stripe.net 51 the subscription id moved under Parent.SubscriptionDetails.
        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (string.IsNullOrWhiteSpace(subscriptionId)) return;
        if (string.IsNullOrWhiteSpace(invoice.Id)) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == invoice.CustomerId, ct);
        if (org is null) return;

        // Window of the period that just closed. Fall back to the current calendar
        // month if Stripe didn't populate the period fields.
        DateTime periodStart = invoice.PeriodStart == default
            ? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            : DateTime.SpecifyKind(invoice.PeriodStart, DateTimeKind.Utc);
        DateTime periodEnd = invoice.PeriodEnd == default
            ? DateTime.UtcNow
            : DateTime.SpecifyKind(invoice.PeriodEnd, DateTimeKind.Utc);

        // ── Yearly-subscription correctness: monthly windows, not the raw period ──
        // The order quota is a MONTHLY allowance on every paid plan regardless of the
        // payment interval. A monthly subscription's invoice period (~1 month) passes
        // through unchanged — single window keyed on periodStart, byte-identical to
        // the pre-yearly behaviour. A YEARLY subscription's renewal invoice spans ~12
        // months; counting the whole year's orders against the MONTHLY cap would
        // grotesquely over-bill, so multi-month periods are decomposed into
        // calendar-month windows and each month is computed + billed separately, each
        // with its own period-based idempotency key.
        var windows = SplitBillingPeriodIntoMonthlyWindows(periodStart, periodEnd);

        // ── Attach overage items to THIS invoice when it is still a draft ────
        // invoice.created fires ~1h before auto-finalisation, so the invoice is
        // normally a draft and the overage items can be pinned onto the very
        // invoice that closes the period. Items created on the customer alone
        // sweep into the NEXT invoice — on a yearly subscription that is ~a year
        // of float, and after a cancellation the next invoice never comes, so the
        // item is stranded. Non-draft statuses (e.g. a replayed event processed
        // after finalisation would 400 on attach) fall back to the historical
        // customer-sweep behaviour with a warning. Idempotency keys are unchanged
        // in both paths.
        var attachToInvoiceId = invoice.Status == "draft" ? invoice.Id : null;
        if (attachToInvoiceId is null)
            _logger.LogWarning(
                "invoice.created for org {OrgId}: invoice {InvoiceId} status '{Status}' is not draft — " +
                "overage items (if any) will be created on the customer and sweep into the next invoice.",
                org.Id, invoice.Id, invoice.Status);

        foreach (var (windowStart, windowEnd) in windows)
        {
            // Delivered-only meter (Billing:CountDeliveredOnly, default OFF): the flag and
            // its status filter live INSIDE ComputePeriodOverageOrdersAsync, so each monthly
            // window here automatically counts only orders that reached the supplier when
            // the flag is ON (creation-month attribution, evaluated at invoice time —
            // late deliveries after a period's invoice are never caught up). The
            // period-based idempotency keys below are intentionally UNCHANGED.
            var overageOrders = await _billing.ComputePeriodOverageOrdersAsync(
                org.Id, windowStart, windowEnd, ct);
            if (overageOrders <= 0) continue;

            // Idempotent: keyed on the BILLING PERIOD ("{orgId}:{periodStart:O}"), NOT the
            // invoice id. The same period maps to the same key, so the unique
            // (org_id, billing_key) row blocks a second charge even if Stripe emits a new
            // invoice id for the same/overlapping period (e.g. a voided + re-issued draft).
            var billingKey = BuildPeriodBillingKey(org.Id, windowStart);
            // Draft invoice → attach overload (items land on THIS invoice);
            // otherwise the historical customer-sweep overload, byte-identical.
            var result = attachToInvoiceId is not null
                ? await _billing.BillOverageForInvoiceAsync(
                    org.Id, billingKey, overageOrders, attachToInvoiceId, ct)
                : await _billing.BillOverageForInvoiceAsync(
                    org.Id, billingKey, overageOrders, ct);

            _logger.LogInformation(
                "invoice.created overage for org {OrgId}: {Orders} orders, period={Period}, alreadyBilled={Already}, item={Item}",
                org.Id, result.OverageOrders, billingKey, result.AlreadyBilled, result.StripeItemId);
        }
    }

    /// <summary>
    /// Splits an invoice billing period into per-calendar-month windows for overage
    /// metering. A period no longer than a single month (≤ 32 days — every monthly
    /// Stripe anchor period) is returned UNCHANGED as one window, keeping the monthly
    /// billing path byte-identical. Longer periods (yearly/quarterly subscriptions)
    /// are cut on UTC calendar-month boundaries: the first/last windows may be partial
    /// (anchor day → month end, month start → anchor day); each full month in between
    /// is its own window. Each window's start feeds <see cref="BuildPeriodBillingKey"/>,
    /// so every month gets its own idempotency ledger row and a webhook replay (or a
    /// re-issued invoice for the same period) can never double-bill any month.
    /// </summary>
    internal static IReadOnlyList<(DateTime Start, DateTime End)> SplitBillingPeriodIntoMonthlyWindows(
        DateTime periodStart,
        DateTime periodEnd)
    {
        // Single-month periods (all monthly subscriptions: 28–31 days, plus a safety
        // margin) pass through untouched — the monthly path stays byte-identical.
        if (periodEnd <= periodStart || (periodEnd - periodStart).TotalDays <= 32)
            return new[] { (periodStart, periodEnd) };

        var windows = new List<(DateTime Start, DateTime End)>();
        var cursor = periodStart;
        while (cursor < periodEnd)
        {
            var nextMonthBoundary = new DateTime(cursor.Year, cursor.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMonths(1);
            var windowEnd = nextMonthBoundary < periodEnd ? nextMonthBoundary : periodEnd;
            windows.Add((cursor, windowEnd));
            cursor = windowEnd;
        }
        return windows;
    }

    /// <summary>
    /// Period-based idempotency key for order overage: <c>{orgId}:{periodStart:O}</c>.
    /// Deriving the key from the billing period (rather than the Stripe invoice id)
    /// means a re-issued/recreated invoice for the same period maps to the same key,
    /// so the unique (org_id, billing_key) ledger row prevents a double-bill.
    /// </summary>
    internal static string BuildPeriodBillingKey(Guid orgId, DateTime periodStart) =>
        $"{orgId}:{DateTime.SpecifyKind(periodStart, DateTimeKind.Utc):O}";

    private async Task<string?> GetSubscriptionPriceIdAsync(string subscriptionId, CancellationToken ct)
    {
        var service = new Stripe.SubscriptionService();
        var subscription = await service.GetAsync(subscriptionId, cancellationToken: ct);
        return subscription.Items.Data.FirstOrDefault()?.Price?.Id;
    }

    private async Task<string?> GetSubscriptionStatusAsync(string subscriptionId, CancellationToken ct)
    {
        var service = new Stripe.SubscriptionService();
        var subscription = await service.GetAsync(subscriptionId, cancellationToken: ct);
        return subscription.Status;
    }

    private static string NormalizeBillingInterval(string? billingInterval)
    {
        var value = (billingInterval ?? "monthly").Trim().ToLowerInvariant();
        return value == "yearly" ? "yearly" : "monthly";
    }

    private string GetPrimaryFrontendUrl()
    {
        var configured = _config["Frontend:Url"] ?? "http://localhost:8082";
        return configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?.TrimEnd('/')
            ?? "http://localhost:8082";
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public record CheckoutRequest(string Plan, string? BillingInterval = null);
