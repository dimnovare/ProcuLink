using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Tests.TestSupport;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

// ════════════════════════════════════════════════════════════════════════════
//  THE DEFECT (v2 audit P0-5)
//
//    "A first-run org that takes the promoted sample path sees 'Received 0'
//     next to a card reading '1 orders received' and a table listing that
//     order."
//     — DashboardController.cs:61 vs OrderQueryService.cs:85-87, :134
//
//  Two queries, two populations, nothing on the screen saying so:
//
//    GET /api/orders/summary   .Where(o => o.OrgId == orgId && !o.IsSample)
//    GET /api/orders (list)    .Where(o => o.OrgId == organisationId)
//
//  The summary side is the correct one and is NOT what changed. Every number
//  the product reports already excludes practice orders — the billing meter
//  (StripeBillingService.CountOrdersAsync :841/:847, and the invoiced overage
//  :929), the plan gates that inherit it, the dashboard KPIs, the wire
//  topology, and the onboarding milestones, whose contract states it outright:
//
//    "Every flag/count EXCLUDES sample data (IsSample suppliers/orders):
//     running the sample order must never 'complete' onboarding with zero
//     real data."                            — OnboardingController.cs:40-42
//
//  The list is the outlier, and it is right to RETURN the practice order — the
//  user is deliberately sent to one to rehearse the review flow, so hiding it
//  would strand work they were told to do. What it could not do was SAY so.
//  So the fix is a label, not a filter: SampleCount on the list envelope,
//  SampleTotal on the summary, IsSample on every row.
//
//  WHAT THIS FILE ASSERTS — a property over BOTH queries, never a pair of
//  hard-coded expected numbers. "1 and 1" would still pass the day someone
//  changes both to "0 and 0". The property is:
//
//      listTotal - listSampleCount == summaryTotal
//
//  computed from a corpus whose composition the test varies, with the expected
//  values derived from what was seeded.
//
//  WHICH DIRECTION THE NEXT INSTANCE COMES FROM. Not this defect again — a
//  THIRD count, over yet another population, added later and rendered beside
//  these two. Two defences:
//    • EveryOrgWideOrderCountSurface... below pins the four surfaces that exist
//      today to ONE number, so a new count wired into any of them must agree.
//    • SampleExclusionIsDeclaredNotAssumedTests (Architecture) fails the build
//      when a new aggregate over PurchaseOrders appears anywhere in production
//      without declaring which population it counts. There is no EF
//      HasQueryFilter on IsSample — the exclusion is convention at 20-odd call
//      sites — so nothing else would catch it.
// ════════════════════════════════════════════════════════════════════════════

public class OrderCountsAndListDescribeOnePopulationTests
{
    private const int WindowSize = 200; // > any corpus below, so one window holds it all

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Harness(
        ProcuLinkDbContext   Db,
        DashboardController  Dashboard,
        OnboardingController Onboarding,
        IOrderService        Orders,
        Guid                 OrgId);

    private static Harness Build()
    {
        var db     = MakeDb();
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new Harness(
            db,
            new DashboardController(db, tenant.Object),
            new OnboardingController(db, tenant.Object),
            OrderListTestSupport.BuildOrderService(db),
            orgId);
    }

