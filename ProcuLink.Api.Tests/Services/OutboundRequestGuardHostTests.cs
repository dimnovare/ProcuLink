using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Security;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class OutboundRequestGuardHostTests
{
    private static OutboundRequestGuard Guard(bool allowPrivate = false)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = allowPrivate ? "true" : "false",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    [Fact]
    public async Task ValidateHostAsync_LocalhostName_IsBlocked()
    {
        var guard = Guard();
        var result = await guard.ValidateHostAsync("localhost", 25, CancellationToken.None);
        Assert.False(result.Allowed);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("169.254.169.254")]
    public void IsBlockedAddress_PrivateIPs_AreBlocked(string ip)
    {
        Assert.True(OutboundRequestGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public async Task ValidateHostAsync_AllowPrivate_BypassesCheck()
    {
        var guard = Guard(allowPrivate: true);
        var result = await guard.ValidateHostAsync("192.168.1.99", 25, CancellationToken.None);
        Assert.True(result.Allowed);
    }
}
