using System.Net.Security;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class FtpsDeliveryDispatcherTests
{
    private static FtpsDeliveryDispatcher Dispatcher() =>
        new(NullLogger<FtpsDeliveryDispatcher>.Instance);

    private static SupplierDeliveryConfig MakeConfig(object config) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = DeliveryProtocolConstants.Ftps,
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(config),
            EncryptedCredentials = string.Empty,
        };

    private static string Creds(string username = "ftp-user", string password = "secret") =>
        JsonSerializer.Serialize(new { username, password });

    [Fact]
    public void Protocol_IsFtps()
    {
        Dispatcher().Protocol.Should().Be("ftps");
        Dispatcher().Protocol.Should().Be(DeliveryProtocolConstants.Ftps);
    }

    [Fact]
    public async Task Dispatch_BlankHost_ReturnsFailure_DoesNotThrow()
    {
        var config = MakeConfig(new { host = "", remotePath = "/in" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("FTPS delivery configuration is invalid — host is required.");
    }

    [Fact]
    public async Task Dispatch_MalformedConfigJson_ReturnsParseFailure()
    {
        var config = MakeConfig(new { });
        config.ConfigJson = "{not-json";

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("FTPS delivery configuration could not be parsed.");
    }

    [Fact]
    public async Task Dispatch_MissingCredentials_ReturnsFailure()
    {
        var config = MakeConfig(new { host = "ftps.vendor.test", port = 21, remotePath = "/in" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, string.Empty, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("FTPS delivery credentials are missing — username is required.");
    }

    [Theory]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("..\\..\\windows\\system32", ".._.._windows_system32")]
    [InlineData("order.csv", "order.csv")]
    [InlineData("po 2026/01.xml", "po_2026_01.xml")]
    public void SanitiseFileName_StripsPathTraversal(string input, string expected)
    {
        FtpsDeliveryDispatcher.SanitiseFileName(input).Should().Be(expected);
    }

    [Fact]
    public void SanitiseFileName_EmptyOrAllStripped_FallsBackToDefault()
    {
        FtpsDeliveryDispatcher.SanitiseFileName("").Should().Be("delivery.bin");
        FtpsDeliveryDispatcher.SanitiseFileName("///").Should().Be("delivery.bin");
    }

    // ── Certificate validation decision logic ────────────────────────────────

    [Fact]
    public void ShouldAcceptCertificate_NoErrors_AcceptsRegardlessOfFlag()
    {
        // A valid certificate (no policy errors) is always accepted.
        FtpsDeliveryDispatcher.ShouldAcceptCertificate(SslPolicyErrors.None, allowInvalidCertificate: false)
            .Should().BeTrue("valid cert must always be accepted");
        FtpsDeliveryDispatcher.ShouldAcceptCertificate(SslPolicyErrors.None, allowInvalidCertificate: true)
            .Should().BeTrue("valid cert must always be accepted, even with override flag");
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    public void ShouldAcceptCertificate_PolicyErrors_DefaultFalse_Rejects(SslPolicyErrors errors)
    {
        // Secure by default: policy errors are rejected when the override is not set.
        FtpsDeliveryDispatcher.ShouldAcceptCertificate(errors, allowInvalidCertificate: false)
            .Should().BeFalse("policy errors must be rejected when allowInvalidCertificate is false (secure default)");
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    public void ShouldAcceptCertificate_PolicyErrors_ExplicitOverride_Accepts(SslPolicyErrors errors)
    {
        // Explicit opt-in override: operator has consciously accepted invalid certs for this supplier.
        FtpsDeliveryDispatcher.ShouldAcceptCertificate(errors, allowInvalidCertificate: true)
            .Should().BeTrue("policy errors must be tolerated when allowInvalidCertificate is explicitly true");
    }
}
