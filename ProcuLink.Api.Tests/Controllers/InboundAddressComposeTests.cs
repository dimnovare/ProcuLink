using FluentAssertions;
using ProcuLink.Api.Controllers;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

public class InboundAddressComposeTests
{
    [Theory]
    [InlineData("in.proculink.eu,orders.proculink.eu", "abc123@in.proculink.eu")]
    [InlineData("orders.proculink.eu", "abc123@orders.proculink.eu")]
    [InlineData("", "orders@abc123.proculink.eu")]
    public void ComposeAddress_ShowsTheFirstConfiguredDomain(string configured, string expected) =>
        SettingsController.ComposeAddress("abc123", configured, null).Should().Be(expected);
}
