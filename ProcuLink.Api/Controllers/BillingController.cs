// ProcuLink.Api/Controllers/BillingController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
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

    public BillingController(
        IBillingService            billing,
        ICurrentTenantService      tenant,
        IConfiguration             config,
        ILogger<BillingController> logger,
        ProcuLinkDbContext         db)
    {
        _billing = billing;
        _tenant  = tenant;
        _config  = config;
        _logger  = logger;
        _db      = db;
    }

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
        if (!validPlans.Contains(request.Plan))
            return BadRequest(new { error = $"Invalid plan '{request.Plan}'. Valid: growth, operations, integration." });

        var returnUrl = $"{_config["Frontend:Url"] ?? "http://localhost:8081"}/settings";
        var url = await _billing.CreateCheckoutSessionAsync(_tenant.OrganisationId, request.Plan, returnUrl, ct);
        return Ok(new { url });
    }

    // ── POST /api/billing/portal ──────────────────────────────────────────

    [HttpPost("portal")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePortal(CancellationToken ct)
    {
        var returnUrl = $"{_config["Frontend:Url"] ?? "http://localhost:8081"}/settings";
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

    // ── POST /api/billing/webhook ─────────────────────────────────────────

    [HttpPost("webhook")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Webhook()
    {
        var json      = await new StreamReader(Request.Body).ReadToEndAsync();
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
            await HandleStripeEventAsync(stripeEvent);
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

    private async Task HandleStripeEventAsync(Stripe.Event e)
    {
        switch (e.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutCompletedAsync(e.Data.Object as Stripe.Checkout.Session);
                break;

            case "customer.subscription.updated":
                await HandleSubscriptionUpdatedAsync(e.Data.Object as Stripe.Subscription);
                break;

            case "customer.subscription.deleted":
                await HandleSubscriptionDeletedAsync(e.Data.Object as Stripe.Subscription);
                break;

            default:
                _logger.LogDebug("Ignored Stripe event {Type}", e.Type);
                break;
        }
    }

    private async Task HandleCheckoutCompletedAsync(Stripe.Checkout.Session? session)
    {
        if (session is null) return;

        session.Metadata.TryGetValue("org_id", out var orgIdStr);
        session.Metadata.TryGetValue("plan", out var plan);

        if (!Guid.TryParse(orgIdStr, out var orgId) || string.IsNullOrEmpty(plan)) return;

        var org = await _db.Organisations.FindAsync(orgId);
        if (org is null) return;

        // Idempotent: skip if already in target state
        if (org.Plan == plan && org.StripeCustomerId == session.CustomerId) return;

        org.Plan                 = plan;
        org.StripeCustomerId     = session.CustomerId;
        org.StripeSubscriptionId = session.SubscriptionId;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Org {OrgId} upgraded to {Plan} via Stripe checkout {SessionId}",
            orgId, plan, session.Id);
    }

    private async Task HandleSubscriptionUpdatedAsync(Stripe.Subscription? sub)
    {
        if (sub is null) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == sub.CustomerId);
        if (org is null) return;

        var status = sub.Status;
        if (status is "trialing" or "active")
        {
            sub.Metadata.TryGetValue("plan", out var plan);
            if (!string.IsNullOrEmpty(plan) && org.Plan != plan)
            {
                org.Plan = plan;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Org {OrgId} plan confirmed as {Plan} (sub status: {Status})",
                    org.Id, plan, status);
            }
        }
        else
        {
            _logger.LogWarning("Subscription {SubId} for org {OrgId} is {Status} — monitoring, not downgrading yet.",
                sub.Id, org.Id, status);
        }
    }

    private async Task HandleSubscriptionDeletedAsync(Stripe.Subscription? sub)
    {
        if (sub is null) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == sub.CustomerId);
        if (org is null) return;

        if (org.Plan == PlanConstants.Pilot && org.StripeSubscriptionId is null) return;

        org.Plan                 = PlanConstants.Pilot;
        org.StripeSubscriptionId = null;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Org {OrgId} subscription cancelled — reverted to frozen Pilot.", org.Id);
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public record CheckoutRequest(string Plan);
