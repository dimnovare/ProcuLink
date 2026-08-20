using System.Globalization;
using FluentAssertions;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The order-overage Stripe invoice-item description is customer-facing: it lands
/// verbatim on real invoices. Its shape must therefore not depend on the host OS
/// culture. On a comma-decimal server (de-DE, et-EE, ...) the pre-fix interpolation
/// rendered "Order overage: 50 orders x EUR0,50" — observed on a real test-mode
/// invoice item. These tests pin the invariant "0.50" under comma-decimal cultures.
/// </summary>
public class StripeBillingServiceOverageDescriptionCultureTests
{
    [Theory]
    [InlineData("de-DE")]
    [InlineData("et-EE")]
    public void Overage_description_uses_dot_decimal_regardless_of_server_culture(string cultureName)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            // Control: prove the culture would corrupt an unguarded interpolation,
            // so a pass below means the guard bites, not that the culture is inert.
            $"{0.50m:0.00}".Should().Be("0,50", "the test culture must be comma-decimal");

            var description = StripeBillingService.BuildOverageDescription(50);

            description.Should().Be("Order overage: 50 orders x EUR0.50");
            description.Should().NotContain("0,50");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
