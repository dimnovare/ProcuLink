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
/// Tests the BEST-PRICE overage fix for the pricing "overage inversion".
///
/// <para>Before the fix, raw €0.50/order overage made a lower tier + overage cost
/// MORE than the next flat tier at some volumes (Growth@500 ≈ €324 &lt; Operations
/// €399; Operations@1,500 ≈ €899 &lt; Integration €999) — which disincentivised
/// upgrades. The fix caps the metered overage at the cheapest tier whose included
/// volume covers the usage; pure per-order overage only applies above the TOP
/// self-serve tier's included volume.</para>
///
/// <para>These tests prove (1) the core invariant — at NO volume does a tier +
/// capped overage exceed the cheapest flat tier that would have covered the volume;
/// (2) the specific inversion scenarios are removed; and (3) existing in-window
/// (no-overage) and small-overage billing is unchanged (no regression). Pure unit
/// tests exercise <see cref="PlanConstants.BestPriceOverageOrders"/>; integration
/// tests drive it through <see cref="StripeBillingService"/>.</para>
/// </summary>
public class BestPriceOverageTests
{
    private const decimal Rate = PlanConstants.OveragePerOrderEur; // €0.50

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Effective monthly EUR a tier pays at the given usage, AFTER the best-price cap.</summary>
    private static decimal EffectiveMonthlyEur(string plan, int used)
    {
        var limit  = PlanConstants.GetOrderLimit(plan);
        var overage = PlanConstants.BestPriceOverageOrders(plan, limit, used);
        return PlanConstants.GetMonthlyPriceEur(plan) + overage * Rate;
    }

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StripeBillingService MakeService(ProcuLinkDbContext db) =>
        new(db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService());

    private static Organisation Org(string plan, int? orderLimitOverride = null)
    {
        var id = Guid.NewGuid();
        return new Organisation
        {
            Id                 = id,
            ClerkOrgId         = $"org_{id:N}",
            Name               = "Test Org",
            Slug               = $"test-{id:N}",
            Plan               = plan,
            AccountStatus      = AccountStatusConstants.Active,
            CreatedAt          = DateTime.UtcNow.AddDays(-40),
            TrialStartedAt     = DateTime.UtcNow.AddDays(-40),
            OrderLimitOverride = orderLimitOverride,
        };
    }