    private static PurchaseOrderEntity Order(Guid orgId, bool isSample, string status, int seq)
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(seq);
        return new PurchaseOrderEntity
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            PoNumber  = $"PO-{seq:D4}",
            Status    = status,
            OrderDate = DateOnly.FromDateTime(createdAt),
            Currency  = "EUR",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            IsSample  = isSample,
        };
    }

    /// <summary>
    /// Seeds <paramref name="real"/> real + <paramref name="samples"/> practice orders for the
    /// org, plus a decoy order in a DIFFERENT org. The decoy is not decoration: every assertion
    /// here is an equality between counts, so an org-scope regression in either query would
    /// otherwise cancel itself out and pass.
    /// </summary>
    private static async Task SeedAsync(Harness h, int real, int samples)
    {
        var seq = 0;
        for (var i = 0; i < real; i++)
            h.Db.PurchaseOrders.Add(Order(h.OrgId, isSample: false, StatusFor(i), seq++));
        for (var i = 0; i < samples; i++)
            h.Db.PurchaseOrders.Add(Order(h.OrgId, isSample: true, StatusFor(i), seq++));

        var foreignOrgId = Guid.NewGuid();
        h.Db.PurchaseOrders.Add(Order(foreignOrgId, isSample: false, "delivered", seq++));
        h.Db.PurchaseOrders.Add(Order(foreignOrgId, isSample: true, "pending_review", seq));

        await h.Db.SaveChangesAsync();

        // ── ANTI-VACUITY FLOOR ────────────────────────────────────────────────
        // Every assertion below is an equality between counts, and 0 == 0 - 0 holds
        // for an empty database. If a refactor ever breaks this seeding, the property
        // tests would go green for free. Prove the corpus is the size we asked for
        // BEFORE trusting anything computed over it.
        var seededForOrg = await h.Db.PurchaseOrders.CountAsync(o => o.OrgId == h.OrgId);
        seededForOrg.Should().Be(real + samples, "the corpus under test must actually exist");

        var seededSamples = await h.Db.PurchaseOrders.CountAsync(o => o.OrgId == h.OrgId && o.IsSample);
        seededSamples.Should().Be(samples, "practice orders must actually carry IsSample = true");

        (await h.Db.PurchaseOrders.CountAsync(o => o.OrgId != h.OrgId))
            .Should().Be(2, "the foreign-org decoys must exist or org scope is untested");
    }

    // Spread across statuses so a status-bucketed count cannot accidentally agree
    // with a total by only ever seeing one bucket.
    private static string StatusFor(int i) =>
        (i % 3) switch { 0 => "pending_review", 1 => "delivered", _ => "ready" };

    private static async Task<OrdersSummaryDto> SummaryAsync(Harness h) =>
        (await h.Dashboard.GetSummary(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrdersSummaryDto>().Subject;

    private static async Task<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount, int SampleCount)>
        ListAsync(Harness h)
    {
        var result = await h.Orders.ListWindowAsync(
            h.OrgId, skip: 0, take: WindowSize,
            status: null, supplierId: null, search: null, dateFrom: null, dateTo: null,
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    /// <summary>Reads a property off the anonymous object GetStats returns.</summary>
    private static int Stat(object payload, string name) =>
        (int)payload.GetType().GetProperty(name)!.GetValue(payload)!;

    // ── The corpora ───────────────────────────────────────────────────────────
    // First row IS the audit scenario: a first-run org whose ONLY order is the
    // promoted sample. The rest rotate the composition so no assertion can pass
    // by coincidence of one particular shape.
    public static TheoryData<int, int> Corpora => new()
    {
        { 0, 1 },   // ← P0-5 verbatim: nothing real, one practice order
        { 0, 3 },
        { 1, 1 },
        { 7, 2 },
        { 4, 0 },   // no practice order at all — the label must not invent one
    };

    // ── 1. The property, over both queries ────────────────────────────────────

    [Theory]
    [MemberData(nameof(Corpora))]
    public async Task SummaryTotalAndListTotalDescribeTheSamePopulation(int real, int samples)
    {
        var h = Build();
        await SeedAsync(h, real, samples);

        var summary                            = await SummaryAsync(h);
        var (items, listTotal, listSampleCount) = await ListAsync(h);

        // THE PROPERTY. Derived from the corpus, never hard-coded: with `real` and
        // `samples` both varying, a test that pinned literals could not hold here.
        (listTotal - listSampleCount).Should().Be(summary.Total,
            "the list total minus its practice orders IS the population the summary counts — " +
            "a screen that shows both numbers must be able to reconcile them");

        (summary.Total + summary.SampleTotal).Should().Be(listTotal,
            "and the same identity from the summary's side: every row the list will return is " +
            "either counted in Total or declared in SampleTotal, never silently omitted");

        listSampleCount.Should().Be(summary.SampleTotal,
            "both endpoints must agree on how many practice orders exist");

        // Cross-check against what was actually seeded, so the identity above cannot be
        // satisfied by two equally-wrong numbers.
        listTotal.Should().Be(real + samples);
        summary.Total.Should().Be(real);
        listSampleCount.Should().Be(samples);
        items.Should().HaveCount(real + samples, "the window is wider than the corpus");
    }

    // ── 2. The defect verbatim, spelled out ───────────────────────────────────

    [Fact]
    public async Task OrgWhoseOnlyOrderIsThePromotedSample_DoesNotShowZeroBesideATableOfOne()
    {
        var h = Build();
        await SeedAsync(h, real: 0, samples: 1);

        var summary                            = await SummaryAsync(h);
        var (items, listTotal, listSampleCount) = await ListAsync(h);

        // The two numbers the audit caught contradicting each other.
        summary.Total.Should().Be(0, "nothing real has been processed yet");
        listTotal.Should().Be(1, "the practice order is still returned — the user was sent to it");

        // …and the reconciliation that stops them being a contradiction.
        listSampleCount.Should().Be(1, "the list must SAY that its one row is a practice order");
        summary.SampleTotal.Should().Be(1, "and the '0' must say what it is excluding");
        (listTotal - listSampleCount).Should().Be(summary.Total);

        items.Should().ContainSingle().Which.IsSample.Should().BeTrue(
            "a count alone is not enough — the screen must know WHICH row to label");
    }

    // ── 3. Every row declares itself ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Corpora))]
    public async Task EveryReturnedRowSaysWhetherItIsAPracticeOrder(int real, int samples)
    {
        var h = Build();
        await SeedAsync(h, real, samples);

        var (items, _, listSampleCount) = await ListAsync(h);

        items.Count(i => i.IsSample).Should().Be(samples,
            "per-row IsSample is what lets the table badge the practice order instead of " +
            "showing it as indistinguishable from real work");
        items.Count(i => !i.IsSample).Should().Be(real);
        items.Count(i => i.IsSample).Should().Be(listSampleCount,
            "the envelope's count and the rows must not disagree with each other either");
    }

    // ── 4. The rotation: a THIRD count over yet another population ────────────

    [Theory]
    [MemberData(nameof(Corpora))]
    public async Task EveryOrgWideOrderCountSurfaceDescribesTheSamePopulation(int real, int samples)
    {
        var h = Build();
        await SeedAsync(h, real, samples);

        var summary                    = await SummaryAsync(h);
        var (_, listTotal, sampleCount) = await ListAsync(h);

        var stats = (await h.Dashboard.GetStats(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value!;

        var onboarding = (await h.Onboarding.GetStatus(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OnboardingStatusDto>().Subject;

        // Four surfaces, one number. GET /api/dashboard/stats, GET /api/onboarding/status,
        // GET /api/orders/summary and GET /api/orders are rendered together on the first-run
        // screen; if a later change makes any one of them count a different set, this fails
        // rather than shipping two numbers that disagree in front of the user.
        var reported = new (string Surface, int Value)[]
        {
            ("GET /api/orders/summary → total",           summary.Total),
            ("GET /api/dashboard/stats → totalOrders",    Stat(stats, "totalOrders")),
            ("GET /api/onboarding/status → orderCount",   onboarding.OrderCount),
            ("GET /api/orders → totalCount - sampleCount", listTotal - sampleCount),
        };

        reported.Select(r => r.Value).Distinct().Should().ContainSingle(
            "every org-wide order count on the first-run screen must describe ONE population; " +
            $"got {string.Join(", ", reported.Select(r => $"{r.Surface} = {r.Value}"))}");

        reported.Should().AllSatisfy(r => r.Value.Should().Be(real));
    }
}
