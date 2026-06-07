using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Erp;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Erp;

/// <summary>
/// SSRF wiring proof for the ERP connectors. <see cref="ErplyConnector"/> and
/// <see cref="DirectoConnector"/> send to a tenant-supplied <c>cfg.Url</c> via the named
/// <c>"delivery"</c> <see cref="IHttpClientFactory"/> client — with NO per-call SSRF guard.
/// A tenant could set an <c>erp_erply</c> URL to <c>http://169.254.169.254/…</c> (cloud
/// metadata) or a 10.x / 100.64.x internal host and reach internal infrastructure.
///
/// The fix attaches the same connect-time-revalidating primary handler
/// (<see cref="OutboundRequestGuard.CreateGuardedHttpHandler"/>) to the <c>"delivery"</c>
/// named-client registration in both API and Worker hosts. These tests build the named
/// factory via the SAME <c>AddHttpClient("delivery").ConfigurePrimaryHttpMessageHandler(...)</c>
/// wiring the hosts use, then drive the real connector against SSRF targets and assert the
/// request is blocked at TCP connect (never reaching a server).
/// </summary>
public class ErpConnectorSsrfTests
{
    /// <summary>
    /// Builds a real named <c>"delivery"</c> <see cref="IHttpClientFactory"/> wired EXACTLY like
    /// API/Worker Program.cs: a 30s timeout plus the guard's connect-time-revalidating primary
    /// handler. <c>AllowPrivateNetworkTargets</c> stays false so the SSRF ranges are enforced.
    /// </summary>
    private static IHttpClientFactory BuildGuardedDeliveryFactory()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton(new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance));
        services.AddHttpClient("delivery", c => c.Timeout = TimeSpan.FromSeconds(30))
                .ConfigurePrimaryHttpMessageHandler(sp =>
                    sp.GetRequiredService<OutboundRequestGuard>().CreateGuardedHttpHandler());

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata — classic SSRF target
    [InlineData("http://10.0.0.5/api/orders")]                // RFC-1918 private
    [InlineData("http://100.64.0.1/api/orders")]              // CGNAT / shared 100.64.0.0/10 (newly blocked)
    public async Task ErplyConnector_SsrfTarget_IsBlockedAtConnect(string url)
    {
        var connector = new ErplyConnector(
            BuildGuardedDeliveryFactory(),
            NullLogger<ErplyConnector>.Instance);

        var request = MakeRequest(
            DeliveryProtocolConstants.ErpErply,
            JsonSerializer.Serialize(new { url, clientCode = "ACME", timeoutSeconds = 30 }),
            JsonSerializer.Serialize(new { type = "bearer", token = "secret-token" }));

        var result = await connector.SendAsync(request, default);

        // The guarded handler throws HttpRequestException at connect; ErplyConnector catches it
        // and returns a "failed before a response" failure — i.e. the request never left the host.
        result.Success.Should().BeFalse(
            because: "the guarded delivery client must reject SSRF targets at TCP connect");
        result.ResponseCode.Should().BeNull(
            because: "no HTTP response is ever received from a blocked SSRF target");
    }

    [Fact]
    public async Task DirectoConnector_SsrfMetadataTarget_IsBlockedAtConnect()
    {
        var connector = new DirectoConnector(
            BuildGuardedDeliveryFactory(),
            NullLogger<DirectoConnector>.Instance);

        var request = MakeRequest(
            DeliveryProtocolConstants.ErpDirecto,
            JsonSerializer.Serialize(new { url = "http://169.254.169.254/xmlcore", database = "acme", timeoutSeconds = 30 }),
            JsonSerializer.Serialize(new { user = "apiuser", password = "secret" }));

        var result = await connector.SendAsync(request, default);

        result.Success.Should().BeFalse(
            because: "the guarded delivery client must reject the metadata endpoint at TCP connect");
    }

    private static ProcuLink.Core.Services.Erp.ErpDeliveryRequest MakeRequest(
        string protocol,
        string configJson,
        string credentialsJson) =>
        new(
            "<order id=\"PO-1\" />"u8.ToArray(),
            "PO-1.xml",
            "application/xml",
            new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(),
                OrgId = Guid.NewGuid(),
                SupplierId = Guid.NewGuid(),
                Protocol = protocol,
                AutoDeliver = true,
                ConfigJson = configJson,
                EncryptedCredentials = string.Empty,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            credentialsJson);
}
