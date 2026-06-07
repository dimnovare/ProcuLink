using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

public class OutboundRequestGuardTests
{
    // ── IsBlockedAddress — pure IP-range logic, no DNS ────────────────────────

    [Theory]
    [InlineData("169.254.169.254")]   // AWS/GCP/Railway cloud metadata
    [InlineData("127.0.0.1")]          // loopback
    [InlineData("127.100.50.25")]      // loopback range (127.0.0.0/8)
    [InlineData("10.0.0.1")]           // private 10/8
    [InlineData("10.255.255.255")]     // private 10/8 edge
    [InlineData("172.16.0.1")]         // private 172.16/12
    [InlineData("172.31.255.255")]     // private 172.16/12 edge
    [InlineData("192.168.1.1")]        // private 192.168/16
    [InlineData("192.168.0.0")]        // private 192.168/16 start
    [InlineData("100.64.0.1")]         // CGNAT/shared 100.64.0.0/10 start (RFC 6598)
    [InlineData("100.100.50.25")]      // CGNAT/shared 100.64.0.0/10 mid
    [InlineData("100.127.255.255")]    // CGNAT/shared 100.64.0.0/10 edge
    [InlineData("198.18.0.1")]         // benchmark 198.18.0.0/15 start (RFC 2544)
    [InlineData("198.19.255.255")]     // benchmark 198.18.0.0/15 edge
    [InlineData("0.0.0.0")]            // unspecified
    public void IsBlockedAddress_ReturnsTrueForBlockedIpv4(string ipStr)
    {
        var ip = IPAddress.Parse(ipStr);
        OutboundRequestGuard.IsBlockedAddress(ip).Should().BeTrue(
            because: $"{ipStr} is in a forbidden range");
    }

    [Theory]
    [InlineData("93.184.216.34")]      // example.com
    [InlineData("8.8.8.8")]            // Google DNS
    [InlineData("1.1.1.1")]            // Cloudflare DNS
    [InlineData("172.15.255.255")]     // just below private 172.16/12
    [InlineData("172.32.0.0")]         // just above private 172.16/12
    [InlineData("11.0.0.1")]           // not 10/8
    [InlineData("192.167.1.1")]        // not 192.168/16
    [InlineData("169.255.0.1")]        // not link-local
    [InlineData("168.254.0.1")]        // not link-local (first octet differs)
    [InlineData("100.63.255.255")]     // just below CGNAT 100.64.0.0/10
    [InlineData("100.128.0.0")]        // just above CGNAT 100.64.0.0/10
    [InlineData("198.17.255.255")]     // just below benchmark 198.18.0.0/15
    [InlineData("198.20.0.0")]         // just above benchmark 198.18.0.0/15
    public void IsBlockedAddress_ReturnsFalseForPublicIpv4(string ipStr)
    {
        var ip = IPAddress.Parse(ipStr);
        OutboundRequestGuard.IsBlockedAddress(ip).Should().BeFalse(
            because: $"{ipStr} is a public address");
    }

    [Fact]
    public void IsBlockedAddress_ReturnsTrueForIpv6Loopback()
    {
        OutboundRequestGuard.IsBlockedAddress(IPAddress.IPv6Loopback).Should().BeTrue();
    }

    [Fact]
    public void IsBlockedAddress_ReturnsTrueForIpv4MappedLoopback()
    {
        // ::ffff:127.0.0.1 — IPv4-mapped loopback
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");
        OutboundRequestGuard.IsBlockedAddress(mapped).Should().BeTrue();
    }

    [Fact]
    public void IsBlockedAddress_ReturnsTrueForIpv6LinkLocal()
    {
        // fe80::1 — link-local
        OutboundRequestGuard.IsBlockedAddress(IPAddress.Parse("fe80::1")).Should().BeTrue();
    }

    [Fact]
    public void IsBlockedAddress_ReturnsTrueForIpv6UniqueLocal()
    {
        // fc00::1 and fd00::1 — unique local (fc00::/7)
        OutboundRequestGuard.IsBlockedAddress(IPAddress.Parse("fc00::1")).Should().BeTrue();
        OutboundRequestGuard.IsBlockedAddress(IPAddress.Parse("fd00::1")).Should().BeTrue();
    }

    // ── ValidateAsync — scheme, localhost, and config-override ────────────────

    private static OutboundRequestGuard MakeGuard(bool allowPrivate)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = allowPrivate ? "true" : "false",
            })
            .Build();
        return new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
    }

    [Theory]
    [InlineData("ftp://example.com/orders.csv")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://evil.com/")]
    [InlineData("javascript:alert(1)")]
    public async Task ValidateAsync_RejectsNonHttpScheme(string url)
    {
        // Scheme check is always enforced regardless of AllowPrivateNetworkTargets.
        var guard = MakeGuard(allowPrivate: true);

        var result = await guard.ValidateAsync(url, default);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("scheme");
    }

    [Fact]
    public async Task ValidateAsync_RejectsRelativeUrl()
    {
        var guard = MakeGuard(allowPrivate: false);

        var result = await guard.ValidateAsync("/relative/path", default);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_RejectsLocalhostWhenFlagIsFalse()
    {
        var guard = MakeGuard(allowPrivate: false);

        var result = await guard.ValidateAsync("http://localhost/api", default);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("localhost");
    }

    [Fact]
    public async Task ValidateAsync_AllowsLocalhostWhenFlagIsTrue()
    {
        var guard = MakeGuard(allowPrivate: true);

        var result = await guard.ValidateAsync("http://localhost/api", default);

        // When AllowPrivateNetworkTargets = true the guard skips network checks
        // (scheme is still valid) and returns Allowed.
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_AllowsPublicHttpsUrl()
    {
        // example.com resolves to a public IP — should pass strict mode.
        var guard = MakeGuard(allowPrivate: false);

        var result = await guard.ValidateAsync("https://example.com/orders", default);

        // example.com (93.184.216.34) is a public address.
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_SchemeRejectionAlwaysApplies_EvenWhenFlagIsTrue()
    {
        var guard = MakeGuard(allowPrivate: true);

        var result = await guard.ValidateAsync("ftp://localhost/file.txt", default);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("scheme");
    }
}
