using FluentAssertions;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Constants;

/// <summary>
/// Pricing-fix tests for the plan limits and the plan → capability gates in
/// <see cref="PlanConstants"/>. The decoupled-channel theories (webhook delivery, email, SFTP and
/// S3 ingestion on every paid plan) went with those four features in plan 13b (2026-11-13).
/// </summary>
public class PlanFeatureGateTests
{
    [Fact]
    public void OveragePerOrder_IsFiftyCents()
    {
        PlanConstants.OveragePerOrderEur.Should().Be(0.50m);
    }

    [Fact]
    public void IntegrationOrderLimit_RaisedTo1500_KeepingPerOrderPriceMonotonic()
    {
        PlanConstants.GetOrderLimit(PlanConstants.Operations).Should().Be(500);
        PlanConstants.GetOrderLimit(PlanConstants.Integration).Should().Be(1500);
        PlanConstants.GetOrderLimit(PlanConstants.Distributor).Should().Be(2500);

        // €/order is monotonically decreasing as you go up the volume ladder.
        var opsPerOrder  = PlanConstants.GetMonthlyPriceEur(PlanConstants.Operations)  / 500m;   // 0.798
        var intPerOrder  = PlanConstants.GetMonthlyPriceEur(PlanConstants.Integration) / 1500m;  // 0.666
        var distPerOrder = PlanConstants.GetMonthlyPriceEur(PlanConstants.Distributor) / 2500m;  // 0.600
        intPerOrder.Should().BeLessThan(opsPerOrder);
        distPerOrder.Should().BeLessThan(intPerOrder);
    }

    [Fact]
    public void EffectiveOrderLimit_UsesOverrideWhenSet_ElsePlanDefault()
    {
        PlanConstants.GetEffectiveOrderLimit(PlanConstants.Growth, null).Should().Be(150);
        PlanConstants.GetEffectiveOrderLimit(PlanConstants.Growth, 1000).Should().Be(1000);
        // Negative override is ignored → falls back to plan default.
        PlanConstants.GetEffectiveOrderLimit(PlanConstants.Growth, -5).Should().Be(150);
        // Zero is a legitimate (admin-set) limit.
        PlanConstants.GetEffectiveOrderLimit(PlanConstants.Growth, 0).Should().Be(0);
    }

    [Fact]
    public void EffectiveSupplierLimit_UsesOverrideWhenSet_ElsePlanDefault()
    {
        PlanConstants.GetEffectiveSupplierLimit(PlanConstants.Growth, null).Should().Be(5);
        PlanConstants.GetEffectiveSupplierLimit(PlanConstants.Growth, 25).Should().Be(25);
    }

    // ── SSO (SAML/OIDC) — Enterprise-only capability gate ─────────────────────
    // SSO is plan-gated to Enterprise. The capability is delivered by Clerk Enterprise Connections;
    // this flag is the presentation/policy gate the Settings "Single sign-on" tab and
    // BillingStatus.SsoAvailable read. See docs/strategy/2026-06-08-sso-saml-implementation.md.

    [Fact]
    public void Sso_IsAvailableOnEnterprise()
    {
        PlanConstants.PlanHasFeature(PlanConstants.Enterprise, BillingFeature.Sso)
            .Should().BeTrue("Enterprise must include SSO (SAML/OIDC)");
    }

    [Theory]
    [InlineData(PlanConstants.Pilot)]
    [InlineData(PlanConstants.Growth)]
    [InlineData(PlanConstants.Operations)]
    [InlineData(PlanConstants.Integration)]
    [InlineData(PlanConstants.Distributor)]
    public void Sso_IsRestrictedBelowEnterprise(string plan)
    {
        // Distributor sits above Integration in rank but is still NOT Enterprise,
        // so SSO must remain gated — this guards the ordinal-rank comparison from
        // accidentally unlocking SSO on the high-volume self-serve tier.
        PlanConstants.PlanHasFeature(plan, BillingFeature.Sso)
            .Should().BeFalse($"SSO must be gated to Enterprise, not available on {plan}");
    }

    [Fact]
    public void Sso_IsIncludedInEnterpriseFeatureSet_AndAbsentFromDistributor()
    {
        PlanConstants.FeaturesForPlan(PlanConstants.Enterprise)
            .Should().Contain(BillingFeature.Sso);
        PlanConstants.FeaturesForPlan(PlanConstants.Distributor)
            .Should().NotContain(BillingFeature.Sso);
    }
}
