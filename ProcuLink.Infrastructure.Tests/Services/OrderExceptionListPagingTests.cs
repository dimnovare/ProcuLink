using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

// ════════════════════════════════════════════════════════════════════════════
//  OrderExceptionService.ListAsync used to return EVERY exception the
//  organisation had ever raised, with no window of any kind.
//
//  Exception rows are never deleted — ResolveAsync and IgnoreAsync flip State
//  and leave the row — so the set this method reads only ever grows. The size
//  of the response was therefore a function of how long the account had
//  existed, which is the definition of unbounded.
//
//  These tests pin the window itself: that it is applied, that it is applied in
//  the database rather than after the fact, that the clamp cannot be talked out
//  of by a hostile page size, and that the total is counted over the SAME
//  filter the rows came from.
// ════════════════════════════════════════════════════════════════════════════

public class OrderExceptionListPagingTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Seeds <paramref name="count"/> exceptions with STRICTLY DECREASING CreatedAt, so
    /// "newest first" has one unambiguous answer and an off-by-one in the page arithmetic
    /// shows up as the wrong row rather than as a reshuffle.
    /// </summary>
    private static async Task<List<OrderException>> SeedAsync(
        ProcuLinkDbContext db, Guid orgId, int count, string state = "open")
    {
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, count).Select(i => new OrderException
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            OrderId   = Guid.NewGuid(),
            Stage     = "Map",
            Code      = "unresolved_mapping",
            Severity  = "warning",
            State     = state,
            Message   = $"exception {i}",
            CreatedAt = start.AddSeconds(-i),   // index 0 is the newest
        }).ToList();

        db.OrderExceptions.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    // ── the window exists ────────────────────────────────────────────────────

    /// <summary>
    /// The defect, stated directly: 250 rows in the table, and the caller that asks for the
    /// default gets a bounded page rather than all of them — while still being told there are
    /// 250.
    /// </summary>
    [Fact]
    public async Task ListAsync_AtTheDefaultPageSize_DoesNotReturnTheWholeHistory()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, orgId, 250);

        var page = await new OrderExceptionService(db).ListAsync(
            orgId, null, 1, OrderExceptionPaging.DefaultPageSize, CancellationToken.None);

        page.Rows.Should().HaveCount(OrderExceptionPaging.DefaultPageSize);
        page.Total.Should().Be(250, "the caller still needs to know how much history exists");
        page.PageSize.Should().Be(OrderExceptionPaging.DefaultPageSize);
        page.Page.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst_AndTheSecondPageContinuesWhereTheFirstStopped()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var seeded = await SeedAsync(db, orgId, 30);
        var svc = new OrderExceptionService(db);

        var first  = await svc.ListAsync(orgId, null, 1, 10, CancellationToken.None);
        var second = await svc.ListAsync(orgId, null, 2, 10, CancellationToken.None);
        var third  = await svc.ListAsync(orgId, null, 3, 10, CancellationToken.None);

        first.Rows.Select(r => r.Message).Should().Equal(
            Enumerable.Range(0, 10).Select(i => $"exception {i}"));
        second.Rows.Select(r => r.Message).Should().Equal(
            Enumerable.Range(10, 10).Select(i => $"exception {i}"));
        third.Rows.Select(r => r.Message).Should().Equal(
            Enumerable.Range(20, 10).Select(i => $"exception {i}"));

        // The three pages together are the whole history, each row exactly once.
        first.Rows.Concat(second.Rows).Concat(third.Rows).Select(r => r.Id)
            .Should().BeEquivalentTo(seeded.Select(r => r.Id));
    }

    [Fact]
    public async Task ListAsync_PastTheEnd_ReturnsNoRowsButStillTheTotal()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, orgId, 12);

        var page = await new OrderExceptionService(db).ListAsync(orgId, null, 9, 10, CancellationToken.None);

        page.Rows.Should().BeEmpty();
        page.Total.Should().Be(12);
    }

    // ── the clamp ────────────────────────────────────────────────────────────

    /// <summary>
    /// The clamp is the whole bound. A caller that could ask for pageSize=100000 would have
    /// re-created the unbounded read through the front door.
    /// </summary>
    [Fact]
    public async Task ListAsync_ClampsAnOversizedPageSizeToTheCeiling_AndSaysSo()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, orgId, 250);

        var page = await new OrderExceptionService(db).ListAsync(
            orgId, null, 1, 100_000, CancellationToken.None);

        page.Rows.Should().HaveCount(OrderExceptionPaging.MaxPageSize);
        page.PageSize.Should().Be(OrderExceptionPaging.MaxPageSize,
            "the applied window is reported, never the requested one");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ListAsync_ClampsANonPositivePageSizeUpToOne(int requested)
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, orgId, 5);

        var page = await new OrderExceptionService(db).ListAsync(
            orgId, null, 1, requested, CancellationToken.None);

        page.Rows.Should().HaveCount(OrderExceptionPaging.MinPageSize);
        page.PageSize.Should().Be(OrderExceptionPaging.MinPageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ListAsync_TreatsANonPositivePageAsTheFirstPage(int requested)
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, orgId, 5);

        var page = await new OrderExceptionService(db).ListAsync(
            orgId, null, requested, 2, CancellationToken.None);

        page.Page.Should().Be(1);
        // A negative Skip would throw rather than page; the clamp is what stops that.
        page.Rows.Select(r => r.Message).Should().Equal("exception 0", "exception 1");
    }

    // ── the total describes the same population as the rows ──────────────────

    [Fact]
    public async Task ListAsync_TotalIsCountedOverTheStateFilter_NotTheWholeTable()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        await SeedAsync(db, orgId, 40, state: "open");
        await SeedAsync(db, orgId, 60, state: "resolved");

        var svc = new OrderExceptionService(db);

        var open = await svc.ListAsync(orgId, "open", 1, 10, CancellationToken.None);
        open.Total.Should().Be(40, "a pager built from 100 would offer pages that hold nothing");
        open.Rows.Should().OnlyContain(r => r.State == "open");

        var all = await svc.ListAsync(orgId, null, 1, 10, CancellationToken.None);
        all.Total.Should().Be(100);
    }

    /// <summary>
    /// Org scoping survives the rewrite. A paged read that lost its org predicate would leak
    /// another workspace's exception messages, which carry PO numbers.
    /// </summary>
    [Fact]
    public async Task ListAsync_CountsAndReturnsOnlyTheCallersOrganisation()
    {
        var db    = MakeDb();
        var mine  = Guid.NewGuid();
        var other = Guid.NewGuid();
        await SeedAsync(db, mine, 7);
        await SeedAsync(db, other, 90);

        var page = await new OrderExceptionService(db).ListAsync(mine, null, 1, 200, CancellationToken.None);

        page.Total.Should().Be(7);
        page.Rows.Should().OnlyContain(r => r.OrgId == mine);
    }
}
