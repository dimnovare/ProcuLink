using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

// ════════════════════════════════════════════════════════════════════════════
//  The four numbers on GET /api/dashboard/stats, pinned against a seeded set.
//
//  They used to come from four sequential awaited CountAsync calls — four
//  separate round trips to a managed Postgres, serialised, on the landing
//  page's first paint. They now come from one grouped query.
//
//  A refactor like that changes NOTHING a user can see if it is right, which is
//  exactly why it needs pinning: the failure mode is not a crash, it is a KPI
//  that quietly reads 40 instead of 12 and is believed. So the fixture below is
//  built so that all four numbers are DIFFERENT from each other and different
//  from the row count, and every discriminator the endpoint applies —
//  organisation, practice-order flag, calendar-month window, status — is
//  exercised by at least one order that lands on the wrong side of it.
//
//  Sample-order exclusion has its own file (DashboardExcludesSampleOrdersTests)
//  and is not restated here; what this file adds is the month window and the
//  arithmetic between the four figures.
// ════════════════════════════════════════════════════════════════════════════

public class DashboardStatsKpiTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (DashboardController Ctrl, Guid OrgId, ProcuLinkDbContext Db) Build()
    {
        var db     = MakeDb();
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return (new DashboardController(db, tenant.Object), orgId, db);
    }

    private static PurchaseOrderEntity Order(
        Guid orgId, string status, DateTime createdAt, bool isSample = false) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, SupplierId = Guid.NewGuid(),
        PoNumber = $"PO-{Guid.NewGuid():N}", Status = status,
        OrderDate = DateOnly.FromDateTime(createdAt),
        Currency = "EUR", CreatedAt = createdAt, UpdatedAt = createdAt,
        IsSample = isSample,
    };

    private static int Stat(object payload, string name) =>
        (int)payload.GetType().GetProperty(name)!.GetValue(payload)!;

    /// <summary>The instant the endpoint's own month window opens, computed the same way it does.</summary>
    private static DateTime MonthStart =>
        new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Comfortably inside the current month and never in the future, whatever day it is —
    /// month start plus a few hours, which is safe even on the 1st.
    /// </summary>
    private static DateTime ThisMonth => MonthStart.AddHours(3);

    /// <summary>Strictly before the window opens, by one second: the boundary case.</summary>
    private static DateTime JustBeforeThisMonth => MonthStart.AddSeconds(-1);

    private static DateTime LastYear => MonthStart.AddYears(-1);

    /// <summary>
    /// One fixture, four expectations, all different numbers.
    ///
    /// Real, this month:   4 (2 pending_review, 1 delivered, 1 parsing)
    /// Real, earlier:      3 (1 pending_review, 2 delivered)
    /// Practice orders:    2 (both this month, one delivered — must count nowhere)
    /// Another org:        2 (must count nowhere)
    ///
    /// so: totalOrdersThisMonth = 4, totalOrders = 7, pendingReview = 3, delivered = 3.
    /// pendingReview and delivered tie deliberately — a swap between them is invisible to a
    /// test that only checks they are "some number" — and the two totals differ from both.
    /// </summary>
    [Fact]
    public async Task GetStats_ReportsAllFourKpisOverTheSameSeededSet()
    {
        var (ctrl, orgId, db) = Build();
        var otherOrg = Guid.NewGuid();

        db.PurchaseOrders.AddRange(
            // Real, this month.
            Order(orgId, OrderStatusConstants.PendingReview, ThisMonth),
            Order(orgId, OrderStatusConstants.PendingReview, ThisMonth),
            Order(orgId, OrderStatusConstants.Delivered,     ThisMonth),
            Order(orgId, OrderStatusConstants.Parsing,       ThisMonth),

            // Real, but before the window opened. Counted in totalOrders, never in the month.
            Order(orgId, OrderStatusConstants.PendingReview, JustBeforeThisMonth),
            Order(orgId, OrderStatusConstants.Delivered,     JustBeforeThisMonth),
            Order(orgId, OrderStatusConstants.Delivered,     LastYear),

            // Practice orders: this month, one of them delivered. Counted nowhere.
            Order(orgId, OrderStatusConstants.Delivered,     ThisMonth, isSample: true),
            Order(orgId, OrderStatusConstants.PendingReview, ThisMonth, isSample: true),

            // Another organisation. Counted nowhere.
            Order(otherOrg, OrderStatusConstants.Delivered,     ThisMonth),
            Order(otherOrg, OrderStatusConstants.PendingReview, ThisMonth));
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetStats(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        Stat(ok.Value!, "totalOrdersThisMonth").Should().Be(4);
        Stat(ok.Value!, "totalOrders").Should().Be(7);
        Stat(ok.Value!, "pendingReview").Should().Be(3);
        Stat(ok.Value!, "delivered").Should().Be(3);
    }

    /// <summary>
    /// The month figure counts EVERY status inside the window, not just the interesting two.
    /// A grouped rewrite that summed only the statuses it names would silently under-report a
    /// workspace whose orders are mid-pipeline.
    /// </summary>
    [Fact]
    public async Task GetStats_ThisMonthCountsEveryStatus_NotOnlyPendingReviewAndDelivered()
    {
        var (ctrl, orgId, db) = Build();

        db.PurchaseOrders.AddRange(
            Order(orgId, OrderStatusConstants.Parsing,        ThisMonth),
            Order(orgId, OrderStatusConstants.ReadyToDeliver, ThisMonth),
            Order(orgId, OrderStatusConstants.DeliveryFailed, ThisMonth));
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetStats(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        Stat(ok.Value!, "totalOrdersThisMonth").Should().Be(3);
        Stat(ok.Value!, "totalOrders").Should().Be(3);
        Stat(ok.Value!, "pendingReview").Should().Be(0);
        Stat(ok.Value!, "delivered").Should().Be(0);
    }

    /// <summary>
    /// The window boundary itself: an order created exactly at the first instant of the month
    /// is inside it. The endpoint's predicate is <c>&gt;=</c>, and a rewrite that made it
    /// <c>&gt;</c> would lose a real order on the 1st and nowhere else — the kind of thing
    /// that is only ever noticed once a month.
    /// </summary>
    [Fact]
    public async Task GetStats_AnOrderCreatedAtTheFirstInstantOfTheMonthIsInsideTheWindow()
    {
        var (ctrl, orgId, db) = Build();

        db.PurchaseOrders.AddRange(
            Order(orgId, OrderStatusConstants.Delivered, MonthStart),
            Order(orgId, OrderStatusConstants.Delivered, JustBeforeThisMonth));
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetStats(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        Stat(ok.Value!, "totalOrdersThisMonth").Should().Be(1);
        Stat(ok.Value!, "totalOrders").Should().Be(2);
    }

    [Fact]
    public async Task GetStats_EmptyOrg_ReportsZeroesRatherThanFailing()
    {
        var (ctrl, _, _) = Build();

        var ok = (await ctrl.GetStats(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        Stat(ok.Value!, "totalOrdersThisMonth").Should().Be(0);
        Stat(ok.Value!, "totalOrders").Should().Be(0);
        Stat(ok.Value!, "pendingReview").Should().Be(0);
        Stat(ok.Value!, "delivered").Should().Be(0);
    }
}
