using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Owner/admin surface. This is the ONE deliberately CROSS-TENANT controller —
/// every action queries ALL organisations, NOT a single tenant. Admission is
/// therefore gated by <see cref="AdminOnlyAttribute"/> (the env allowlist
/// <c>Admin:UserIds</c> / <c>Admin:Emails</c>), which fails closed.
///
/// Do NOT add <c>OrganisationId</c> org-scoping here — that is the whole point
/// of this controller — but equally do NOT remove the <c>[AdminOnly]</c> gate.
/// </summary>
[ApiController]
[Route("api/admin")]
[AdminOnly]
public sealed class AdminController : ControllerBase
{
    private readonly ProcuLinkDbContext       _db;
    private readonly IBillingService          _billing;
    private readonly IConfiguration           _config;
    private readonly ILogger<AdminController>  _logger;

    public AdminController(
        ProcuLinkDbContext       db,
        IBillingService          billing,
        IConfiguration           config,
        ILogger<AdminController>  logger)
    {
        _db      = db;
        _billing = billing;
        _config  = config;
        _logger  = logger;
    }

    // In production _billing always resolves to StripeBillingService. The cast
    // exposes the admin-only methods (Stripe MRR + invoice) without widening the
    // IBillingService interface — same pattern as BillingController.BillingEvents.
    private StripeBillingService? Stripe => _billing as StripeBillingService;

