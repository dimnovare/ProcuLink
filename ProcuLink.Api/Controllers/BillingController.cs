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

    // ── Cast helper — lets webhook handlers call EmitBilling* without a Program.cs change ─
    // In production _billing always resolves to StripeBillingService.
    // If it doesn't (e.g. in a test using a mock IBillingService) the call is safely skipped.
    private StripeBillingService? BillingEvents => _billing as StripeBillingService;

    // ── Plan rank — used to detect downgrades in HandleSubscriptionUpdatedAsync ───────────
    private static readonly Dictionary<string, int> PlanRank = new(StringComparer.OrdinalIgnoreCase)
    {
        [PlanConstants.Pilot]       = 0,
        [PlanConstants.Growth]      = 1,
        [PlanConstants.Operations]  = 2,
        [PlanConstants.Integration] = 3,
        [PlanConstants.Enterprise]  = 4,
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
        var validPlans = new[] { PlanConstants.Growth, PlanConstants.Operations, PlanConstants.Integration };
        if (!validPlans.Contains(request.Plan, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Invalid plan '{request.Plan}'. Valid: growth, operations, integration." });

        var frontendUrl = _config["Frontend:Url"] ?? "http://localhost:8082";
        try
        {
            var url = await _billing.CreateCheckoutSessionAsync(_tenant.OrganisationId, request.Plan.ToLowerInvariant(), frontendUrl, ct);
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
        var returnUrl = $"{_config["Frontend:Url"] ?? "http://localhost:8082"}/settings";
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
    /// organisation, plus the configured per-org monthly cap. Used by the
    /// settings UI and by support to diagnose AI no-op behaviour.
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

        Stripe.Event stripeEvent;
        try
        {
            stripeEvent = Stripe.EventUtility.ConstructEvent(json, signature, secret);
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogWarning("Stripe webhook signature validation failed: {Msg}", ex.Message);
            return BadRequest(new { error = "Invalid signature." });
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
                await HandleSubscriptionUpdatedAsync(e.Data.Object as Stripe.Subscription, ct);
                break;

            case "customer.subscription.deleted":
                await HandleSubscriptionDeletedAsync(e.Data.Object as Stripe.Subscription, ct);
                break;

            default:
                _logger.LogDebug("Ignored Stripe event {Type}", e.Type);
                break;
        }
    }

    private async Task HandleCheckoutCompletedAsync(Stripe.Checkout.Session? session, CancellationToken ct)
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
        var mappedPlan = MapPriceIdToPlan(priceId) ?? plan;

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

        await (BillingEvents?.EmitBillingUpgradedAsync(
            orgId:           orgId,
            fromPlan:        fromPlan ?? PlanConstants.Pilot,
            toPlan:          mappedPlan,
            stripeSessionId: session.Id,
            ct:              ct) ?? Task.CompletedTask);
    }

    private async Task HandleSubscriptionUpdatedAsync(Stripe.Subscription? sub, CancellationToken ct)
    {
        if (sub is null) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == sub.CustomerId, ct);
        if (org is null) return;

        var priceId = sub.Items.Data.FirstOrDefault()?.Price?.Id;
        var mappedPlan = MapPriceIdToPlan(priceId);

        var fromPlan = org.Plan; // capture before mutation for analytics
        if (!string.IsNullOrEmpty(mappedPlan))
            org.Plan = mappedPlan;

        org.StripeSubscriptionId = sub.Id;
        org.StripePriceId = priceId;
        org.StripeSubscriptionStatus = sub.Status;
        org.AccountStatus = sub.Status switch
        {
            "trialing" => AccountStatusConstants.Trialing,
            "active" => AccountStatusConstants.Active,
            "past_due" or "unpaid" => AccountStatusConstants.PastDue,
            "canceled" => AccountStatusConstants.ReadOnly,
            _ => org.AccountStatus,
        };
        org.BillingUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Org {OrgId} subscription {SubId} updated: status={Status}, plan={Plan}",
            org.Id, sub.Id, sub.Status, org.Plan);

        if (!string.IsNullOrEmpty(mappedPlan) && IsPlanDowngrade(fromPlan, mappedPlan))
        {
            await (BillingEvents?.EmitBillingDowngradedAsync(
                orgId:    org.Id,
                fromPlan: fromPlan ?? PlanConstants.Pilot,
                toPlan:   mappedPlan,
                ct:       ct) ?? Task.CompletedTask);
        }
    }

    private async Task HandleSubscriptionDeletedAsync(Stripe.Subscription? sub, CancellationToken ct)
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
        org.BillingUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Org {OrgId} subscription cancelled — reverted to frozen Pilot.", org.Id);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var hadOrders = await _db.PurchaseOrders
            .AnyAsync(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= monthStart, ct);

        await (BillingEvents?.EmitBillingCancelledAsync(
            orgId:              org.Id,
            previousPlan:       previousPlan,
            hadOrdersThisMonth: hadOrders,
            ct:                 ct) ?? Task.CompletedTask);
    }

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

    private string? MapPriceIdToPlan(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return null;
        if (priceId == _config["Stripe:GrowthPriceId"]) return PlanConstants.Growth;
        if (priceId == _config["Stripe:OperationsPriceId"]) return PlanConstants.Operations;
        if (priceId == _config["Stripe:IntegrationPriceId"]) return PlanConstants.Integration;
        return null;
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public record CheckoutRequest(string Plan);
