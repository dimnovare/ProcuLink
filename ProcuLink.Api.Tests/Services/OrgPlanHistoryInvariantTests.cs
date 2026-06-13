using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// STRUCTURAL invariant of <c>org_plan_history</c> that the AS-OF metering reader
/// (<c>StripeBillingService.ComputePeriodOverageOrdersAsync</c>) silently depends on,
/// and which the <c>ProcuLinkDbContext</c> SaveChanges write hook is responsible for
/// upholding. Previously asserted only by a code comment ("baseline strictly BEFORE
/// the change row … so as-of resolution can never tie the old and new values at the
/// same instant"); these tests pin it so a regression fails CI.
///
/// The invariant: for any org, the history rows are STRICTLY ORDERED by
/// <see cref="OrgPlanHistory.EffectiveFrom"/> — no two rows for the same org share an
/// instant. The as-of reader picks "the latest row with EffectiveFrom ≤ windowStart";
/// if two rows tied at one instant, that pick (and therefore the metered cap/override
/// for a whole calendar window) would be ambiguous and order-dependent.
///
/// The sharpest case is the SELF-HEALED baseline: when a pre-feature org's plan/override
/// changes, the hook inserts a baseline with the OLD values AND a change row with the NEW
/// values in the SAME save. The baseline must sort strictly before the change row even in
/// the degenerate <c>CreatedAt == default</c> case (the 1 ms guard in the hook).
/// </summary>
public class OrgPlanHistoryInvariantTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Organisation NewOrg(string plan, int? orderLimitOverride = null, DateTime? createdAt = null)
    {
        var id = Guid.NewGuid();
        var when = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new Organisation
        {
            Id                 = id,
            ClerkOrgId         = $"org_{id:N}",
            Name               = "Invariant Org",
            Slug               = $"inv-{id:N}",
            Plan               = plan,
            AccountStatus      = AccountStatusConstants.Active,
            OrderLimitOverride = orderLimitOverride,
            CreatedAt          = when,
            TrialStartedAt     = when,
        };
    }

    private static async Task<List<OrgPlanHistory>> OrderedHistoryAsync(ProcuLinkDbContext db, Guid orgId) =>
        await db.OrgPlanHistories
            .Where(h => h.OrgId == orgId)
            .OrderBy(h => h.EffectiveFrom)
            .ToListAsync();

    /// <summary>The core invariant: EffectiveFrom is strictly increasing per org (no ties).</summary>
    private static void AssertStrictlyOrdered(IReadOnlyList<OrgPlanHistory> rows)
    {
        for (var i = 1; i < rows.Count; i++)
            rows[i].EffectiveFrom.Should().BeAfter(rows[i - 1].EffectiveFrom,
                $"history row {i} must be strictly after row {i - 1} — no two rows may tie at one instant");
    }

    // ── a normal lifecycle of mutations stays strictly ordered ───────────────

    [Fact]
    public async Task SequenceOfPlanAndOverrideChanges_RowsAreStrictlyOrderedByEffectiveFrom()
    {
        var db  = MakeDb();
        var org = NewOrg(PlanConstants.Pilot);
        db.Organisations.Add(org);
        await db.SaveChangesAsync(); // baseline

        // Distinct saves; delays keep the wall-clock EffectiveFrom values apart,
        // exactly as production mutations (separate webhook/admin calls) would be.
        // 20 ms comfortably exceeds the Windows DateTimeOffset.UtcNow tick resolution.
        org.Plan = PlanConstants.Growth;        await db.SaveChangesAsync(); await Task.Delay(20);
        org.OrderLimitOverride = 1000;          await db.SaveChangesAsync(); await Task.Delay(20);
        org.Plan = PlanConstants.Operations;    await db.SaveChangesAsync(); await Task.Delay(20);
        org.OrderLimitOverride = null;          await db.SaveChangesAsync();

        var rows = await OrderedHistoryAsync(db, org.Id);
        rows.Should().HaveCount(5, "baseline + four metering-relevant mutations");
        AssertStrictlyOrdered(rows);
    }

    // ── the sharp case: self-healed baseline orders strictly before its change ──

    [Fact]
    public async Task SelfHealedBaseline_OrdersStrictlyBeforeChangeRow()
    {
        var db  = MakeDb();
        var org = NewOrg(PlanConstants.Operations, orderLimitOverride: 750);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        // Simulate a pre-feature org with no history at all.
        db.OrgPlanHistories.RemoveRange(await db.OrgPlanHistories.Where(h => h.OrgId == org.Id).ToListAsync());
        await db.SaveChangesAsync();

        org.Plan = PlanConstants.Growth;
        await db.SaveChangesAsync(); // heals a baseline + appends the change, in one save

        var rows = await OrderedHistoryAsync(db, org.Id);
        rows.Should().HaveCount(2, "a baseline with the OLD values is healed in before the change row");
        rows[0].Plan.Should().Be(PlanConstants.Operations, "baseline records the PRE-change plan");
        rows[1].Plan.Should().Be(PlanConstants.Growth,     "change row records the new plan");
        AssertStrictlyOrdered(rows);
    }

    [Fact]
    public async Task SelfHealedBaseline_DegenerateCreatedAt_StillStrictlyOrdered()
    {
        // CreatedAt == default is the degenerate case the 1 ms guard in the hook
        // exists for. Without the guard the healed baseline and the change row could
        // both land at `now`, tying the as-of lookup. Pin that they DON'T tie.
        var db  = MakeDb();
        var org = NewOrg(PlanConstants.Growth, orderLimitOverride: 200, createdAt: default);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        db.OrgPlanHistories.RemoveRange(await db.OrgPlanHistories.Where(h => h.OrgId == org.Id).ToListAsync());
        await db.SaveChangesAsync();

        org.OrderLimitOverride = 500; // a metering change on a history-less, CreatedAt-default org
        await db.SaveChangesAsync();

        var rows = await OrderedHistoryAsync(db, org.Id);
        rows.Should().HaveCount(2);
        rows[0].OrderLimitOverride.Should().Be(200, "baseline holds the PRE-change override");
        rows[1].OrderLimitOverride.Should().Be(500, "change row holds the new override");
        AssertStrictlyOrdered(rows);
    }

    // ── the invariant holds independently per org (no cross-org interference) ──

    [Fact]
    public async Task TwoOrgsMutatingTogether_EachOrgsHistoryIsIndependentlyStrictlyOrdered()
    {
        var db = MakeDb();
        var a  = NewOrg(PlanConstants.Pilot);
        var b  = NewOrg(PlanConstants.Growth);
        db.Organisations.AddRange(a, b);
        await db.SaveChangesAsync();

        a.Plan = PlanConstants.Growth;
        b.OrderLimitOverride = 999;
        await db.SaveChangesAsync(); // both orgs mutate in the SAME save

        AssertStrictlyOrdered(await OrderedHistoryAsync(db, a.Id));
        AssertStrictlyOrdered(await OrderedHistoryAsync(db, b.Id));
        (await db.OrgPlanHistories.CountAsync(h => h.OrgId == a.Id)).Should().Be(2);
        (await db.OrgPlanHistories.CountAsync(h => h.OrgId == b.Id)).Should().Be(2);
    }
}
