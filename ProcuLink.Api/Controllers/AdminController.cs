using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
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
    /// <summary>
    /// Upper bound for the admin Pilot trial extension (10 years). Beyond this,
    /// <see cref="DateTime.AddDays"/> risks overflow which — absent a global
    /// exception handler — would surface as a 500. A real extension is days/weeks.
    /// </summary>
    private const int MaxExtendTrialDays = 3650;

    private readonly ProcuLinkDbContext       _db;
    private readonly IBillingService          _billing;
    private readonly IConfiguration           _config;
    private readonly ILogger<AdminController>  _logger;
    private readonly IDataErasureService      _erasure;
    private readonly IItemMappingService      _itemMappings;

    public AdminController(
        ProcuLinkDbContext       db,
        IBillingService          billing,
        IConfiguration           config,
        ILogger<AdminController>  logger,
        IDataErasureService      erasure,
        IItemMappingService      itemMappings)
    {
        _db           = db;
        _billing      = billing;
        _config       = config;
        _logger       = logger;
        _erasure      = erasure;
        _itemMappings = itemMappings;
    }

    // In production _billing always resolves to StripeBillingService. The cast
    // exposes the admin-only methods (Stripe MRR + invoice) without widening the
    // IBillingService interface — same pattern as BillingController.BillingEvents.
    private StripeBillingService? Stripe => _billing as StripeBillingService;

    // ── GET /api/admin/access ─────────────────────────────────────────────
    // Lightweight probe so the frontend can hide the Admin nav link for
    // non-admins. The class-level [AdminOnly] gate already returns 403 for
    // anyone not on the server-side allowlist, so reaching this action means
    // "you are an admin" → 204. There is NO body: the allowlist is never
    // serialized to the browser, so the link being shown/hidden leaks nothing,
    // and defense-in-depth is unchanged (every other admin endpoint re-gates).
    [HttpGet("access")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetAccess() => NoContent();

    // ── GET /api/admin/job-failures ───────────────────────────────────────
    /// <summary>
    /// Recent Hangfire job failures (method, exception, reason, failed-at) so the platform owner can
    /// diagnose a stuck/failing worker without opening the Hangfire Postgres. Cross-tenant by nature
    /// (Hangfire jobs aren't org-partitioned), which is why this lives on the [AdminOnly] surface, not
    /// the org-scoped /api/ops. Defensive: if the job store is unavailable it returns an empty list,
    /// never a 500.
    /// </summary>
    [HttpGet("job-failures")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetJobFailures([FromQuery] int count = 50)
    {
        count = Math.Clamp(count, 1, 200);
        try
        {
            var api = Hangfire.JobStorage.Current.GetMonitoringApi();
            var rows = api.FailedJobs(0, count).Select(kv => new AdminJobFailureDto(
                Id:               kv.Key,
                Job:              kv.Value.Job is { } j ? $"{j.Type?.Name}.{j.Method?.Name}" : "(unknown)",
                ExceptionType:    kv.Value.ExceptionType,
                ExceptionMessage: Truncate(kv.Value.ExceptionMessage, 600),
                Reason:           kv.Value.Reason,
                FailedAt:         kv.Value.FailedAt)).ToList();
            return Ok(new { totalFailed = api.FailedCount(), shown = rows.Count, failures = rows });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin job-failures: Hangfire monitoring API unavailable.");
            return Ok(new { totalFailed = 0, shown = 0, failures = Array.Empty<AdminJobFailureDto>() });
        }
    }

    private static string? Truncate(string? s, int max) =>
        s is { Length: > 0 } && s.Length > max ? s[..max] + "…" : s;

    // ── GET /api/admin/item-mapping-twins ─────────────────────────────────
    /// <summary>
    /// Learned item mappings whose buyer codes differ only in CASE — rows written before every
    /// write path shared one case rule (<c>ItemCodeComparison</c>).
    ///
    /// <para><b>Read-only, and deliberately so.</b> It reports what production already holds and
    /// does not merge or delete anything. Which spelling survives a merge is a decision about a
    /// customer's item codes; it needs the founder, not a background job. Nothing is broken while
    /// the decision is pending: since WP-14 the resolver picks between twins deterministically
    /// (exact-case wins, then most recently updated, then id), and every write path now refuses to
    /// create a new one — so this list can only shrink.</para>
    ///
    /// <para>Grouped by <c>(org_id, supplier_id, lower(buyer_item_code))</c> having count &gt; 1.
    /// Cross-org like the rest of this controller.</para>
    /// </summary>
    [HttpGet("item-mapping-twins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetItemMappingTwins(CancellationToken ct)
    {
        var orgIds = await _db.Organisations
            .AsNoTracking()
            .Select(o => o.Id)
            .ToListAsync(ct);

        var rows = new List<object>();
        foreach (var orgId in orgIds)
        {
            foreach (var twin in await _itemMappings.FindCaseVariantTwinsAsync(orgId, ct))
            {
                rows.Add(new
                {
                    organisationId = orgId,
                    supplierId     = twin.SupplierId,
                    foldedCode     = twin.FoldedCode,
                    rowCount       = twin.RowCount,
                    spellings      = twin.Spellings,
                });
            }
        }

        return Ok(new
        {
            totalGroups = rows.Count,
            note = "Read-only. Merging or deleting a twin changes a customer's item codes and is a "
                 + "founder decision; nothing here does it for you.",
            groups = rows,
        });
    }

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

            // Durable accountability trail (GDPR Art.5(2)) — creating a real-money
            // Stripe invoice is a privileged billing action. Record ids + total only,
            // never any Stripe key/secret.
            await WriteAdminAuditAsync(request.OrganisationId, request.OrganisationId, "Organisation",
                "admin.invoice.created",
                new
                {
                    invoiceId = result.InvoiceId,
                    status = result.Status,
                    currency = request.Currency,
                    totalCents = request.LineItems.Sum(li => (long)li.AmountCents * Math.Max(li.Quantity, 1)),
                },
                ct);

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

    // ── POST /api/admin/organisations/{id}/limits ─────────────────────────
    /// <summary>
    /// Sets per-org admin overrides for the order limit, supplier limit, and/or
    /// Pilot trial end so the founder can grant a prospect extra headroom or a
    /// beefier/extended Pilot without changing their plan. Cross-tenant by design
    /// (the org is targeted by route id) and gated by <see cref="AdminOnlyAttribute"/>
    /// at the controller level — a non-admin gets 403, never reaching this method.
    /// Limits must be non-negative when set. Returns the updated org plus the
    /// now-effective limits/trial-end.
    /// </summary>
    [HttpPost("organisations/{id:guid}/limits")]
    [ProducesResponseType(typeof(OrgLimitsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetOrganisationLimits(
        Guid id,
        [FromBody] SetOrgLimitsRequest request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });
        if (request.OrderLimitOverride is < 0)
            return BadRequest(new { error = "orderLimitOverride must be non-negative." });
        if (request.SupplierLimitOverride is < 0)
            return BadRequest(new { error = "supplierLimitOverride must be non-negative." });
        if (request.ExtendTrialDays is < 0)
            return BadRequest(new { error = "extendTrialDays must be non-negative." });
        // Upper bound: an absurd value would overflow DateTime.UtcNow.AddDays and,
        // with no global exception handler, surface as a 500. Cap at 10 years and
        // return a clean 400 instead. (10 years is far beyond any real Pilot extension.)
        if (request.ExtendTrialDays is > MaxExtendTrialDays)
            return BadRequest(new { error = $"extendTrialDays must not exceed {MaxExtendTrialDays} (10 years)." });

        // Cross-tenant by design — the org is targeted by route id (admin surface).
        var org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org is null)
            return NotFound(new { error = $"Organisation {id} not found." });

        // Order limit override: clear-flag wins, then a present value sets it.
        if (request.ClearOrderLimit)
            org.OrderLimitOverride = null;
        else if (request.OrderLimitOverride is { } ol)
            org.OrderLimitOverride = ol;

        if (request.ClearSupplierLimit)
            org.SupplierLimitOverride = null;
        else if (request.SupplierLimitOverride is { } sl)
            org.SupplierLimitOverride = sl;

        // Trial end: clear-flag wins; then extend-days (relative to now) takes
        // precedence over an absolute date if both are supplied.
        if (request.ClearTrialEnds)
            org.TrialEndsAtOverride = null;
        else if (request.ExtendTrialDays is { } days)
            org.TrialEndsAtOverride = DateTime.UtcNow.AddDays(days);
        else if (request.TrialEndsAtOverride is { } endsAt)
            org.TrialEndsAtOverride = DateTime.SpecifyKind(endsAt, DateTimeKind.Utc);

        await _db.SaveChangesAsync(ct);

        var plan = PlanConstants.All.Contains(org.Plan) ? org.Plan : PlanConstants.Pilot;
        var effectiveOrderLimit = PlanConstants.GetEffectiveOrderLimit(plan, org.OrderLimitOverride);
        var effectiveSupplierLimit = PlanConstants.GetEffectiveSupplierLimit(plan, org.SupplierLimitOverride);
        var effectiveTrialEnd = org.TrialEndsAtOverride
            ?? org.TrialEndsAt
            ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);

        _logger.LogInformation(
            "Admin set limits for org {OrgId}: orderOverride={OrderOverride}, supplierOverride={SupplierOverride}, trialEndOverride={TrialEnd}",
            org.Id, org.OrderLimitOverride, org.SupplierLimitOverride, org.TrialEndsAtOverride);
        // Durable accountability trail (GDPR Art.5(2)) — plan/limit/trial overrides are
        // privileged changes to a tenant's entitlements; capture who set what.
        await WriteAdminAuditAsync(org.Id, org.Id, "Organisation", "admin.org.limits_changed",
            new
            {
                orderLimitOverride = org.OrderLimitOverride,
                supplierLimitOverride = org.SupplierLimitOverride,
                trialEndsAtOverride = org.TrialEndsAtOverride,
            },
            ct);

        return Ok(new OrgLimitsResponse(
            Id:                     org.Id,
            Name:                   org.Name,
            Plan:                   org.Plan,
            AccountStatus:          org.AccountStatus,
            OrderLimitOverride:     org.OrderLimitOverride,
            SupplierLimitOverride:  org.SupplierLimitOverride,
            TrialEndsAtOverride:    org.TrialEndsAtOverride,
            EffectiveOrderLimit:    effectiveOrderLimit,
            EffectiveSupplierLimit: effectiveSupplierLimit,
            EffectiveTrialEndsAt:   effectiveTrialEnd));
    }

    // ── POST /api/admin/organisations/{id}/account-status ─────────────────
    /// <summary>
    /// Manual account-status transition: the product surface for un-freezing an organisation
    /// whose Stripe subscription was cancelled. Before this existed, an org left at
    /// <c>read_only</c> by a cancel could only be recovered with a raw production UPDATE.
    /// Cross-tenant by design (org targeted by route id) and gated by the class-level
    /// <see cref="AdminOnlyAttribute"/>, same as <see cref="SetOrganisationLimits"/>.
    ///
    /// <para><b>Exactly one transition is permitted: <c>read_only</c> → <c>trialing</c></b>, and
    /// only for a Pilot-plan org with no live Stripe subscription. Every other account status is
    /// OWNED by another writer, and a manual write would be silently re-derived:</para>
    /// <list type="bullet">
    /// <item><c>active</c> / <c>past_due</c> / <c>cancelled</c> come from Stripe, via the billing
    /// webhook and <see cref="StripeSubscriptionReconciliationService"/> (both through
    /// <see cref="StripeBillingMapping.MapStatusToAccountStatus"/>). Setting <c>active</c> by hand
    /// either gets overwritten on the next reconcile or grants paid entitlements with no
    /// subscription behind them.</item>
    /// <item><c>trial_expired</c> ⇄ <c>trialing</c> is owned by the Pilot trial-window arbiter,
    /// <c>IBillingService.MarkPilotExpiredIfNeededAsync</c>, which ALREADY reactivates an expired
    /// Pilot on its own once an admin extends the window via <c>.../limits</c>.</item>
    /// <item>A manual freeze (<c>→ read_only</c>) has no proven operational need; cancel in Stripe
    /// instead, so the billing record and the account status stay in agreement.</item>
    /// </list>
    ///
    /// <para><b>Why the no-live-subscription precondition:</b> the reconciliation sweep skips an
    /// org whose <c>StripeSubscriptionId</c> is blank (<c>ReconcileOrgAsync</c>'s early return), so
    /// for a cancelled org this write survives. With a subscription id present the sweep re-derives
    /// the status from Stripe, so we refuse rather than write something that quietly reverts —
    /// a paused subscription is un-paused in Stripe, not papered over here.</para>
    ///
    /// <para><b>Why this cannot lie:</b> the endpoint owns no copy of the trial-expiry rule. After
    /// writing <c>trialing</c> it calls the canonical arbiter, which immediately re-expires the org
    /// if its Pilot window has elapsed or its order cap is spent. The response reports the
    /// EFFECTIVE status, flags <c>revertedByTrialWindow</c>, and names the endpoint that actually
    /// fixes it. So the returned status is always the one the database holds.</para>
    /// </summary>
    [HttpPost("organisations/{id:guid}/account-status")]
    [ProducesResponseType(typeof(OrgAccountStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetOrganisationAccountStatus(
        Guid id,
        [FromBody] SetOrgAccountStatusRequest request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.AccountStatus))
            return BadRequest(new { error = "accountStatus is required." });

        // account_status is persisted lowercase — normalise before comparing or writing.
        var requested = request.AccountStatus.Trim().ToLowerInvariant();

        // Cross-tenant by design — the org is targeted by route id (admin surface).
        var org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org is null)
            return NotFound(new { error = $"Organisation {id} not found." });

        if (requested != AccountStatusConstants.Trialing)
            return BadRequest(new
            {
                error = $"'{request.AccountStatus}' cannot be set by hand. The only manual transition is "
                      + "'read_only' to 'trialing' (un-freezing an organisation whose subscription was "
                      + "cancelled). Every other account status is derived from Stripe or from the Pilot "
                      + "trial window, and a manual value would be overwritten by the next reconcile.",
            });

        if (!string.Equals(org.AccountStatus, AccountStatusConstants.ReadOnly, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new
            {
                error = $"This organisation is '{org.AccountStatus}', and only a 'read_only' organisation "
                      + "can be moved to 'trialing'. An expired trial does not need this endpoint: extend "
                      + $"the trial with POST /api/admin/organisations/{org.Id}/limits and it reactivates "
                      + "on its own.",
            });

        if (!string.IsNullOrWhiteSpace(org.StripeSubscriptionId))
            return BadRequest(new
            {
                error = "This organisation still has a live Stripe subscription, so its account status is "
                      + "maintained from Stripe — anything set here would be overwritten by the next "
                      + "reconciliation. Fix the subscription in Stripe instead (resume it, or let the "
                      + "cancellation complete, which frees this organisation for a manual reset).",
            });

        if (!string.Equals(org.Plan, PlanConstants.Pilot, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new
            {
                error = $"'trialing' is a Pilot state, but this organisation is on the '{org.Plan}' plan. "
                      + "A paid plan's account status comes from its Stripe subscription.",
            });

        var previous = org.AccountStatus;
        org.AccountStatus = AccountStatusConstants.Trialing;
        await _db.SaveChangesAsync(ct);

        // Hand the final say straight back to the canonical trial-window arbiter rather than
        // re-implementing its predicate here (a fourth copy of that rule is exactly how status
        // drift gets shipped). It re-expires the org immediately when the Pilot window has already
        // elapsed or the order cap is spent — and it can only see the org now that it is no longer
        // read_only, which is the early return that made the frozen state unrecoverable.
        await _billing.MarkPilotExpiredIfNeededAsync(org.Id, ct);

        // Read back what the database ACTUALLY holds (no-tracking, so this is the stored value and
        // not the change tracker's opinion) — the response must never claim more than that.
        var refreshed = await _db.Organisations.AsNoTracking().FirstAsync(o => o.Id == org.Id, ct);
        var effective = refreshed.AccountStatus;
        var reverted = !string.Equals(effective, AccountStatusConstants.Trialing, StringComparison.OrdinalIgnoreCase);
        var effectiveTrialEnd = refreshed.TrialEndsAtOverride
            ?? refreshed.TrialEndsAt
            ?? refreshed.TrialStartedAt.Add(PlanConstants.PilotDuration);

        var note = reverted
            ? "The organisation was un-frozen, but its Pilot window has already ended (or its order "
              + $"allowance is used up), so the trial check returned it to '{effective}' straight away. "
              + $"Give it more time or headroom with POST /api/admin/organisations/{org.Id}/limits, then "
              + "run this again."
            : null;

        _logger.LogInformation(
            "Admin set account status for org {OrgId}: {From} -> requested {Requested}, effective {Effective}.",
            org.Id, previous, requested, effective);

        // Durable accountability trail (GDPR Art.5(2)) — who/when/from/to for a privileged change
        // to a tenant's ability to process orders. Records the EFFECTIVE outcome, not the wish.
        await WriteAdminAuditAsync(org.Id, org.Id, "Organisation", "admin.org.account_status_changed",
            new
            {
                from = previous,
                requested,
                to = effective,
                revertedByTrialWindow = reverted,
                plan = refreshed.Plan,
            },
            ct);

        return Ok(new OrgAccountStatusResponse(
            Id:                     refreshed.Id,
            Name:                   refreshed.Name,
            Plan:                   refreshed.Plan,
            PreviousAccountStatus:  previous,
            RequestedAccountStatus: requested,
            AccountStatus:          effective,
            RevertedByTrialWindow:  reverted,
            EffectiveTrialEndsAt:   effectiveTrialEnd,
            Note:                   note));
    }

    // ── POST /api/admin/organisations/{id}/retention ──────────────────────
    /// <summary>
    /// Sets (or clears) the per-org BLOB-retention window. NULL/cleared = retention
    /// DISABLED — the default; nothing is ever deleted for an org that has not opted in.
    /// Setting a value (≥ 1 day) opts the org into the daily blob-retention sweep, which
    /// purges ONLY the R2 file blobs (source files + generated artifacts) of TERMINAL
    /// orders older than the window — DB rows, hashes, provenance and audit trail stay.
    /// A second global latch (<c>Retention:DryRun</c>, default true on the Worker) must
    /// ALSO be flipped before anything is actually deleted. Admin-only by the class
    /// <see cref="AdminOnlyAttribute"/> gate — org admins cannot self-serve this yet.
    /// </summary>
    [HttpPost("organisations/{id:guid}/retention")]
    [ProducesResponseType(typeof(OrgRetentionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetOrganisationRetention(
        Guid id,
        [FromBody] SetOrgRetentionRequest request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });
        if (!request.Clear && request.RetentionDays is null)
            return BadRequest(new { error = "Provide retentionDays (>= 1) to enable retention, or clear=true to disable it." });
        if (!request.Clear && request.RetentionDays is < 1)
            return BadRequest(new { error = "retentionDays must be at least 1. Use clear=true to disable retention." });

        // Cross-tenant by design — the org is targeted by route id (admin surface).
        var org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org is null)
            return NotFound(new { error = $"Organisation {id} not found." });

        org.RetentionDays = request.Clear ? null : request.RetentionDays;
        await _db.SaveChangesAsync(ct);

        // Deliberate operator trail for a destructive-capability toggle.
        _logger.LogWarning(
            "Admin set blob retention for org {OrgId}: retentionDays={RetentionDays} (null = disabled).",
            org.Id, org.RetentionDays);
        // Durable accountability trail (GDPR Art.5(2)) — a retention window enables
        // the destructive blob-purge sweep, so who changed it must be queryable.
        await WriteAdminAuditAsync(org.Id, org.Id, "Organisation", "admin.org.retention_changed",
            new { retentionDays = org.RetentionDays, enabled = org.RetentionDays is not null }, ct);

        return Ok(new OrgRetentionResponse(
            Id:               org.Id,
            Name:             org.Name,
            RetentionDays:    org.RetentionDays,
            RetentionEnabled: org.RetentionDays is not null));
    }

    // ── DELETE /api/admin/organisations/{orgId}/orders/{orderId} ──────────
    // GDPR right-to-erasure: hard-deletes an order's R2 blobs + every order-tied
    // DB row, strictly org-scoped (closes audit d-5). Admin-only (class [AdminOnly]).
    [HttpDelete("organisations/{orgId:guid}/orders/{orderId:guid}")]
    [ProducesResponseType(typeof(OrderErasureResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EraseOrder(Guid orgId, Guid orderId, CancellationToken ct)
    {
        var result = await _erasure.EraseOrderAsync(orgId, orderId, ct);
        if (!result.Found)
            return NotFound(new { error = "Order not found for that organisation (or already erased)." });

        _logger.LogWarning(
            "ADMIN ERASE: order {OrderId} (org {OrgId}) hard-deleted by admin — {@Result}.",
            orderId, orgId, result);
        // Durable accountability trail (GDPR Art.5(2)) — keyed to the erased order.
        await WriteAdminAuditAsync(orgId, orderId, "Order", "admin.order.erased", result, ct);
        return Ok(result);
    }

    // ── POST /api/admin/organisations/{orgId}/orders/bulk-erase ───────────
    /// <summary>
    /// Bulk hard-erase: permanently removes every order in the org that matches the
    /// supplied filter, in ONE server-side batch. Built for cleaning a large test org
    /// without the per-order <see cref="EraseOrder"/> path's ~thousands of rate-limited
    /// HTTP deletes. Filter fields (poNumberPrefix / status / ids / olderThan) combine
    /// with AND. At least one real criterion is REQUIRED — an empty filter is rejected
    /// (400) so it can never mass-wipe the org. Strictly org-scoped (route org id is
    /// ANDed into every match) and admin-only (class <see cref="AdminOnlyAttribute"/>);
    /// it can never erase across orgs. Returns the erased count + summed child counts.
    /// </summary>
    [HttpPost("organisations/{orgId:guid}/orders/bulk-erase")]
    [ProducesResponseType(typeof(BulkOrderErasureResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkEraseOrders(
        Guid orgId,
        [FromBody] BulkEraseFilter filter,
        CancellationToken ct)
    {
        // Require at least one REAL criterion. Blank/whitespace strings and an empty
        // ids list are not criteria — this is the fat-finger guard against {} wiping
        // an entire org. (The service enforces the same invariant defensively.)
        var hasCriterion =
            filter is not null &&
            (!string.IsNullOrWhiteSpace(filter.PoNumberPrefix)
             || !string.IsNullOrWhiteSpace(filter.Status)
             || filter.Ids is { Count: > 0 }
             || filter.OlderThan is not null);
        if (!hasCriterion)
            return BadRequest(new
            {
                error = "At least one filter criterion (poNumberPrefix, status, ids, or olderThan) is required. "
                      + "An empty filter is refused so it cannot mass-erase the organisation.",
            });

        var result = await _erasure.BulkEraseOrdersAsync(orgId, filter!, ct);

        _logger.LogWarning(
            "ADMIN BULK ERASE: org {OrgId} — {Count} orders hard-deleted by admin — {@Result}.",
            orgId, result.OrdersErased, result);
        // Durable accountability trail (GDPR Art.5(2)) — captures the filter + result.
        await WriteAdminAuditAsync(orgId, orgId, "Organisation", "admin.orders.bulk_erased",
            new
            {
                filter = new { filter!.PoNumberPrefix, filter.Status, idCount = filter.Ids?.Count, filter.OlderThan },
                result,
            },
            ct);
        return Ok(result);
    }

    // ── Durable admin audit (GDPR Art.5(2) accountability) ────────────────────

    /// <summary>
    /// Writes a durable, queryable <see cref="AuditEvent"/> for a destructive or
    /// credential/billing admin action so there is a tamper-evident record of WHO
    /// (the platform admin identity) did WHAT to WHICH org/entity — not just an
    /// ephemeral stdout line. The actor's Clerk <c>sub</c> + email are read from the
    /// caller's claims (the same identity the <see cref="AdminOnlyAttribute"/> gate
    /// matched) and stored in the payload; <see cref="AuditEvent.UserId"/> stays NULL
    /// because a platform admin is cross-tenant, not an org <c>AppUser</c> (an FK to a
    /// non-existent org user would fail). NEVER pass secret values in
    /// <paramref name="detail"/> — record that a thing changed, never the plaintext.
    /// Best-effort: an audit-write failure is logged at Error but never fails the
    /// already-completed action (Error, not Warning, because a missing accountability
    /// record is a compliance gap worth surfacing).
    /// </summary>
    private async Task WriteAdminAuditAsync(
        Guid orgId, Guid entityId, string entityType, string action, object detail, CancellationToken ct)
    {
        try
        {
            var sub = User.FindFirst("sub")?.Value;
            var email = new[] { "email", "email_address", ClaimTypes.Email }
                .Select(t => User.FindFirst(t)?.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var payload = new { actor = new { sub, email }, detail };
            _db.AuditEvents.Add(new AuditEvent
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                UserId     = null,
                EntityType = entityType,
                EntityId   = entityId,
                Action     = action,
                Payload    = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
                CreatedAt  = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write durable admin audit {Action} for org {OrgId} (entity {EntityId}). "
                + "The action itself already completed; this is an accountability-log gap.",
                action, orgId, entityId);
        }
    }
}