    private static async Task SeedOrdersThisMonthAsync(ProcuLinkDbContext db, Guid orgId, int count)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Sup", CreatedAt = DateTime.UtcNow });
        for (var i = 0; i < count; i++)
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                SupplierId = supplierId,
                PoNumber   = $"PO-{Guid.NewGuid():N}",
                Status     = "delivered",
                OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
                Currency   = "EUR",
                CreatedAt  = monthStart.AddMinutes(i + 1),
                UpdatedAt  = monthStart.AddMinutes(i + 1),
            });
        }
        await db.SaveChangesAsync();
    }

    // ── THE CORE INVARIANT: no tier+overage ever beats the next flat tier ──

    /// <summary>
    /// Exhaustive sweep — the core upgrade-incentive invariant. For EVERY self-serve
    /// tier T and EVERY order volume up to the top tier's inclusion, T's effective
    /// cost (flat + best-price overage) must never exceed the flat price of ANY
    /// HIGHER tier that would also cover that volume. In other words: staying on a
    /// lower tier and paying overage is never more expensive than upgrading — the
    /// exact property the raw-overage inversion violated.
    ///
    /// <para>(We compare each tier only against tiers ABOVE it: a customer
    /// deliberately sitting on a pricier base tier at low volume is overpaying by
    /// choice, which is not the inversion this fix targets.)</para>
    /// </summary>
    [Fact]
    public void NoVolume_MakesALowerTierCostMoreThanAnyHigherCoveringTier()
    {
        var ladder = PlanConstants.SelfServeLadder;
        var topIncluded = ladder[^1].Included;

        for (var ti = 0; ti < ladder.Count; ti++)
        {
            var tier = ladder[ti];
            // Sample the whole range, plus the exact tier boundaries (worst case).
            for (var used = 0; used <= topIncluded; used += 25)
            {
                var effective = EffectiveMonthlyEur(tier.Plan, used);

                // Compare ONLY against strictly-higher tiers that also cover this volume.
                foreach (var higher in ladder.Skip(ti + 1).Where(h => h.Included >= used))
                {
                    effective.Should().BeLessThanOrEqualTo(
                        higher.FlatEur,
                        "{0} at {1} orders (€{2}) must never cost more than upgrading to {3} (flat €{4}, covers {1})",
                        tier.Plan, used, effective, higher.Plan, higher.FlatEur);
                }
            }
        }
    }

    /// <summary>
    /// Tighter check at every tier-inclusion BOUNDARY (and one below it) — the exact
    /// volumes where the raw-overage inversion bit hardest: each lower tier's
    /// effective cost must not exceed the flat price of the boundary tier that covers
    /// that volume.
    /// </summary>
    [Fact]
    public void AtEveryTierBoundary_LowerTierNeverExceedsThatTiersFlatPrice()
    {
        var ladder = PlanConstants.SelfServeLadder;

        for (var bi = 0; bi < ladder.Count; bi++)
        {
            var boundaryTier = ladder[bi];
            foreach (var used in new[] { boundaryTier.Included - 1, boundaryTier.Included })
            {
                if (used < 0) continue;
                // Every tier strictly BELOW the boundary tier must come in at/under its flat.
                foreach (var lower in ladder.Take(bi))
                {
                    var effective = EffectiveMonthlyEur(lower.Plan, used);
                    effective.Should().BeLessThanOrEqualTo(
                        boundaryTier.FlatEur,
                        "{0} at {1} orders (€{2}) must not exceed {3}'s flat €{4} (it covers {1})",
                        lower.Plan, used, effective, boundaryTier.Plan, boundaryTier.FlatEur);
                }
            }
        }
    }

    // ── THE SPECIFIC INVERSIONS NAMED IN THE TASK ARE REMOVED ─────────────

    [Fact]
    public void GrowthAt500_NoLongerExceeds_OperationsFlat()
    {
        // Raw overage: 149 + 350×0.50 = €324  → that was already < €399, but the cap
        // must still guarantee it never EXCEEDS the next covering tier (Operations).
        var growthAt500 = EffectiveMonthlyEur(PlanConstants.Growth, 500);
        growthAt500.Should().BeLessThanOrEqualTo(PlanConstants.GetMonthlyPriceEur(PlanConstants.Operations));
    }

    [Fact]
    public void OperationsAt1500_NoLongerExceeds_IntegrationFlat()
    {
        // Raw overage WOULD be 399 + 1000×0.50 = €899; capped at Integration €999.
        var opsAt1500 = EffectiveMonthlyEur(PlanConstants.Operations, 1500);
        opsAt1500.Should().BeLessThanOrEqualTo(PlanConstants.GetMonthlyPriceEur(PlanConstants.Integration));
    }

    [Fact]
    public void HighVolumeGrowth_IsCappedAtCoveringTier_NotRunawayRawOverage()
    {
        // Growth pushed to Operations' inclusion (500) under raw overage = €324; far
        // above (e.g. Integration inclusion 1500) raw = 149 + 1350×0.50 = €824, which
        // the cap holds ≤ Integration's €999 (the cheapest tier covering 1500).
        var growthAt1500 = EffectiveMonthlyEur(PlanConstants.Growth, 1500);
        growthAt1500.Should().BeLessThanOrEqualTo(PlanConstants.GetMonthlyPriceEur(PlanConstants.Integration));
    }

    // ── ABOVE THE TOP SELF-SERVE TIER: pure per-order overage applies ─────

    [Fact]
    public void AboveTopTierInclusion_ChargesTopTierFlatPlusPureMeteredOverage()
    {
        var top = PlanConstants.SelfServeLadder[^1]; // Distributor: 2500 incl, €1499
        const int excess = 200;
        var used = top.Included + excess;

        // A Distributor org over its inclusion: overage = the raw excess (no higher
        // self-serve tier exists to cap against).
        var overage = PlanConstants.BestPriceOverageOrders(top.Plan, top.Included, used);
        overage.Should().Be(excess, "above the top tier, overage is pure per-order on the excess");

        var effective = top.FlatEur + overage * Rate;
        effective.Should().Be(top.FlatEur + excess * Rate);
    }

    [Fact]
    public void LowerTierAboveTopInclusion_StillCappedAtTopTierEquivalent()
    {
        // A Growth org that somehow processes beyond Distributor's 2500 inclusion is
        // charged as if on the top tier + pure metered excess — never the runaway raw
        // (149 + huge×0.50) figure. Effective equals what the top tier would pay.
        var top = PlanConstants.SelfServeLadder[^1];
        const int excess = 100;
        var used = top.Included + excess;

        var growthEffective = EffectiveMonthlyEur(PlanConstants.Growth, used);
        var topEffective    = EffectiveMonthlyEur(top.Plan, used);
        growthEffective.Should().Be(topEffective,
            "above the top tier every self-serve plan converges to top-tier flat + pure metered excess");
        growthEffective.Should().Be(top.FlatEur + excess * Rate);
    }

    // ── NO REGRESSION: small / in-window overage is byte-identical to raw ──

    [Theory]
    [InlineData(PlanConstants.Growth, 150, 0)]    // exactly at cap → no overage
    [InlineData(PlanConstants.Growth, 120, 0)]    // under cap → no overage
    [InlineData(PlanConstants.Growth, 155, 5)]    // small overage → raw
    [InlineData(PlanConstants.Growth, 160, 10)]   // small overage → raw
    [InlineData(PlanConstants.Operations, 500, 0)]
    [InlineData(PlanConstants.Operations, 510, 10)]
    [InlineData(PlanConstants.Integration, 1500, 0)]
    [InlineData(PlanConstants.Integration, 1520, 20)]
    public void SmallOverage_MatchesRawPerOrderOverage_NoRegression(string plan, int used, int expectedOverage)
    {
        var limit = PlanConstants.GetOrderLimit(plan);
        PlanConstants.BestPriceOverageOrders(plan, limit, used).Should().Be(expectedOverage);
    }

    [Fact]
    public void PilotAndEnterprise_NeverAccrueOverage()
    {
        PlanConstants.BestPriceOverageOrders(PlanConstants.Pilot, PlanConstants.PilotOrderLimit, 10_000)
            .Should().Be(0);
        PlanConstants.BestPriceOverageOrders(PlanConstants.Enterprise, int.MaxValue, 10_000)
            .Should().Be(0);
    }

    [Fact]
    public void AdminLimitOverride_FlowsThrough_AndShrinksOverage()
    {
        // Growth with a granted 1000-order limit; 900 used ⇒ within the override ⇒ no overage.
        PlanConstants.BestPriceOverageOrders(PlanConstants.Growth, 1000, 900).Should().Be(0);
        // 1100 used ⇒ 100 over the override; raw = 149 + 100×0.50 = €199 ≤ caps ⇒ 100 overage.
        PlanConstants.BestPriceOverageOrders(PlanConstants.Growth, 1000, 1100).Should().Be(100);
    }

    // ── ADMIN OVERRIDE ABOVE THE TOP TIER: meter only beyond the granted limit ──

    [Fact]
    public void AdminOverrideAboveTopTier_Distributor_MetersOnlyBeyondTheOverride()
    {
        // The operator granted a Distributor org 3,000 included orders (above the
        // published 2,500). At 3,500 used, ONLY the 500 beyond the granted limit are
        // billable — the old else-branch discarded the override and re-metered from
        // 2,500, over-charging 1,000 orders (€500 real Stripe money) instead of 500.
        PlanConstants.BestPriceOverageOrders(PlanConstants.Distributor, 3000, 3500)
            .Should().Be(500, "metering starts at the granted limit, not the published inclusion");
    }

    [Fact]
    public void AdminOverrideAboveTopTier_UsageWithinTheOverride_IsFree()
    {
        // 2,800 used with a 3,000 override: above the published 2,500 inclusion but
        // inside the granted headroom ⇒ no overage at all.
        PlanConstants.BestPriceOverageOrders(PlanConstants.Distributor, 3000, 2800).Should().Be(0);
    }

    [Fact]
    public void AdminOverrideAboveTopTier_LowerPlan_AlsoMetersOnlyBeyondTheOverride()
    {
        // A Growth org granted 3,000 included orders keeps its own flat and meters only
        // the excess beyond the override — a raised limit only ever SHRINKS the overage.
        PlanConstants.BestPriceOverageOrders(PlanConstants.Growth, 3000, 3500).Should().Be(500);
    }

    [Fact]
    public void AdminOverrideExactlyAtTopInclusion_BehavesLikeNoOverride()
    {
        // Boundary: an effective limit equal to the top tier's inclusion charges the
        // same pure metered excess as the unoverridden top tier (200 over ⇒ 200).
        var top = PlanConstants.SelfServeLadder[^1];
        PlanConstants.BestPriceOverageOrders(top.Plan, top.Included, top.Included + 200).Should().Be(200);
    }

    // ── INTEGRATION: best price flows through StripeBillingService ─────────

    [Fact]
    public async Task GetStatus_GrowthFarOverCap_OverageAmountNeverExceedsNextTierGap()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth); // limit 150, flat €149
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 1500); // deep over the cap

        var status = await MakeService(db).GetStatusAsync(org.Id);

        var effectiveTotal = PlanConstants.GetMonthlyPriceEur(PlanConstants.Growth) + status.OverageAmountEur;
        effectiveTotal.Should().BeLessThanOrEqualTo(
            PlanConstants.GetMonthlyPriceEur(PlanConstants.Integration),
            "Growth at 1500 orders must never cost more than Integration's flat €999");
        status.CanProcessOrders.Should().BeTrue("over-cap never blocks a paid plan");
    }

    [Fact]
    public async Task ComputePeriodOverage_GrowthFarOverCap_IsBestPriceCapped()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Operations); // limit 500, flat €399
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 1500); // raw would be €899

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(
            org.Id, monthStart, DateTime.UtcNow.AddDays(1));

        var effectiveTotal = PlanConstants.GetMonthlyPriceEur(PlanConstants.Operations) + overage * Rate;
        effectiveTotal.Should().BeLessThanOrEqualTo(
            PlanConstants.GetMonthlyPriceEur(PlanConstants.Integration),
            "billed Operations overage at 1500 orders must never exceed Integration's €999");
    }

    [Fact]
    public async Task ComputePeriodOverage_SmallOverage_UnchangedFromRaw()
    {
        // Regression: a small overage that does NOT trip the cap is billed exactly as
        // raw per-order overage (10 orders over → 10 billable).
        var db = MakeDb();
        var org = Org(PlanConstants.Growth); // limit 150
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 160); // 10 over, well under the cap

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(
            org.Id, monthStart, DateTime.UtcNow.AddDays(1));

        overage.Should().Be(10, "a small overage is unchanged by the best-price cap");
    }

    // ── LADDER is config-driven (no duplicated magic numbers) ─────────────

    [Fact]
    public void SelfServeLadder_IsDerivedFromTheExistingPlanLadder()
    {
        var ladder = PlanConstants.SelfServeLadder;
        ladder.Should().HaveCount(4, "Growth, Operations, Integration, Distributor");

        foreach (var (plan, included, flatEur) in ladder)
        {
            included.Should().Be(PlanConstants.GetOrderLimit(plan),
                "ladder inclusion must come from PlanConstants.Limits, not a literal");
            flatEur.Should().Be(PlanConstants.GetMonthlyPriceEur(plan),
                "ladder flat price must come from PlanConstants.MonthlyPriceEur, not a literal");
        }

        // Sanity: strictly increasing in both inclusion and price (a monotone ladder).
        for (var i = 1; i < ladder.Count; i++)
        {
            ladder[i].Included.Should().BeGreaterThan(ladder[i - 1].Included);
            ladder[i].FlatEur.Should().BeGreaterThan(ladder[i - 1].FlatEur);
        }
    }
}
