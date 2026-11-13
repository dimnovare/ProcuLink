using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestSupport;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

// ════════════════════════════════════════════════════════════════════════════
//  THE DEFECT (v2 audit P0-5)
//
//  The order list RETURNS onboarding practice orders while every count reported
//  beside it excluded them, and nothing on the screen said so. The fix was a
//  label, not a filter: SampleCount on the list envelope and IsSample on every row.
//
//  The counters this file once reconciled against the list (GET /api/orders/summary,
//  /api/dashboard/stats, /api/onboarding/status) were removed with the order
//  screens in plan 13b; the list survives as the read-only archive, so the per-row
//  label is what is left to pin.
// ════════════════════════════════════════════════════════════════════════════

public class OrderCountsAndListDescribeOnePopulationTests
{
    private const int WindowSize = 200; // > any corpus below, so one window holds it all

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Harness(
        ProcuLinkDbContext Db,
        IOrderQueryService      Orders,
        Guid               OrgId);

    private static Harness Build()
    {
        var db     = MakeDb();
        var orgId  = Guid.NewGuid();

        return new Harness(db, OrderListTestSupport.BuildOrderService(db), orgId);
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

    // ── Every row declares itself ──────────────────────────────────────────

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
}
