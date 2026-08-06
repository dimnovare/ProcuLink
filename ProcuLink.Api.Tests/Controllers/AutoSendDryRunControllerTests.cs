using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-33 stage 1 — the read surface over the recorded decisions.
///
/// <para>Stage 1 exists so a week of evidence can be READ before a real order is allowed to move
/// unattended. These tests pin the two things that make that reading trustworthy: the aggregate
/// adds up, and it never counts another organisation's orders.</para>
/// </summary>
public sealed class AutoSendDryRunControllerTests
{
    [Fact]
    public async Task Summary_counts_sends_holds_and_channels_for_this_org_only()
    {
        var orgId  = Guid.NewGuid();
        var otherOrg = Guid.NewGuid();

        await using var db = NewDb();

        // Two would-be sends over http, sharing one digest (the recurring-PO case), one over sftp.
        db.AutoSendDryRuns.AddRange(
            Row(orgId, sent: true,  AutoSendDecision.Clean, "http", digest: "aaa"),
            Row(orgId, sent: true,  AutoSendDecision.Clean, "http", digest: "aaa"),
            Row(orgId, sent: true,  AutoSendDecision.Clean, "sftp", digest: "bbb"),
            Row(orgId, sent: false, AutoSendDecision.AcceptanceBlocked, "http", digest: "ccc"),
            Row(orgId, sent: false, AutoSendDecision.UnresolvedLines,   "http", digest: "ddd"),
            // Another tenant's rows must not appear in any number below.
            Row(otherOrg, sent: true, AutoSendDecision.Clean, "http", digest: "zzz"));
        await db.SaveChangesAsync();

        var result = await NewController(db, orgId).GetSummary(CancellationToken.None);
        var dto = Assert.IsType<AutoSendDryRunController.AutoSendDryRunSummaryDto>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(5, dto.Evaluated);
        Assert.Equal(3, dto.WouldHaveSent);
        Assert.Equal(2, dto.Held);

        // Three would-be sends, but only TWO distinct documents — the difference between "a busy
        // week" and "the same purchase order over and over", which is the whole recurring-PO case.
        Assert.Equal(2, dto.DistinctDocuments);

        Assert.Equal(2, Assert.Single(dto.ByChannel, c => c.Channel == "http").Count);
        Assert.Equal(1, Assert.Single(dto.ByChannel, c => c.Channel == "sftp").Count);

        Assert.Equal(3, Assert.Single(dto.ByDecision, d => d.Decision == AutoSendDecision.Clean).Count);
        Assert.Equal(1, Assert.Single(dto.ByDecision, d => d.Decision == AutoSendDecision.AcceptanceBlocked).Count);
    }

    [Fact]
    public async Task List_returns_newest_first_and_can_filter_to_the_orders_that_would_have_moved()
    {
        var orgId = Guid.NewGuid();

        await using var db = NewDb();

        var older = Row(orgId, sent: true,  AutoSendDecision.Clean, "http", digest: "aaa");
        older.EvaluatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = Row(orgId, sent: false, AutoSendDecision.AcceptanceBlocked, "http", digest: "bbb");
        newer.EvaluatedAt = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

        db.AutoSendDryRuns.AddRange(older, newer);
        await db.SaveChangesAsync();

        var controller = NewController(db, orgId);

        var all = Rows(await controller.List(CancellationToken.None));
        Assert.Equal(new[] { newer.OrderId, older.OrderId }, all.Select(r => r.OrderId).ToArray());

        var sentOnly = Rows(await controller.List(CancellationToken.None, wouldHaveSent: true));
        Assert.Equal(older.OrderId, Assert.Single(sentOnly).OrderId);
    }

    [Fact]
    public async Task List_never_returns_another_organisations_decisions()
    {
        var orgId = Guid.NewGuid();

        await using var db = NewDb();
        db.AutoSendDryRuns.Add(Row(Guid.NewGuid(), sent: true, AutoSendDecision.Clean, "http", "zzz"));
        await db.SaveChangesAsync();

        Assert.Empty(Rows(await NewController(db, orgId).List(CancellationToken.None)));
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<AutoSendDryRunController.AutoSendDryRunDto> Rows(IActionResult result) =>
        Assert.IsType<List<AutoSendDryRunController.AutoSendDryRunDto>>(
            Assert.IsType<OkObjectResult>(result).Value);

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"autosend-read-{Guid.NewGuid():N}")
            .Options);

    private static AutoSendDryRunController NewController(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return new AutoSendDryRunController(db, tenant.Object);
    }

    private static AutoSendDryRun Row(Guid orgId, bool sent, string decision, string channel, string digest) => new()
    {
        Id             = Guid.NewGuid(),
        OrgId          = orgId,
        OrderId        = Guid.NewGuid(),
        SupplierId     = Guid.NewGuid(),
        WouldHaveSent  = sent,
        Decision       = decision,
        Channel        = channel,
        OutputFormat   = "csv",
        DecisionDigest = digest,
        EvaluatedAt    = DateTime.UtcNow,
    };
}
