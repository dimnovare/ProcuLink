using FluentAssertions;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Constants;

/// <summary>The document ladder (Starter/Pro/Business/Scale) beside the legacy one.</summary>
public class DocumentLadderTests
{
    [Fact]
    public void DocumentPlans_HaveEveryFeatureButSso()
    {
        var wrong = (from plan in PlanConstants.DocumentLadder
                     from feature in Enum.GetValues<BillingFeature>()
                     where PlanConstants.PlanHasFeature(plan, feature) != (feature != BillingFeature.Sso)
                     select $"{plan} / {feature}").ToList();
        wrong.Should().BeEmpty();
    }

    [Fact]
    public void Overage_IsPerPlanAndCappedWithinTheLadder()
    {
        PlanConstants.BestPriceOverageOrders(PlanConstants.Starter, 150, 150).Should().Be(0);
        PlanConstants.BestPriceOverageOrders(PlanConstants.Starter, 150, 151).Should().Be(1);

        // 400 documents: capped at Pro's price (it covers 400), never Business's.
        var over = PlanConstants.BestPriceOverageOrders(PlanConstants.Starter, 150, 400);
        var total = over * PlanConstants.GetOverageRateEur(PlanConstants.Starter) + 35m;
        total.Should().BeLessThanOrEqualTo(69m).And.BeGreaterThan(68.75m);

        PlanConstants.GetOverageRateEur(PlanConstants.Growth).Should().Be(0.50m)
            .And.Be(PlanConstants.OveragePerOrderEur);
    }

    [Fact]
    public void FlowLimits_FollowTheLadder()
    {
        PlanConstants.GetFlowLimit(PlanConstants.Pilot).Should().Be(2);
        PlanConstants.GetFlowLimit(PlanConstants.Starter).Should().Be(5);
        PlanConstants.GetFlowLimit(PlanConstants.Pro).Should().Be(20);
        PlanConstants.GetFlowLimit(PlanConstants.Business).Should().Be(100);
        PlanConstants.GetFlowLimit(PlanConstants.Scale).Should().BeNull();
        PlanConstants.GetFlowLimit(PlanConstants.Enterprise).Should().BeNull();
        PlanConstants.GetFlowLimit(PlanConstants.Growth).Should().BeNull();
        PlanConstants.GetFlowLimit("nonsense").Should().Be(2);
    }
}
