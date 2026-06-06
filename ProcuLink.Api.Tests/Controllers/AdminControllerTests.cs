using FluentAssertions;
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

        return new AdminController(db, billing, config, NullLogger<AdminController>.Instance);
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
