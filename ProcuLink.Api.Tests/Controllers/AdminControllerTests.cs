using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for the cross-tenant admin overview + organisations + invoice endpoints.
/// MRR math and the Stripe-unconfigured guards are the focus. Authorization is
/// covered separately by <see cref="ProcuLink.Api.Tests.Auth.AdminOnlyAttributeTests"/>.
/// </summary>
public class AdminControllerTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Builds an AdminController whose IBillingService is a real
    /// StripeBillingService (no SecretKey ⇒ Stripe "unconfigured").</summary>
    private static AdminController Build(ProcuLinkDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())  // no Stripe:SecretKey
            .Build();

        var billing = new StripeBillingService(
            db, config, NullLogger<StripeBillingService>.Instance, new FakeAnalyticsService());

        return new AdminController(db, billing, config, NullLogger<AdminController>.Instance, new NoopErasureService(),
            new ProcuLink.Infrastructure.Services.ItemMappingService(db));
    }

    /// <summary>Builds an AdminController with an explicit (recording) erasure service.</summary>
    private static AdminController Build(ProcuLinkDbContext db, ProcuLink.Core.Services.IDataErasureService erasure)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var billing = new StripeBillingService(
            db, config, NullLogger<StripeBillingService>.Instance, new FakeAnalyticsService());
        return new AdminController(db, billing, config, NullLogger<AdminController>.Instance, erasure,
            new ProcuLink.Infrastructure.Services.ItemMappingService(db));
    }

    private sealed class NoopErasureService : ProcuLink.Core.Services.IDataErasureService
    {
        public Task<ProcuLink.Core.Services.OrderErasureResult> EraseOrderAsync(Guid org, Guid orderId, CancellationToken ct)
            => Task.FromResult(new ProcuLink.Core.Services.OrderErasureResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        public Task<ProcuLink.Core.Services.BulkOrderErasureResult> BulkEraseOrdersAsync(
            Guid org, ProcuLink.Core.Services.BulkEraseFilter filter, CancellationToken ct)
            => Task.FromResult(new ProcuLink.Core.Services.BulkOrderErasureResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    /// <summary>Erasure double that reports the order was found + erased (Found=true),
    /// so the controller reaches its durable-audit write.</summary>
    private sealed class FoundErasureService : ProcuLink.Core.Services.IDataErasureService
    {
        public Task<ProcuLink.Core.Services.OrderErasureResult> EraseOrderAsync(Guid org, Guid orderId, CancellationToken ct)
            => Task.FromResult(new ProcuLink.Core.Services.OrderErasureResult(true, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1));

        public Task<ProcuLink.Core.Services.BulkOrderErasureResult> BulkEraseOrdersAsync(
            Guid org, ProcuLink.Core.Services.BulkEraseFilter filter, CancellationToken ct)
            => Task.FromResult(new ProcuLink.Core.Services.BulkOrderErasureResult(3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3));
    }

    /// <summary>Attaches an authenticated admin principal (sub + email claims) so the
    /// controller can capture the actor identity in the audit payload.</summary>
    private static AdminController WithAdmin(AdminController ctrl, string sub, string email)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", sub),
            new Claim("email", email),
        }, authenticationType: "Test");
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return ctrl;
    }

    /// <summary>Captures the org id + filter the controller forwards, returns a canned result.</summary>
    private sealed class RecordingErasureService : ProcuLink.Core.Services.IDataErasureService
    {
        private readonly ProcuLink.Core.Services.BulkOrderErasureResult _result;
        public Guid? LastOrgId { get; private set; }
        public ProcuLink.Core.Services.BulkEraseFilter? LastFilter { get; private set; }
        public RecordingErasureService(ProcuLink.Core.Services.BulkOrderErasureResult result) => _result = result;

        public Task<ProcuLink.Core.Services.OrderErasureResult> EraseOrderAsync(Guid org, Guid orderId, CancellationToken ct)
            => Task.FromResult(new ProcuLink.Core.Services.OrderErasureResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        public Task<ProcuLink.Core.Services.BulkOrderErasureResult> BulkEraseOrdersAsync(
            Guid org, ProcuLink.Core.Services.BulkEraseFilter filter, CancellationToken ct)
        {
            LastOrgId = org;
            LastFilter = filter;
            return Task.FromResult(_result);
        }
    }

    private static Organisation Org(string name, string plan, string status, DateTime? createdAt = null) =>
        new()
        {
            Id             = Guid.NewGuid(),
            ClerkOrgId     = $"org_{Guid.NewGuid():N}",
            Name           = name,
            Slug           = name.ToLowerInvariant().Replace(' ', '-'),
            Plan           = plan,
            AccountStatus  = status,
            CreatedAt      = createdAt ?? DateTime.UtcNow,
            TrialStartedAt = DateTime.UtcNow.AddDays(-10),
        };

    // ── overview MRR math ─────────────────────────────────────────────────

    [Fact]
    public async Task GetOverview_ComputesMrrFromActivePaidOrgs()
    {
        var db = MakeDb();
        db.Organisations.AddRange(
            Org("Growth Active",   PlanConstants.Growth,      AccountStatusConstants.Active),      // 149
            Org("Ops Active",      PlanConstants.Operations,  AccountStatusConstants.Active),      // 399
            Org("Dist Active",     PlanConstants.Distributor, AccountStatusConstants.Active),      // 1499
            Org("Growth PastDue",  PlanConstants.Growth,      AccountStatusConstants.PastDue),     // 0 (not active)
            Org("Ops Trialing",    PlanConstants.Operations,  AccountStatusConstants.Trialing),    // 0 (not active)
            Org("Pilot Trialing",  PlanConstants.Pilot,       AccountStatusConstants.Trialing),    // 0 (not paid)
            Org("Ent Active",      PlanConstants.Enterprise,  AccountStatusConstants.Active)       // 0 (custom price)
        );
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.GetOverview(CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AdminOverviewDto>().Subject;

        dto.Mrr.Should().Be(149m + 399m + 1499m); // 2047
        dto.Arr.Should().Be((149m + 399m + 1499m) * 12m);
        dto.CountsByAccountStatus[AccountStatusConstants.Active].Should().Be(4);
        dto.CountsByAccountStatus[AccountStatusConstants.PastDue].Should().Be(1);
        dto.CountsByAccountStatus[AccountStatusConstants.Trialing].Should().Be(2);
    }

    // ── access probe ──────────────────────────────────────────────────────
    // The endpoint itself only returns 204; the real protection is the
    // class-level [AdminOnly] gate (covered by AdminOnlyAttributeTests). This
    // locks the contract the frontend nav-hide relies on: reaching the action
    // ⇒ 204 No Content, no body.
    [Fact]
    public void GetAccess_ReturnsNoContent()
    {
        var ctrl = Build(MakeDb());
        ctrl.GetAccess().Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetOverview_WhenStripeUnconfigured_StripeMrrNull_AndNotReconciled()
    {
        var db = MakeDb();
        db.Organisations.Add(Org("Solo", PlanConstants.Operations, AccountStatusConstants.Active));
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.GetOverview(CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AdminOverviewDto>().Subject;

        dto.StripeMrr.Should().BeNull("Stripe is not configured (no SecretKey)");
        dto.Reconciled.Should().BeFalse();
        dto.Mrr.Should().Be(399m, "the DB MRR is still returned when Stripe is unconfigured");
    }

    [Fact]
    public async Task GetOverview_CountsNewOrgsThisMonth()
    {
        var db = MakeDb();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Organisations.AddRange(
            Org("New A",  PlanConstants.Pilot, AccountStatusConstants.Trialing, monthStart.AddDays(1)),
            Org("New B",  PlanConstants.Pilot, AccountStatusConstants.Trialing, DateTime.UtcNow),
            Org("Old",    PlanConstants.Pilot, AccountStatusConstants.Trialing, monthStart.AddMonths(-2))
        );
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var dto = (await ctrl.GetOverview(CancellationToken.None) as OkObjectResult)!
            .Value as AdminOverviewDto;

        dto!.NewOrgsThisMonth.Should().Be(2);
    }

    [Fact]
    public async Task GetOverview_EmptyAccount_ReturnsZeros()
    {
        var ctrl = Build(MakeDb());
        var dto = (await ctrl.GetOverview(CancellationToken.None) as OkObjectResult)!
            .Value as AdminOverviewDto;

        dto!.Mrr.Should().Be(0m);
        dto.Arr.Should().Be(0m);
        dto.NewOrgsThisMonth.Should().Be(0);
        dto.StripeMrr.Should().BeNull();
        dto.Reconciled.Should().BeFalse();
    }

    // ── organisations listing (cross-tenant) ──────────────────────────────

    [Fact]
    public async Task GetOrganisations_ReturnsAllOrgs_WithMrrContributionAndAggregates()
    {
        var db = MakeDb();
        var orgA = Org("Acme", PlanConstants.Operations, AccountStatusConstants.Active);
        orgA.StripeCustomerId     = "cus_acme";
        orgA.StripeSubscriptionId = "sub_acme";
        var orgB = Org("Beta", PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.AddRange(orgA, orgB);

        var supplierA = Guid.NewGuid();
        db.Suppliers.AddRange(
            new Supplier { Id = supplierA, OrgId = orgA.Id, Name = "Sup A1", CreatedAt = DateTime.UtcNow },
            new Supplier { Id = Guid.NewGuid(), OrgId = orgA.Id, Name = "Sup A2", CreatedAt = DateTime.UtcNow },
            new Supplier { Id = Guid.NewGuid(), OrgId = orgA.Id, Name = "Sup A Deleted", CreatedAt = DateTime.UtcNow, DeletedAt = DateTime.UtcNow },
            new Supplier { Id = Guid.NewGuid(), OrgId = orgA.Id, Name = "__sample__", CreatedAt = DateTime.UtcNow, IsSample = true }
        );

        db.PurchaseOrders.AddRange(
            MakeOrder(orgA.Id, supplierA, createdAt: DateTime.UtcNow.AddDays(-2)),
            MakeOrder(orgA.Id, supplierA, createdAt: DateTime.UtcNow.AddDays(-40)),  // outside 30d
            MakeOrder(orgA.Id, supplierA, createdAt: DateTime.UtcNow.AddDays(-1), isSample: true) // excluded
        );
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.GetOrganisations(CancellationToken.None);

        var list = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<AdminOrganisationDto>>().Subject.ToList();

        list.Should().HaveCount(2);

        var acme = list.Single(o => o.Name == "Acme");
        acme.Plan.Should().Be(PlanConstants.Operations);
        acme.MrrContribution.Should().Be(399m);
        acme.StripeCustomerId.Should().Be("cus_acme");
        acme.StripeSubscriptionId.Should().Be("sub_acme");
        acme.SupplierCount.Should().Be(2, "deleted + sample suppliers are excluded");
        acme.OrderVolume30d.Should().Be(1, "only the non-sample order within 30 days counts");
        acme.LastOrderActivity.Should().NotBeNull();

        var beta = list.Single(o => o.Name == "Beta");
        beta.MrrContribution.Should().Be(0m, "Pilot contributes no MRR");
        beta.SupplierCount.Should().Be(0);
        beta.OrderVolume30d.Should().Be(0);
        beta.LastOrderActivity.Should().BeNull();
    }

    // ── invoices guard ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_WhenStripeUnconfigured_ReturnsCleanNon500()
    {
        var db = MakeDb();
        var org = Org("Acme", PlanConstants.Operations, AccountStatusConstants.Active);
        org.StripeCustomerId = "cus_acme";
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var request = new CreateInvoiceRequest(
            org.Id,
            new[] { new CreateInvoiceLineItem("Founder-led onboarding", 50000, 1) },
            "eur");

        var result = await ctrl.CreateInvoice(request, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().NotBe(500, "Stripe-unconfigured must be a clean 4xx/503, never a 500");
        status.StatusCode.Should().BeOneOf(400, 503);
    }

    [Fact]
    public async Task CreateInvoice_MissingOrganisationId_Returns400()
    {
        var ctrl = Build(MakeDb());
        var request = new CreateInvoiceRequest(
            Guid.Empty,
            new[] { new CreateInvoiceLineItem("x", 100, 1) });

        var result = await ctrl.CreateInvoice(request, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateInvoice_NoLineItems_Returns400()
    {
        var ctrl = Build(MakeDb());
        var request = new CreateInvoiceRequest(Guid.NewGuid(), Array.Empty<CreateInvoiceLineItem>());

        var result = await ctrl.CreateInvoice(request, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateInvoice_NonPositiveAmount_Returns400()
    {
        var ctrl = Build(MakeDb());
        var request = new CreateInvoiceRequest(
            Guid.NewGuid(),
            new[] { new CreateInvoiceLineItem("free?", 0, 1) });

        var result = await ctrl.CreateInvoice(request, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── per-org admin limit/trial overrides ───────────────────────────────

    [Fact]
    public async Task SetOrganisationLimits_SetsOverrides_AndReturnsEffectiveLimits()
    {
        var db = MakeDb();
        var org = Org("Acme", PlanConstants.Growth, AccountStatusConstants.Active); // defaults 150 / 5
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var request = new SetOrgLimitsRequest(
            OrderLimitOverride: 1000,
            SupplierLimitOverride: 30);

        var result = await ctrl.SetOrganisationLimits(org.Id, request, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrgLimitsResponse>().Subject;
        dto.OrderLimitOverride.Should().Be(1000);
        dto.SupplierLimitOverride.Should().Be(30);
        dto.EffectiveOrderLimit.Should().Be(1000, "override replaces the plan default 150");
        dto.EffectiveSupplierLimit.Should().Be(30);

        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.OrderLimitOverride.Should().Be(1000);
        saved.SupplierLimitOverride.Should().Be(30);
    }

    [Fact]
    public async Task SetOrganisationLimits_ExtendTrialDays_PushesEffectiveTrialEndIntoFuture()
    {
        var db = MakeDb();
        var org = Org("Beta", PlanConstants.Pilot, AccountStatusConstants.Trialing);
        org.TrialStartedAt = DateTime.UtcNow.AddDays(-40); // default window long gone
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(ExtendTrialDays: 60), CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrgLimitsResponse>().Subject;
        dto.EffectiveTrialEndsAt.Should().BeAfter(DateTime.UtcNow.AddDays(59));
        dto.TrialEndsAtOverride.Should().NotBeNull();
    }

    [Fact]
    public async Task SetOrganisationLimits_ClearFlag_RemovesOverride()
    {
        var db = MakeDb();
        var org = Org("Gamma", PlanConstants.Operations, AccountStatusConstants.Active);
        org.OrderLimitOverride = 9999;
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(ClearOrderLimit: true), CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrgLimitsResponse>().Subject;
        dto.OrderLimitOverride.Should().BeNull();
        dto.EffectiveOrderLimit.Should().Be(500, "cleared override ⇒ Operations plan default");
    }

    [Fact]
    public async Task SetOrganisationLimits_NegativeLimit_Returns400()
    {
        var db = MakeDb();
        var org = Org("Delta", PlanConstants.Growth, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(OrderLimitOverride: -1), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SetOrganisationLimits_AbsurdExtendTrialDays_Returns400_NotOverflow500()
    {
        // An absurd extension (well beyond DateTime.MaxValue when added to UtcNow)
        // would throw ArgumentOutOfRangeException inside AddDays and — with no global
        // exception handler — surface as a 500. The upper-bound guard must turn it
        // into a clean 400 instead. We must reach SetOrganisationLimits WITHOUT it
        // throwing.
        var db = MakeDb();
        var org = Org("Epsilon", PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(ExtendTrialDays: int.MaxValue), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "an absurd extendTrialDays must be a clean 400, never an unhandled overflow → 500");
    }

    [Fact]
    public async Task SetOrganisationLimits_UnknownOrg_Returns404()
    {
        var ctrl = Build(MakeDb());
        var result = await ctrl.SetOrganisationLimits(
            Guid.NewGuid(), new SetOrgLimitsRequest(OrderLimitOverride: 100), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void SetOrganisationLimits_IsOnTheAdminOnlyGatedController()
    {
        // The endpoint lives on AdminController, which is class-decorated with
        // [AdminOnly] (fail-closed). A non-admin caller is therefore rejected with
        // 403 by the filter before reaching this action. (The filter's 403 behaviour
        // is exercised directly in AdminOnlyAttributeTests.)
        typeof(AdminController)
            .GetCustomAttributes(typeof(ProcuLink.Api.Auth.AdminOnlyAttribute), inherit: true)
            .Should().NotBeEmpty();

        typeof(AdminController)
            .GetMethod(nameof(AdminController.SetOrganisationLimits))
            .Should().NotBeNull("the admin limits endpoint must exist on the gated controller");
    }

    // ── manual account-status transition ──────────────────────────────────
    //
    // The 2026-07-24 gap: an org frozen by a Stripe cancel (account_status=read_only,
    // plan reverted to Pilot, subscription id nulled) had NO product surface to come back
    // — only a raw production UPDATE. read_only -> trialing is the ONE permitted transition;
    // everything else is refused because another writer owns it (see the endpoint's doc
    // comment and docs/superpowers/specs/2026-07-24-admin-account-status-endpoint-design.md).

    /// <summary>A frozen-Pilot org exactly as the cancel paths leave it:
    /// read_only + Pilot + no live subscription id, still inside its trial window.</summary>
    private static Organisation FrozenPilotOrg(string name = "Frozen Co")
    {
        var org = Org(name, PlanConstants.Pilot, AccountStatusConstants.ReadOnly);
        org.StripeCustomerId = "cus_live_kept";     // both cancel paths KEEP the customer id
        org.StripeSubscriptionId = null;            // ...and null the subscription id
        org.StripeSubscriptionStatus = "canceled";
        return org;
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_ReadOnlyToTrialing_UnfreezesTheOrg()
    {
        var db = MakeDb();
        var org = FrozenPilotOrg();                 // TrialStartedAt = now-10d ⇒ 4 days left
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrgAccountStatusResponse>().Subject;
        dto.PreviousAccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
        dto.RequestedAccountStatus.Should().Be(AccountStatusConstants.Trialing);
        dto.AccountStatus.Should().Be(AccountStatusConstants.Trialing);
        dto.RevertedByTrialWindow.Should().BeFalse("the org still has 4 days of Pilot window left");

        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(AccountStatusConstants.Trialing);
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_ExpiredTrialWindow_ReportsTheArbitersVerdict_NotALie()
    {
        // The endpoint does not own a copy of the expiry predicate — it hands the decision
        // back to MarkPilotExpiredIfNeededAsync (the canonical arbiter), which immediately
        // re-expires an org whose Pilot window has elapsed. The response must report what
        // the DB actually holds, and point the operator at the endpoint that really fixes it.
        var db = MakeDb();
        var org = FrozenPilotOrg("Long Lapsed Co");
        org.TrialStartedAt = DateTime.UtcNow.AddDays(-400);   // window long gone
        org.TrialEndsAt = DateTime.UtcNow.AddDays(-386);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrgAccountStatusResponse>().Subject;
        dto.RequestedAccountStatus.Should().Be(AccountStatusConstants.Trialing);
        dto.AccountStatus.Should().Be(AccountStatusConstants.TrialExpired, "the trial arbiter re-expired it");
        dto.RevertedByTrialWindow.Should().BeTrue();
        dto.Note.Should().Contain("limits", "the note must name the endpoint that extends the trial");

        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(AccountStatusConstants.TrialExpired,
            "the response must never claim a status the database does not hold");
    }

    [Theory]
    [InlineData(AccountStatusConstants.Active)]        // Stripe-owned; would be a lie / free paid features
    [InlineData(AccountStatusConstants.PastDue)]       // Stripe-derived
    [InlineData(AccountStatusConstants.Cancelled)]     // Stripe-derived
    [InlineData(AccountStatusConstants.ReadOnly)]      // no proven need for a manual freeze
    [InlineData(AccountStatusConstants.TrialExpired)]  // the trial arbiter owns this
    public async Task SetOrganisationAccountStatus_TargetOutsideThePermittedSet_Returns400(string target)
    {
        var db = MakeDb();
        var org = FrozenPilotOrg();
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(target), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly, "a refused transition must not write");
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_UnknownStatusString_Returns400()
    {
        var db = MakeDb();
        var org = FrozenPilotOrg();
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest("unfrozen"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
    }

    [Theory]
    [InlineData(AccountStatusConstants.Active)]
    [InlineData(AccountStatusConstants.Trialing)]
    [InlineData(AccountStatusConstants.PastDue)]
    [InlineData(AccountStatusConstants.TrialExpired)]
    public async Task SetOrganisationAccountStatus_SourceOutsideThePermittedSet_Returns400(string from)
    {
        // Only read_only is a permitted SOURCE. trial_expired in particular is already handled
        // automatically by the trial arbiter once an admin extends the trial via .../limits.
        var db = MakeDb();
        var org = FrozenPilotOrg();
        org.AccountStatus = from;
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(from);
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_OrgWithLiveSubscription_Returns400_ReconcilerOwnsIt()
    {
        // With a subscription id present the org is IN the reconciliation sweep
        // (StripeSubscriptionReconciliationService.ReconcileOrgAsync only early-returns when
        // the id is blank), so any status we wrote here would be re-derived from Stripe on the
        // next run. Refuse rather than write something that silently reverts.
        var db = MakeDb();
        var org = FrozenPilotOrg("Paused Co");
        org.StripeSubscriptionId = "sub_live_123";
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_NonPilotPlan_Returns400()
    {
        // trialing on a paid plan is not a state any writer produces.
        var db = MakeDb();
        var org = FrozenPilotOrg("Growth Frozen");
        org.Plan = PlanConstants.Growth;
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_MissingBodyOrStatus_Returns400()
    {
        var db = MakeDb();
        var org = FrozenPilotOrg();
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);

        (await ctrl.SetOrganisationAccountStatus(org.Id, null!, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatus: null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest("   "), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_UnknownOrg_Returns404()
    {
        var ctrl = Build(MakeDb());
        var result = await ctrl.SetOrganisationAccountStatus(
            Guid.NewGuid(), new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_IsCaseInsensitiveOnTheTargetStatus()
    {
        var db = MakeDb();
        var org = FrozenPilotOrg();
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(" Trialing "), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var saved = await db.Organisations.FindAsync(org.Id);
        saved!.AccountStatus.Should().Be(AccountStatusConstants.Trialing, "account_status is persisted lowercase");
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_WritesDurableAuditEvent_WithWhoWhenFromTo()
    {
        var db = MakeDb();
        var org = FrozenPilotOrg();
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = WithAdmin(Build(db), "user_admin_1", "founder@proculink.eu");
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "admin.org.account_status_changed");
        audit.OrgId.Should().Be(org.Id);
        audit.EntityId.Should().Be(org.Id);
        audit.EntityType.Should().Be("Organisation");
        audit.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        var payload = audit.Payload!.RootElement;
        payload.GetProperty("actor").GetProperty("sub").GetString().Should().Be("user_admin_1");
        payload.GetProperty("actor").GetProperty("email").GetString().Should().Be("founder@proculink.eu");
        var detail = payload.GetProperty("detail");
        detail.GetProperty("from").GetString().Should().Be(AccountStatusConstants.ReadOnly);
        detail.GetProperty("to").GetString().Should().Be(AccountStatusConstants.Trialing);
        detail.GetProperty("requested").GetString().Should().Be(AccountStatusConstants.Trialing);
    }

    [Fact]
    public async Task SetOrganisationAccountStatus_RefusedTransition_WritesNoAuditEvent()
    {
        var db = MakeDb();
        var org = FrozenPilotOrg();
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = WithAdmin(Build(db), "user_admin_1", "founder@proculink.eu");
        var result = await ctrl.SetOrganisationAccountStatus(
            org.Id, new SetOrgAccountStatusRequest(AccountStatusConstants.Active), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();

        (await db.AuditEvents.CountAsync(e => e.Action == "admin.org.account_status_changed"))
            .Should().Be(0, "a refused transition changed nothing — it must not fabricate an audit row");
    }

    [Fact]
    public void SetOrganisationAccountStatus_IsOnTheAdminOnlyGatedController()
    {
        typeof(AdminController)
            .GetCustomAttributes(typeof(ProcuLink.Api.Auth.AdminOnlyAttribute), inherit: true)
            .Should().NotBeEmpty();

        typeof(AdminController)
            .GetMethod(nameof(AdminController.SetOrganisationAccountStatus))
            .Should().NotBeNull("the account-status endpoint must exist on the gated controller");
    }

    // ── bulk erase ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkEraseOrders_EmptyFilter_Returns400()
    {
        var ctrl = Build(MakeDb());
        var result = await ctrl.BulkEraseOrders(
            Guid.NewGuid(), new ProcuLink.Core.Services.BulkEraseFilter(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "an empty filter must be rejected so a fat-finger can never mass-wipe an org");
    }

    [Fact]
    public async Task BulkEraseOrders_BlankPrefix_Returns400()
    {
        var ctrl = Build(MakeDb());
        var result = await ctrl.BulkEraseOrders(
            Guid.NewGuid(), new ProcuLink.Core.Services.BulkEraseFilter(PoNumberPrefix: "  "), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>("a blank prefix is not a real criterion");
    }

    [Fact]
    public async Task BulkEraseOrders_PassesOrgScopedFilter_AndReturnsResult()
    {
        var recording = new RecordingErasureService(
            new ProcuLink.Core.Services.BulkOrderErasureResult(7, 21, 7, 7, 7, 0, 0, 0, 7, 0, 0));
        var ctrl = Build(MakeDb(), recording);
        var orgId = Guid.NewGuid();

        var result = await ctrl.BulkEraseOrders(
            orgId, new ProcuLink.Core.Services.BulkEraseFilter(PoNumberPrefix: "TEST-"), CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ProcuLink.Core.Services.BulkOrderErasureResult>().Subject;
        dto.OrdersErased.Should().Be(7);

        recording.LastOrgId.Should().Be(orgId, "the route org id is forwarded — the erase is org-scoped");
        recording.LastFilter!.PoNumberPrefix.Should().Be("TEST-");
    }

    [Fact]
    public void BulkEraseOrders_IsOnTheAdminOnlyGatedController()
    {
        // Same fail-closed gate as every other admin action: the class-level
        // [AdminOnly] filter rejects a non-admin with 403 before this action runs.
        typeof(AdminController)
            .GetCustomAttributes(typeof(ProcuLink.Api.Auth.AdminOnlyAttribute), inherit: true)
            .Should().NotBeEmpty();

        typeof(AdminController)
            .GetMethod(nameof(AdminController.BulkEraseOrders))
            .Should().NotBeNull("the bulk-erase endpoint must exist on the gated controller");
    }

    // ── POST organisations/{id}/retention (blob retention — admin-only opt-in) ──

    [Fact]
    public async Task SetOrganisationRetention_SetsWindow_AndReportsEnabled()
    {
        var db = MakeDb();
        var org = Org("Retention Co", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationRetention(
            org.Id, new SetOrgRetentionRequest(RetentionDays: 90), CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrgRetentionResponse>().Subject;
        dto.RetentionDays.Should().Be(90);
        dto.RetentionEnabled.Should().BeTrue();

        (await db.Organisations.FindAsync(org.Id))!.RetentionDays.Should().Be(90);
    }

    [Fact]
    public async Task SetOrganisationRetention_Clear_DisablesRetention()
    {
        var db = MakeDb();
        var org = Org("Retention Co", PlanConstants.Operations, AccountStatusConstants.Active);
        org.RetentionDays = 30;
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationRetention(
            org.Id, new SetOrgRetentionRequest(Clear: true), CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrgRetentionResponse>().Subject;
        dto.RetentionDays.Should().BeNull();
        dto.RetentionEnabled.Should().BeFalse();

        (await db.Organisations.FindAsync(org.Id))!.RetentionDays.Should().BeNull(
            "NULL = retention disabled, the safe default");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task SetOrganisationRetention_RejectsNonPositiveWindow(int days)
    {
        var db = MakeDb();
        var org = Org("Retention Co", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.SetOrganisationRetention(
            org.Id, new SetOrgRetentionRequest(RetentionDays: days), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Organisations.FindAsync(org.Id))!.RetentionDays.Should().BeNull("a rejected request must change nothing");
    }

    [Fact]
    public async Task SetOrganisationRetention_RejectsEmptyRequest()
    {
        var db = MakeDb();
        var org = Org("Retention Co", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        // Neither a value nor clear=true — ambiguous for a destructive capability → 400.
        var result = await ctrl.SetOrganisationRetention(
            org.Id, new SetOrgRetentionRequest(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SetOrganisationRetention_UnknownOrg_Returns404()
    {
        var db = MakeDb();
        var ctrl = Build(db);

        var result = await ctrl.SetOrganisationRetention(
            Guid.NewGuid(), new SetOrgRetentionRequest(RetentionDays: 30), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── GDPR accountability: durable audit for destructive/credential actions ──
    // Finding #7: destructive/credential admin actions were logged only to ephemeral
    // stdout. Each must now write a durable, queryable AuditEvent capturing WHO did
    // WHAT to WHICH org/entity — with the admin identity recorded (never a secret).

    [Fact]
    public async Task EraseOrder_WritesDurableAuditEvent_WithActorAndTarget()
    {
        var db = MakeDb();
        var org = Org("Acme", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var orderId = Guid.NewGuid();
        var ctrl = WithAdmin(Build(db, new FoundErasureService()), "user_admin_1", "founder@proculink.eu");
        var result = await ctrl.EraseOrder(org.Id, orderId, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "admin.order.erased");
        audit.OrgId.Should().Be(org.Id);
        audit.EntityId.Should().Be(orderId, "the audit is keyed to the erased order");
        audit.UserId.Should().BeNull("the platform admin is cross-tenant, not an org AppUser");
        audit.Payload.Should().NotBeNull();
        audit.Payload!.RootElement.GetProperty("actor").GetProperty("sub").GetString()
            .Should().Be("user_admin_1");
        audit.Payload!.RootElement.GetProperty("actor").GetProperty("email").GetString()
            .Should().Be("founder@proculink.eu");
    }

    [Fact]
    public async Task BulkEraseOrders_WritesDurableAuditEvent()
    {
        var db = MakeDb();
        var org = Org("Acme", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = WithAdmin(Build(db, new FoundErasureService()), "user_admin_1", "founder@proculink.eu");
        var result = await ctrl.BulkEraseOrders(
            org.Id, new ProcuLink.Core.Services.BulkEraseFilter(PoNumberPrefix: "TEST-"), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "admin.orders.bulk_erased");
        audit.OrgId.Should().Be(org.Id);
        audit.Payload!.RootElement.GetProperty("actor").GetProperty("sub").GetString()
            .Should().Be("user_admin_1");
    }

    [Fact]
    public async Task SetOrganisationRetention_WritesDurableAuditEvent()
    {
        var db = MakeDb();
        var org = Org("Retention Co", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = WithAdmin(Build(db), "user_admin_1", "founder@proculink.eu");
        var result = await ctrl.SetOrganisationRetention(
            org.Id, new SetOrgRetentionRequest(RetentionDays: 90), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "admin.org.retention_changed");
        audit.OrgId.Should().Be(org.Id);
        audit.EntityId.Should().Be(org.Id);
    }

    [Fact]
    public async Task SetOrganisationLimits_WritesDurableAuditEvent()
    {
        var db = MakeDb();
        var org = Org("Acme", PlanConstants.Growth, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = WithAdmin(Build(db), "user_admin_1", "founder@proculink.eu");
        var result = await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(OrderLimitOverride: 1000, SupplierLimitOverride: 30),
            CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "admin.org.limits_changed");
        audit.OrgId.Should().Be(org.Id);
        audit.EntityId.Should().Be(org.Id);
    }

    [Fact]
    public async Task EraseOrder_NotFound_WritesNoAuditEvent()
    {
        // The no-op erase (order already gone / wrong org) must NOT fabricate an audit row.
        var db = MakeDb();
        var org = Org("Acme", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var ctrl = WithAdmin(Build(db, new NoopErasureService()), "user_admin_1", "founder@proculink.eu");
        var result = await ctrl.EraseOrder(org.Id, Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();

        (await db.AuditEvents.AnyAsync()).Should().BeFalse("a not-found erase changed nothing to audit");
    }

    // ── PO-number → org lookup (support triage) ───────────────────────────
    //
    // The support gap: "PO 4500012580 isn't arriving" gave the founder NO route
    // from a PO number to the owning organisation — the controller's only
    // PurchaseOrders read was a per-org aggregate. These tests pin the lookup:
    // cross-org match, org names attached, blank input refused, result bounded.

    [Fact]
    public void FindOrders_IsOnTheAdminOnlyGatedController()
    {
        // The endpoint lives on AdminController, which is class-decorated with
        // [AdminOnly] (fail-closed). A non-admin caller is therefore rejected with
        // 403 by the filter before reaching this action. (The filter's 403 behaviour
        // is exercised directly in AdminOnlyAttributeTests.)
        typeof(AdminController)
            .GetCustomAttributes(typeof(ProcuLink.Api.Auth.AdminOnlyAttribute), inherit: true)
            .Should().NotBeEmpty();

        typeof(AdminController)
            .GetMethod(nameof(AdminController.FindOrders))
            .Should().NotBeNull("the admin PO-number lookup must exist on the gated controller");
    }

    [Fact]
    public async Task FindOrders_MatchesAcrossOrgs_WithOrgAndSupplierNames()
    {
        var db = MakeDb();
        var orgA = Org("Acme", PlanConstants.Operations, AccountStatusConstants.Active);
        var orgB = Org("Beta", PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.AddRange(orgA, orgB);

        var supA = Guid.NewGuid();
        var supB = Guid.NewGuid();
        db.Suppliers.AddRange(
            new Supplier { Id = supA, OrgId = orgA.Id, Name = "Sup A", CreatedAt = DateTime.UtcNow },
            new Supplier { Id = supB, OrgId = orgB.Id, Name = "Sup B", CreatedAt = DateTime.UtcNow });

        // Same customer-quoted PO in two orgs — one stored exactly, one differing
        // only in case/padding, so the normalized key is what has to connect them.
        var exact = MakeOrder(orgA.Id, supA, createdAt: DateTime.UtcNow.AddDays(-1));
        exact.PoNumber = "PO-4500012580";
        exact.PoNumberNormalized = ProcuLink.Core.Services.PoNumberIdentity.Normalize(exact.PoNumber);

        var caseVariant = MakeOrder(orgB.Id, supB, createdAt: DateTime.UtcNow.AddDays(-2));
        caseVariant.PoNumber = "po-4500012580";
        caseVariant.PoNumberNormalized = ProcuLink.Core.Services.PoNumberIdentity.Normalize(caseVariant.PoNumber);

        var unrelated = MakeOrder(orgA.Id, supA, createdAt: DateTime.UtcNow);
        unrelated.PoNumber = "PO-9999";
        unrelated.PoNumberNormalized = ProcuLink.Core.Services.PoNumberIdentity.Normalize(unrelated.PoNumber);

        db.PurchaseOrders.AddRange(exact, caseVariant, unrelated);
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.FindOrders("  PO-4500012580 ", CancellationToken.None);

        var body = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AdminOrderFindResponse>().Subject;

        body.Count.Should().Be(2);
        body.Capped.Should().BeFalse();
        body.Matches.Should().HaveCount(2);

        var a = body.Matches.Single(m => m.OrgId == orgA.Id);
        a.OrgName.Should().Be("Acme");
        a.OrgSlug.Should().Be(orgA.Slug);
        a.OrderId.Should().Be(exact.Id);
        a.Status.Should().Be("delivered");
        a.SupplierName.Should().Be("Sup A");
        a.PoNumber.Should().Be("PO-4500012580");

        var b = body.Matches.Single(m => m.OrgId == orgB.Id);
        b.OrgName.Should().Be("Beta");
        b.OrgSlug.Should().Be(orgB.Slug);
        b.OrderId.Should().Be(caseVariant.Id);
        b.SupplierName.Should().Be("Sup B");
        b.PoNumber.Should().Be("po-4500012580");

        // Exact stored spelling sorts ahead of the case-variant match.
        body.Matches[0].OrderId.Should().Be(exact.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindOrders_BlankPo_Returns400(string? po)
    {
        var db = MakeDb();
        var ctrl = Build(db);

        var result = await ctrl.FindOrders(po, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task FindOrders_CapsAtTwenty_NewestFirst()
    {
        var db = MakeDb();
        var org = Org("Acme", PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        var sup = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = sup, OrgId = org.Id, Name = "Sup A", CreatedAt = DateTime.UtcNow });

        // 25 orders all carrying the same (normalized) PO number, oldest first.
        for (var i = 0; i < 25; i++)
        {
            var o = MakeOrder(org.Id, sup, createdAt: DateTime.UtcNow.AddDays(-25 + i));
            o.PoNumber = "PO-DUP-1";
            o.PoNumberNormalized = ProcuLink.Core.Services.PoNumberIdentity.Normalize(o.PoNumber);
            db.PurchaseOrders.Add(o);
        }
        await db.SaveChangesAsync();

        var ctrl = Build(db);
        var result = await ctrl.FindOrders("PO-DUP-1", CancellationToken.None);

        var body = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AdminOrderFindResponse>().Subject;

        body.Count.Should().Be(20);
        body.Capped.Should().BeTrue();
        body.Matches.Should().HaveCount(20);
        body.Matches.Should().BeInDescendingOrder(m => m.CreatedAt, "the founder wants the recent transits first");
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static PurchaseOrderEntity MakeOrder(
        Guid orgId, Guid supplierId, DateTime createdAt, bool isSample = false) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = $"PO-{Guid.NewGuid():N}",
            Status     = "delivered",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            CreatedAt  = createdAt,
            UpdatedAt  = createdAt,
            IsSample   = isSample,
        };
}