    // ── GET /api/admin/overview ───────────────────────────────────────────
    [HttpGet("overview")]
    [ProducesResponseType(typeof(AdminOverviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
    {
        // All orgs, projected to the fields we need (cross-tenant by design).
        var orgs = await _db.Organisations
            .AsNoTracking()
            .Select(o => new { o.Plan, o.AccountStatus, o.CreatedAt })
            .ToListAsync(ct);

        // DB-computed MRR: active PAID orgs × published monthly list price.
        var mrr = orgs
            .Where(o => PlanConstants.IsPaidPlan(o.Plan)
                        && o.AccountStatus == AccountStatusConstants.Active)
            .Sum(o => PlanConstants.GetMonthlyPriceEur(o.Plan));
        var arr = mrr * 12m;

        // Counts by account status.
        var countsByStatus = orgs
            .GroupBy(o => o.AccountStatus)
            .ToDictionary(g => g.Key, g => g.Count());

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var newOrgsThisMonth = orgs.Count(o => o.CreatedAt >= monthStart);

        // Best-effort trial→paid conversion: of orgs that ever started a trial,
        // the share that now sit on an active paid plan. (Approximate: derived
        // from current state, not historical funnel events.)
        var everTrialed = orgs.Count(o =>
            o.Plan == PlanConstants.Pilot
            || PlanConstants.IsPaidPlan(o.Plan)
            || o.AccountStatus is AccountStatusConstants.TrialExpired
                                or AccountStatusConstants.ReadOnly
                                or AccountStatusConstants.Cancelled);
        var convertedToPaid = orgs.Count(o =>
            PlanConstants.IsPaidPlan(o.Plan)
            && o.AccountStatus == AccountStatusConstants.Active);
        var conversion = everTrialed > 0
            ? Math.Round((double)convertedToPaid / everTrialed, 4)
            : 0d;

        // Stripe reconciliation (null + reconciled=false when not configured).
        decimal? stripeMrr = null;
        var reconciled = false;
        try
        {
            stripeMrr = Stripe is null ? null : await Stripe.GetStripeMrrAsync(ct);
            reconciled = stripeMrr is not null;
        }
        catch (Exception ex)
        {
            // Never let a Stripe hiccup break the operational DB view; never log secrets.
            _logger.LogWarning("Stripe MRR reconciliation failed: {Message}", ex.Message);
            stripeMrr = null;
            reconciled = false;
        }

        return Ok(new AdminOverviewDto(
            Mrr:                   mrr,
            Arr:                   arr,
            StripeMrr:             stripeMrr,
            Reconciled:            reconciled,
            CountsByAccountStatus: countsByStatus,
            NewOrgsThisMonth:      newOrgsThisMonth,
            TrialToPaidConversion: conversion));
    }

    // ── GET /api/admin/organisations ──────────────────────────────────────
    [HttpGet("organisations")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminOrganisationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrganisations(CancellationToken ct)
    {
        var since30d = DateTime.UtcNow.AddDays(-30);

        // Per-org order aggregates (cross-tenant): 30-day count + last activity.
        var orderAgg = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => !o.IsSample)
            .GroupBy(o => o.OrgId)
            .Select(g => new
            {
                OrgId        = g.Key,
                Volume30d    = g.Count(o => o.CreatedAt >= since30d),
                LastActivity = (DateTime?)g.Max(o => o.CreatedAt),
            })
            .ToListAsync(ct);
        var orderByOrg = orderAgg.ToDictionary(x => x.OrgId);

        // Active supplier counts per org.
        var supplierAgg = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.DeletedAt == null && !s.IsSample)
            .GroupBy(s => s.OrgId)
            .Select(g => new { OrgId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var suppliersByOrg = supplierAgg.ToDictionary(x => x.OrgId, x => x.Count);

        var orgs = await _db.Organisations
            .AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new
            {
                o.Id, o.Name, o.Slug, o.Plan, o.AccountStatus,
                o.StripeCustomerId, o.StripeSubscriptionId, o.CreatedAt,
            })
            .ToListAsync(ct);

        var result = orgs.Select(o =>
        {
            var mrrContribution =
                PlanConstants.IsPaidPlan(o.Plan) && o.AccountStatus == AccountStatusConstants.Active
                    ? PlanConstants.GetMonthlyPriceEur(o.Plan)
                    : 0m;

            orderByOrg.TryGetValue(o.Id, out var oa);
            suppliersByOrg.TryGetValue(o.Id, out var supCount);

            return new AdminOrganisationDto(
                Id:                   o.Id,
                Name:                 o.Name,
                Slug:                 o.Slug,
                Plan:                 o.Plan,
                AccountStatus:        o.AccountStatus,
                StripeCustomerId:     o.StripeCustomerId,
                StripeSubscriptionId: o.StripeSubscriptionId,
                MrrContribution:      mrrContribution,
                CreatedAt:            o.CreatedAt,
                LastOrderActivity:    oa?.LastActivity,
                OrderVolume30d:       oa?.Volume30d ?? 0,
                SupplierCount:        supCount);
        }).ToList();

        return Ok(result);
    }

    // ── POST /api/admin/invoices ──────────────────────────────────────────
    [HttpPost("invoices")]
    [ProducesResponseType(typeof(CreateInvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateInvoice(
        [FromBody] CreateInvoiceRequest request,
        CancellationToken ct)
    {
        if (request is null || request.OrganisationId == Guid.Empty)
            return BadRequest(new { error = "organisationId is required." });
        if (request.LineItems is null || request.LineItems.Count == 0)
            return BadRequest(new { error = "At least one line item is required." });
        if (request.LineItems.Any(li => li.AmountCents <= 0))
            return BadRequest(new { error = "Each line item amountCents must be greater than zero." });

        if (Stripe is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Billing is not available." });

        var lineItems = request.LineItems
            .Select(li => new InvoiceLineItemInput(li.Description, li.AmountCents, li.Quantity))
            .ToList();

        try
        {
            var result = await Stripe.CreateInvoiceAsync(
                request.OrganisationId, lineItems, request.Currency, ct);

            return Ok(new CreateInvoiceResponse(
                InvoiceId:        result.InvoiceId,
                HostedInvoiceUrl: result.HostedInvoiceUrl,
                Status:           result.Status));
        }
        catch (BillingNotConfiguredException ex)
        {
            // Stripe not configured — clean 503, never a 500, never log the key.
            _logger.LogWarning("Invoice creation skipped: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Stripe is not configured." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
