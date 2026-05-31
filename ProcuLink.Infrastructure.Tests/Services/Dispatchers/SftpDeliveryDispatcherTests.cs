using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// Unit tests for <see cref="SftpDeliveryDispatcher"/>.
///
/// The dispatcher validates its config + credentials BEFORE it opens any SSH
/// connection, so every guard-path assertion here runs without a live SFTP
/// server: a malformed/blank config or missing credentials returns a
/// <c>DeliveryResult</c> immediately. The pure path/filename helpers
/// (<see cref="SftpDeliveryDispatcher.SanitiseFileName"/>,
/// <see cref="SftpDeliveryDispatcher.NormaliseRemoteDir"/>,
/// <see cref="SftpDeliveryDispatcher.GetDirectoryPath"/>) are exercised directly
/// via InternalsVisibleTo — the SSH transport itself is never invoked.
/// </summary>
public class SftpDeliveryDispatcherTests
{
    // Build a guard with AllowPrivateNetworkTargets=true so existing unit tests
    // that use fictional hostnames (sftp.vendor.test, etc.) are not blocked by
    // DNS resolution — the guard SSRF logic is tested separately in
    // OutboundRequestGuardHostTests and OutboundRequestGuardTests.
    private static OutboundRequestGuard AllowAllGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static SftpDeliveryDispatcher Dispatcher() =>
        new(NullLogger<SftpDeliveryDispatcher>.Instance, AllowAllGuard());

    private static SupplierDeliveryConfig MakeConfig(object config) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = DeliveryProtocolConstants.Sftp,
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(config),
            EncryptedCredentials = string.Empty,
        };

    private static string Creds(string username = "sftp-user", string password = "secret") =>
        JsonSerializer.Serialize(new { username, password });

    [Fact]
    public void Protocol_IsSftp()
    {
        Dispatcher().Protocol.Should().Be("sftp");
        Dispatcher().Protocol.Should().Be(DeliveryProtocolConstants.Sftp);
    }

    [Fact]
    public async Task Dispatch_BlankHost_ReturnsFailure_DoesNotThrow()
    {
        var config = MakeConfig(new { host = "", remotePath = "/in" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SFTP delivery configuration is invalid — host is required.");
    }

    [Fact]
    public async Task Dispatch_MalformedConfigJson_ReturnsParseFailure()
    {
        var config = MakeConfig(new { });
        config.ConfigJson = "{not-json";

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SFTP delivery configuration could not be parsed.");
    }

    [Fact]
    public async Task Dispatch_MissingCredentials_ReturnsFailure()
    {
        var config = MakeConfig(new { host = "sftp.vendor.test", port = 22, remotePath = "/in" });

        // Empty decryptedCredentials → credentials missing.
        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, string.Empty, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SFTP delivery credentials are missing — username is required.");
    }

    [Fact]
    public async Task Dispatch_CredentialsWithoutUsername_ReturnsFailure()
    {
        var config = MakeConfig(new { host = "sftp.vendor.test", port = 22, remotePath = "/in" });
        var credsWithoutUser = JsonSerializer.Serialize(new { password = "secret" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, credsWithoutUser, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SFTP delivery credentials are missing — username is required.");
    }

    // ── Filename sanitisation (path-traversal hardening) ──────────────────────

    [Theory]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("..\\..\\windows\\system32", ".._.._windows_system32")]
    [InlineData("order.csv", "order.csv")]
    [InlineData("po 2026/01.xml", "po_2026_01.xml")]
    public void SanitiseFileName_StripsPathTraversal(string input, string expected)
    {
        SftpDeliveryDispatcher.SanitiseFileName(input).Should().Be(expected);
    }

    [Fact]
    public void SanitiseFileName_EmptyOrAllStripped_FallsBackToDefault()
    {
        SftpDeliveryDispatcher.SanitiseFileName("").Should().Be("delivery.bin");
        SftpDeliveryDispatcher.SanitiseFileName("///").Should().Be("delivery.bin");
    }

    // ── Remote-directory normalisation ────────────────────────────────────────

    [Theory]
    [InlineData(null, ".")]
    [InlineData("", ".")]
    [InlineData("   ", ".")]
    [InlineData("/inbound/orders", "/inbound/orders")]
    [InlineData("inbound/orders", "./inbound/orders")]
    [InlineData("inbound\\orders", "./inbound/orders")]
    public void NormaliseRemoteDir_AbsoluteStaysAbsolute_RelativeGetsDotSlashPrefix(string? input, string expected)
    {
        SftpDeliveryDispatcher.NormaliseRemoteDir(input).Should().Be(expected);
    }

    // ── Directory-path extraction (used for makeDirectories) ──────────────────

    [Theory]
    [InlineData("/inbound/orders/order.csv", "/inbound/orders")]
    [InlineData("/order.csv", "/")]
    [InlineData("order.csv", "/")]
    public void GetDirectoryPath_ReturnsParentDirectory(string remotePath, string expected)
    {
        SftpDeliveryDispatcher.GetDirectoryPath(remotePath).Should().Be(expected);
    }
}
