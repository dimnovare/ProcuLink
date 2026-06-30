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

public class SmtpDeliveryDispatcherTests
{
    // Build a guard with AllowPrivateNetworkTargets=true so existing unit tests
    // that use fictional hostnames (smtp.vendor.test, etc.) are not blocked by
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

    private static SmtpDeliveryDispatcher Dispatcher() =>
        new(NullLogger<SmtpDeliveryDispatcher>.Instance, AllowAllGuard());

    private static SupplierDeliveryConfig MakeConfig(object config) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = DeliveryProtocolConstants.Smtp,
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(config),
            EncryptedCredentials = string.Empty,
        };

    private static string Creds(string username = "smtp-user", string password = "secret") =>
        JsonSerializer.Serialize(new { username, password });

    [Fact]
    public void Protocol_IsSmtp()
    {
        Dispatcher().Protocol.Should().Be("smtp");
        Dispatcher().Protocol.Should().Be(DeliveryProtocolConstants.Smtp);
    }

    [Fact]
    public async Task Dispatch_BlankHost_ReturnsFailure_DoesNotThrow()
    {
        var config = MakeConfig(new { host = "", fromAddress = "po@buyer.test", toAddresses = "supplier@vendor.test" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery configuration is invalid — host is required.");
    }

    [Fact]
    public async Task Dispatch_MalformedConfigJson_ReturnsParseFailure()
    {
        var config = MakeConfig(new { });
        config.ConfigJson = "{not-json";

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery configuration could not be parsed.");
    }

    [Fact]
    public async Task Dispatch_NoRecipients_ReturnsFailure()
    {
        var config = MakeConfig(new { host = "smtp.vendor.test", fromAddress = "po@buyer.test", toAddresses = "" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery has no valid recipient addresses.");
    }

    [Fact]
    public async Task Dispatch_MissingCredentials_ReturnsFailure()
    {
        var config = MakeConfig(new
        {
            host = "smtp.vendor.test",
            fromAddress = "po@buyer.test",
            toAddresses = "supplier@vendor.test",
        });

        // Empty decryptedCredentials → credentials missing.
        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, string.Empty, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery credentials are missing — username is required.");
    }

    [Fact]
    public void TokenSubstitution_ReplacesPoNumberAndFileName()
    {
        var subject = SmtpDeliveryDispatcher.BuildFromTemplate(
            "PO {poNumber} attached as {fileName}", "DEMO-2026-001", "DEMO-2026-001.xml");

        subject.Should().Be("PO DEMO-2026-001 attached as DEMO-2026-001.xml");
    }

    [Fact]
    public void TokenSubstitution_NoTokens_ReturnsTemplateUnchanged()
    {
        var subject = SmtpDeliveryDispatcher.BuildFromTemplate(
            "Static subject line", "DEMO-2026-001", "order.csv");

        subject.Should().Be("Static subject line");
    }
}
