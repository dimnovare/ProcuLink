using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Direct unit tests for the shared <see cref="StripeBillingMapping"/> helper extracted from
/// BillingController. These pin the mapping so the webhook and the reconciliation service can
/// never drift; the byte-identical webhook behaviour is separately proven by re-running
/// BillingControllerTests unchanged.
/// </summary>
public class StripeBillingMappingTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:GrowthPriceId"]            = "price_growth_m",
            ["Stripe:GrowthYearlyPriceId"]      = "price_growth_y",
            ["Stripe:OperationsPriceId"]        = "price_ops_m",
            ["Stripe:OperationsYearlyPriceId"]  = "price_ops_y",
            ["Stripe:IntegrationPriceId"]       = "price_int_m",
            ["Stripe:IntegrationYearlyPriceId"] = "price_int_y",
            ["Stripe:DistributorPriceId"]       = "price_dist_m",
            ["Stripe:DistributorYearlyPriceId"] = "price_dist_y",
        }).Build();

    [Theory]
    [InlineData("price_growth_m", PlanConstants.Growth)]
    [InlineData("price_growth_y", PlanConstants.Growth)]
    [InlineData("price_ops_m",    PlanConstants.Operations)]
    [InlineData("price_ops_y",    PlanConstants.Operations)]
    [InlineData("price_int_m",    PlanConstants.Integration)]
    [InlineData("price_int_y",    PlanConstants.Integration)]
    [InlineData("price_dist_m",   PlanConstants.Distributor)]
    [InlineData("price_dist_y",   PlanConstants.Distributor)]
    public void MapPriceIdToPlan_MapsKnownPrices(string priceId, string expected) =>
        StripeBillingMapping.MapPriceIdToPlan(Config(), priceId).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("price_unknown")]
    public void MapPriceIdToPlan_UnknownOrBlank_ReturnsNull(string? priceId) =>
        StripeBillingMapping.MapPriceIdToPlan(Config(), priceId).Should().BeNull();

    [Theory]
    [InlineData("trialing", AccountStatusConstants.Trialing)]
    [InlineData("active",   AccountStatusConstants.Active)]
    [InlineData("past_due", AccountStatusConstants.PastDue)]
    [InlineData("unpaid",   AccountStatusConstants.PastDue)]
    [InlineData("paused",   AccountStatusConstants.ReadOnly)]
    [InlineData("canceled", AccountStatusConstants.ReadOnly)]
    public void MapStatusToAccountStatus_MapsKnownStatuses(string stripeStatus, string expected) =>
        StripeBillingMapping.MapStatusToAccountStatus(stripeStatus, "current").Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("incomplete")]
    public void MapStatusToAccountStatus_UnknownOrNull_KeepsCurrent(string? stripeStatus) =>
        StripeBillingMapping.MapStatusToAccountStatus(stripeStatus, AccountStatusConstants.Active)
            .Should().Be(AccountStatusConstants.Active);
}
