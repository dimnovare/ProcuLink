using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The org-plan-history SaveChanges chokepoint in <see cref="ProcuLinkDbContext"/>
/// and the boot baseline seeder (<see cref="OrgPlanHistorySeeder"/>):
///  • inserting an org writes ONE baseline row (current values @ CreatedAt);
///  • changing Plan or OrderLimitOverride appends a dated row in the same save;
///  • a pre-feature org (no history) gets a self-healed baseline with the OLD
///    values before the change row, so pre-change windows resolve correctly;
///  • non-billing changes (AccountStatus etc.) append nothing;
///  • the boot seed inserts exactly one baseline per history-less org and is
///    idempotent across reruns.
/// </summary>
public class OrgPlanHistoryWriteTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Organisation NewOrg(string plan = PlanConstants.Pilot, int? orderLimitOverride = null)
    {
        var id = Guid.NewGuid();
        return new Organisation
        {
            Id                 = id,
            ClerkOrgId         = $"org_{id:N}",
            Name               = "Hook Org",
            Slug               = $"hook-{id:N}",
            Plan               = plan,
            AccountStatus      = AccountStatusConstants.Trialing,
            OrderLimitOverride = orderLimitOverride,
            CreatedAt          = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            TrialStartedAt     = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    // ── creation baseline ────────────────────────────────────────────────

    [Fact]
    public async Task InsertingOrganisation_WritesOneBaselineRow_AtCreatedAt()
    {
        var db = MakeDb();
        var org = NewOrg(PlanConstants.Pilot);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var rows = await db.OrgPlanHistories.Where(h => h.OrgId == org.Id).ToListAsync();
        var row = rows.Should().ContainSingle("org creation writes exactly one baseline row").Subject;
        row.Plan.Should().Be(PlanConstants.Pilot);
        row.OrderLimitOverride.Should().BeNull();
        row.EffectiveFrom.Should().Be(new DateTimeOffset(org.CreatedAt));
    }

    // ── change append (same save, atomic with the org mutation) ──────────

    [Fact]
    public async Task ChangingPlan_AppendsOneDatedRow()
    {
        var db = MakeDb();
        var org = NewOrg(PlanConstants.Pilot);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;
        org.Plan = PlanConstants.Growth;
        await db.SaveChangesAsync();

        var rows = await db.OrgPlanHistories
            .Where(h => h.OrgId == org.Id)
            .OrderBy(h => h.EffectiveFrom)
            .ToListAsync();
        rows.Should().HaveCount(2, "baseline + one change row");
        rows[^1].Plan.Should().Be(PlanConstants.Growth);
        rows[^1].EffectiveFrom.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task ChangingOrderLimitOverride_AppendsOneDatedRow()
    {
        var db = MakeDb();
        var org = NewOrg(PlanConstants.Growth);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        org.OrderLimitOverride = 1000;
        await db.SaveChangesAsync();

        var rows = await db.OrgPlanHistories
            .Where(h => h.OrgId == org.Id)
            .OrderBy(h => h.EffectiveFrom)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[^1].Plan.Should().Be(PlanConstants.Growth, "the plan is unchanged");
        rows[^1].OrderLimitOverride.Should().Be(1000);
    }

    [Fact]
    public async Task NonBillingChange_AppendsNothing()
    {
        var db = MakeDb();
        var org = NewOrg(PlanConstants.Growth);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        org.AccountStatus = AccountStatusConstants.PastDue;
        org.BillingEmail  = "ops@example.test";
        await db.SaveChangesAsync();

        (await db.OrgPlanHistories.CountAsync(h => h.OrgId == org.Id))
            .Should().Be(1, "only the creation baseline — status/email changes are not plan history");
    }

    [Fact]
    public async Task RewritingSamePlanValue_AppendsNothing()
    {
        var db = MakeDb();
        var org = NewOrg(PlanConstants.Pilot);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        org.Plan = PlanConstants.Pilot; // e.g. MarkPilotStartedAsync on an already-Pilot org
        org.TrialEndsAt = DateTime.UtcNow.AddDays(14);
        await db.SaveChangesAsync();

        (await db.OrgPlanHistories.CountAsync(h => h.OrgId == org.Id))
            .Should().Be(1, "an unchanged plan value must not append a row");
    }

    // ── self-healing baseline for pre-feature orgs ────────────────────────

    [Fact]
    public async Task PlanChangeOnOrgWithNoHistory_SelfHealsBaselineWithOldValues()
    {
        var db = MakeDb();
        var org = NewOrg(PlanConstants.Operations, orderLimitOverride: 750);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        // Simulate a pre-feature org: wipe the auto-written creation baseline.
        db.OrgPlanHistories.RemoveRange(await db.OrgPlanHistories.Where(h => h.OrgId == org.Id).ToListAsync());
        await db.SaveChangesAsync();

        org.Plan = PlanConstants.Growth;
        await db.SaveChangesAsync();

        var rows = await db.OrgPlanHistories
            .Where(h => h.OrgId == org.Id)
            .OrderBy(h => h.EffectiveFrom)
            .ToListAsync();
        rows.Should().HaveCount(2, "a baseline with the OLD values is healed in before the change row");
        rows[0].Plan.Should().Be(PlanConstants.Operations, "the baseline records the PRE-change plan");
        rows[0].OrderLimitOverride.Should().Be(750, "the baseline records the PRE-change override");
        rows[0].EffectiveFrom.Should().Be(new DateTimeOffset(org.CreatedAt));
        rows[1].Plan.Should().Be(PlanConstants.Growth);
        rows[0].EffectiveFrom.Should().BeBefore(rows[1].EffectiveFrom,
            "the baseline must order strictly before the change row");
    }

    // ── boot baseline seed ────────────────────────────────────────────────

    [Fact]
    public async Task SeedMissingBaselines_SeedsOnlyHistorylessOrgs_AndIsIdempotent()
    {
        var db = MakeDb();
        var seeded   = NewOrg(PlanConstants.Growth, orderLimitOverride: 500); // will get auto baseline
        var unseeded = NewOrg(PlanConstants.Operations);
        db.Organisations.AddRange(seeded, unseeded);
        await db.SaveChangesAsync();

        // Make `unseeded` a pre-feature org (no history rows at all).
        db.OrgPlanHistories.RemoveRange(await db.OrgPlanHistories.Where(h => h.OrgId == unseeded.Id).ToListAsync());
        await db.SaveChangesAsync();

        var first = await OrgPlanHistorySeeder.SeedMissingBaselinesAsync(db);
        first.Should().Be(1, "only the history-less org gets a baseline");

        var row = (await db.OrgPlanHistories.Where(h => h.OrgId == unseeded.Id).ToListAsync())
            .Should().ContainSingle().Subject;
        row.Plan.Should().Be(PlanConstants.Operations);
        row.OrderLimitOverride.Should().BeNull();
        row.EffectiveFrom.Should().Be(new DateTimeOffset(unseeded.CreatedAt));

        (await db.OrgPlanHistories.CountAsync(h => h.OrgId == seeded.Id))
            .Should().Be(1, "an org that already has history is untouched");

        var second = await OrgPlanHistorySeeder.SeedMissingBaselinesAsync(db);
        second.Should().Be(0, "rerunning the seed (every boot) inserts nothing");
        (await db.OrgPlanHistories.CountAsync()).Should().Be(2);
    }
}
