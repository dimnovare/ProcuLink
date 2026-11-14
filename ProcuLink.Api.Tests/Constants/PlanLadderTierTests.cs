using FluentAssertions;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Constants;

/// <summary>
/// The price list, pinned to absolute tiers.
///
/// WHAT WAS UNPROTECTED. <see cref="PlanConstants"/>.MinimumPlan decides, per capability, which
/// tier a customer has to buy. That is a COMMERCIAL decision — the difference between selling
/// per-supplier rules at €149/mo and at €2,500/mo — and until this file existed no test in either
/// repo held any of those numbers in place. The suites around it all move WITH the value:
///
///   • <c>BillingGateEnforcementIsRealTests</c> reads compiled IL and proves a gate is PRESENT and
///     provably reaches <c>IBillingService.HasFeatureAsync</c>. Its <c>Site</c> record has no plan
///     field at all, so it cannot express which tier, and does not try to.
///   • <c>BillingFeatureGateCoverageTests</c> pins only RELATIVE shape — on at the minimum, off on
///     the plan directly below it, monotone above it, off on Pilot — and every one of those
///     expectations is derived from <c>GetMinimumPlan(feature)</c> itself, so it re-derives around
///     any new value. Its one absolute claim is the Pilot lower bound.
///   • <c>BillingFeatureEnforcementTests</c> asserts the named sites really refuse, and matches the
///     403 exactly against <c>BillingGateErrors.RequiresPlan(capability, feature)</c> — again derived.
///
/// Measured consequence (history: <c>CustomSupplierRules</c> was deleted in plan 13b, 2026-11-13):
/// moving <c>[BillingFeature.CustomSupplierRules]</c> from Enterprise to
/// Growth left the entire backend suite green, and the frontend's hand-typed mirror
/// (<c>src/lib/gatedCapabilities.ts</c>) green with it. A capability sold at the top of the ladder
/// could be given away in a one-word edit with nothing anywhere objecting.
///
/// WHY EVERY FEATURE AND NOT JUST THAT ONE. When there were ten, five had some absolute pin
/// already, spread across three files and written incidentally (SSO at Enterprise by three separate
/// suites, the four decoupled channels at Growth). The other five were held by nothing beyond
/// "not on Pilot". Two features remain (plan 13b, 2026-11-13). A price list defended in patches is a price list with holes in it, and
/// which rows happen to be covered is an accident of what someone was fixing at the time. So the
/// whole table is pinned here, in one place, and <see cref="EveryFeature_IsPinned"/> makes it
/// impossible to add a capability without stating what it costs.
///
/// HOW TO CHANGE A TIER. Edit <c>PlanConstants.MinimumPlan</c>, edit the row below, and edit
/// <c>MINIMUM_PLAN</c> in the frontend's <c>src/lib/gatedCapabilities.ts</c> (whose
/// <c>src/test/backendMirror.test.ts</c> diffs the two repos against each other). Three deliberate
/// edits is the point: re-pricing should look like re-pricing, and it should be visible in the
/// diff of a review.
/// </summary>
public class PlanLadderTierTests
{
    /// <summary>
    /// What each gated capability costs — the tier a customer must be on to get it.
    ///
    /// The expected values are <see cref="PlanConstants"/> PLAN-NAME constants (<c>"growth"</c>,
    /// <c>"enterprise"</c>, …), never <c>GetMinimumPlan</c>. A pin that reads the value it is
    /// pinning is a constant compared to itself, which is exactly the self-certifying shape that
    /// left this table unguarded in the first place.
    /// </summary>
    public static readonly IReadOnlyDictionary<BillingFeature, string> SoldFrom =
        new Dictionary<BillingFeature, string>
        {
            // Plan 13b (2026-11-13) deleted the other eight rows with the legacy order surfaces
            // they gated; the two left are the ones with a gate site.
            [BillingFeature.AdvancedAudit]       = PlanConstants.Operations,
            [BillingFeature.Sso]                 = PlanConstants.Enterprise,
        };

    public static TheoryData<BillingFeature, string> PinnedTiers()
    {
        var data = new TheoryData<BillingFeature, string>();
        foreach (var (feature, plan) in SoldFrom) data.Add(feature, plan);
        return data;
    }

    [Theory]
    [MemberData(nameof(PinnedTiers))]
    public void EveryFeature_StartsOnThePlanItIsPricedAt(BillingFeature feature, string expectedPlan)
    {
        PlanConstants.GetMinimumPlan(feature).Should().Be(
            expectedPlan,
            $"{feature} is sold from {expectedPlan} up. Changing which tier unlocks it is a pricing " +
            "decision, not an implementation detail: it moves what a customer has to pay. If the " +
            "move is intended, change PlanConstants.MinimumPlan, this row, and MINIMUM_PLAN in the " +
            "frontend's src/lib/gatedCapabilities.ts together.");
    }

    /// <summary>
    /// The anti-vacuity floor. Without it the theory above proves only that the rows someone
    /// remembered to add are correct, and a new capability could ship with no stated price.
    /// </summary>
    [Fact]
    public void EveryFeature_IsPinned()
    {
        SoldFrom.Keys.Should().BeEquivalentTo(
            Enum.GetValues<BillingFeature>(),
            "every gated capability has a price and this file is where it is written down. A new " +
            "BillingFeature with no row here would be sold from whatever tier its MinimumPlan entry " +
            "happens to say, with nothing asserting that tier was chosen rather than typed.");
    }

    /// <summary>
    /// The pinned tiers must be real plans, and the table must not have collapsed onto one tier —
    /// a table where every row says the same thing would satisfy the theory above while carrying
    /// no pricing information at all.
    /// </summary>
    [Fact]
    public void ThePinnedTiers_AreRealPlans_AcrossMoreThanOneTier()
    {
        SoldFrom.Values.Should().OnlyContain(
            plan => PlanConstants.All.Contains(plan),
            "a capability cannot be priced at a tier that is not on the ladder");

        SoldFrom.Values.Distinct().Should().HaveCountGreaterThan(
            1,
            "the ladder differentiates tiers by what they include — if every capability were pinned " +
            "to one plan this file would assert nothing about pricing");
    }
}
