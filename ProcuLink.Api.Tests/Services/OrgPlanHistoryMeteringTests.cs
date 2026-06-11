using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// AS-OF plan/override resolution in <see cref="StripeBillingService.ComputePeriodOverageOrdersAsync"/>
/// (the yearly-billing over-billing corridor fix). A yearly renewal invoice decomposes
/// into ~12 PAST calendar-month windows; each must be metered at the plan + admin
/// order-limit override that was in force AT THAT window's start (latest
/// org_plan_history row ≤ windowStart), never at today's values:
///  • mid-year DOWNGRADE must NOT retroactively re-meter old months at the new
///    lower cap (wrongful overage charges) — the exact reported scenario;
///  • mid-year UPGRADE must NOT retroactively erase overage that was really
///    accrued under the old lower cap (under-billing);
///  • orgs with NO history fall back to current org values — the pre-history
///    behaviour, pinning the monthly path byte-identical for unseeded orgs.
/// </summary>
public class OrgPlanHistoryMeteringTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StripeBillingService MakeService(ProcuLinkDbContext db) =>
        new(db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService());

    /// <summary>
    /// Adds an org with the given CURRENT plan, then clears the auto-written
    /// creation baseline so each test crafts its own history explicitly.
    /// </summary>
    private static async Task<Organisation> AddOrgWithCleanHistoryAsync(
        ProcuLinkDbContext db, string currentPlan, int? currentOverride = null)
    {
        var id = Guid.NewGuid();
        var org = new Organisation
        {
            Id                 = id,
            ClerkOrgId         = $"org_{id:N}",
            Name               = "History Org",
            Slug               = $"history-{id:N}",
            Plan               = currentPlan,
            AccountStatus      = AccountStatusConstants.Active,
            OrderLimitOverride = currentOverride,
            CreatedAt          = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TrialStartedAt     = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var auto = await db.OrgPlanHistories.Where(h => h.OrgId == id).ToListAsync();
        db.OrgPlanHistories.RemoveRange(auto);
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task AddHistoryAsync(
        ProcuLinkDbContext db, Guid orgId, string plan, int? orderLimitOverride, DateTimeOffset effectiveFrom)
    {
        db.OrgPlanHistories.Add(new OrgPlanHistory
        {
            Id                 = Guid.NewGuid(),
            OrgId              = orgId,
            Plan               = plan,
            OrderLimitOverride = orderLimitOverride,
            EffectiveFrom      = effectiveFrom,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds <paramref name="count"/> non-sample orders created inside [windowStart, …).</summary>
    private static async Task SeedOrdersInWindowAsync(
        ProcuLinkDbContext db, Guid orgId, DateTime windowStart, int count)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Sup", CreatedAt = windowStart });
        for (var i = 0; i < count; i++)
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                SupplierId = supplierId,
                PoNumber   = $"PO-{windowStart:yyyyMM}-{i}",
                Status     = "delivered",
                OrderDate  = DateOnly.FromDateTime(windowStart),
                Currency   = "EUR",
                CreatedAt  = windowStart.AddMinutes(i + 1),
                UpdatedAt  = windowStart.AddMinutes(i + 1),
            });
        }
        await db.SaveChangesAsync();
    }

    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar1 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Apr1 = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jul1 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Aug1 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sep1 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── the exact reported scenario: mid-year DOWNGRADE must not over-bill ──

    [Fact]
    public async Task DowngradeMidYear_PastWindowIsMeteredAtTheOldHigherCap_NotTodaysLowerCap()
    {
        var db = MakeDb();
        // Org is on Growth TODAY (cap 150), but was on Operations (cap 500) until Jul 1.
        var org = await AddOrgWithCleanHistoryAsync(db, PlanConstants.Growth);
        await AddHistoryAsync(db, org.Id, PlanConstants.Operations, null, Jan1);
        await AddHistoryAsync(db, org.Id, PlanConstants.Growth,     null, Jul1);

        // March: 160 orders — over Growth's 150, comfortably under Operations' 500.
        await SeedOrdersInWindowAsync(db, org.Id, Mar1, 160);

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(org.Id, Mar1, Apr1);

        overage.Should().Be(0,
            "March was served on Operations (cap 500); the later downgrade to Growth must not " +
            "retroactively re-meter March at the lower cap and charge wrongful overage");
    }

    [Fact]
    public async Task DowngradeMidYear_WindowAfterTheDowngrade_IsMeteredAtTheNewLowerCap()
    {
        var db = MakeDb();
        var org = await AddOrgWithCleanHistoryAsync(db, PlanConstants.Growth);
        await AddHistoryAsync(db, org.Id, PlanConstants.Operations, null, Jan1);
        await AddHistoryAsync(db, org.Id, PlanConstants.Growth,     null, Jul1);

        // August (after the Jul 1 downgrade): 160 orders — 10 over Growth's 150.
        await SeedOrdersInWindowAsync(db, org.Id, Aug1, 160);

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(org.Id, Aug1, Sep1);

        overage.Should().Be(10, "August really ran on Growth (cap 150), so its overage is genuine");
    }

    // ── upgrade direction: past windows keep the old LOWER cap (no under-billing) ──

    [Fact]
    public async Task UpgradeMidYear_PastWindowIsMeteredAtTheOldLowerCap_NotTodaysHigherCap()
    {
        var db = MakeDb();
        // Org is on Operations TODAY (cap 500), but was on Growth (cap 150) until Jul 1.
        var org = await AddOrgWithCleanHistoryAsync(db, PlanConstants.Operations);
        await AddHistoryAsync(db, org.Id, PlanConstants.Growth,     null, Jan1);
        await AddHistoryAsync(db, org.Id, PlanConstants.Operations, null, Jul1);

        // March: 160 orders — 10 over Growth's 150 back then.
        await SeedOrdersInWindowAsync(db, org.Id, Mar1, 160);

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(org.Id, Mar1, Apr1);

        overage.Should().Be(10,
            "March was served on Growth (cap 150); the later upgrade must not retroactively " +
            "erase overage that was genuinely accrued (under-billing)");
    }

    // ── admin order-limit override is also resolved as-of ────────────────────

    [Fact]
    public async Task OverrideRemovedMidYear_PastWindowKeepsTheOldOverride()
    {
        var db = MakeDb();
        // Growth all year. An admin override of 1000 was in force until Jul 1, then cleared.
        var org = await AddOrgWithCleanHistoryAsync(db, PlanConstants.Growth, currentOverride: null);
        await AddHistoryAsync(db, org.Id, PlanConstants.Growth, 1000, Jan1);
        await AddHistoryAsync(db, org.Id, PlanConstants.Growth, null, Jul1);

        // March: 200 orders — over the plan default 150, under the then-active 1000 override.
        await SeedOrdersInWindowAsync(db, org.Id, Mar1, 200);

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(org.Id, Mar1, Apr1);

        overage.Should().Be(0,
            "the 1000-order admin override was in force in March; clearing it later must not " +
            "retroactively meter March against the plan default");
    }

    // ── boundary: a row effective exactly AT the window start applies ────────

    [Fact]
    public async Task HistoryRowEffectiveExactlyAtWindowStart_AppliesToThatWindow()
    {
        var db = MakeDb();
        var org = await AddOrgWithCleanHistoryAsync(db, PlanConstants.Growth);
        await AddHistoryAsync(db, org.Id, PlanConstants.Operations, null, Jan1);
        await AddHistoryAsync(db, org.Id, PlanConstants.Growth,     null, Mar1); // exactly at window start

        await SeedOrdersInWindowAsync(db, org.Id, Mar1, 160); // 10 over Growth's 150

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(org.Id, Mar1, Apr1);

        overage.Should().Be(10, "a row effective AT the window start (≤) governs that window");
    }

    // ── no-history fallback: pre-feature orgs meter exactly as before ────────

    [Fact]
    public async Task NoHistoryRows_FallsBackToCurrentOrgValues()
    {
        var db = MakeDb();
        var org = await AddOrgWithCleanHistoryAsync(db, PlanConstants.Growth); // no history at all
        await SeedOrdersInWindowAsync(db, org.Id, Mar1, 160); // 10 over Growth's 150

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(org.Id, Mar1, Apr1);

        overage.Should().Be(10,
            "an org with no plan history must meter with its CURRENT plan — the pre-history " +
            "behaviour, keeping the monthly path byte-identical for unseeded orgs");
    }

    [Fact]
    public async Task NoHistoryRows_CurrentOverrideStillApplies()
    {
        var db = MakeDb();
        var org = await AddOrgWithCleanHistoryAsync(db, PlanConstants.Growth, currentOverride: 1000);
        await SeedOrdersInWindowAsync(db, org.Id, Mar1, 200);

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(org.Id, Mar1, Apr1);

        overage.Should().Be(0, "the fallback must use the org's current override too, exactly as before");
    }
}
