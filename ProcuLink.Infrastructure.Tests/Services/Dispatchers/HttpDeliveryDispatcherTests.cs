using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class HttpDeliveryDispatcherTests
{
    private static SupplierDeliveryConfig MakeConfig(string url, string method = "POST") =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "http",
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(new { url, method, timeoutSeconds = 30 }),
            EncryptedCredentials = string.Empty,
        };

    private static IHttpClientFactory MakeFactory(HttpStatusCode status, string body = "OK")
    {
        var handler = new FakeHttpMessageHandler(status, body);
        var client  = new HttpClient(handler);
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(client);
        return factory.Object;
    }

    /// <summary>Returns an OutboundRequestGuard configured to allow all private/local targets.</summary>
    private static OutboundRequestGuard MakePermissiveGuard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
    }

    [Fact]
    public async Task Dispatch_200_ReturnsSuccess()
    {
        var dispatcher = new HttpDeliveryDispatcher(MakeFactory(HttpStatusCode.OK), MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("https://example.com/orders");
        var creds = JsonSerializer.Serialize(new { type = "none" });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("PO,DATE\r\n001,2026-01-01"),
            "order.csv", "text/csv", config, creds, default);

        result.Success.Should().BeTrue();
        result.ResponseCode.Should().Be(200);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_422_ReturnsFailure()
    {
        var dispatcher = new HttpDeliveryDispatcher(
            MakeFactory(HttpStatusCode.UnprocessableEntity, "Invalid format"),
            MakePermissiveGuard(),
            NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("https://example.com/orders");
        var creds = JsonSerializer.Serialize(new { type = "none" });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"),
            "order.csv", "text/csv", config, creds, default);

        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(422);
        result.ErrorMessage.Should().Contain("422");
        result.ErrorMessage.Should().Contain("Response summary: Invalid format");
    }

    [Fact]
    public async Task Dispatch_ApiKeyAuth_SetsHeader()
    {
        string? capturedHeader = null;
        var handler = new CapturingHttpMessageHandler(req =>
        {
            req.Headers.TryGetValues("X-Api-Key", out var vals);
            capturedHeader = vals?.FirstOrDefault();
        });
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(new HttpClient(handler));

        var dispatcher = new HttpDeliveryDispatcher(factory.Object, MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("https://example.com");
        var creds = JsonSerializer.Serialize(new { type = "apikey", header = "X-Api-Key", value = "sk-secret" });

        await dispatcher.DispatchAsync(Array.Empty<byte>(), "f.csv", "text/csv", config, creds, default);

        capturedHeader.Should().Be("sk-secret");
    }

    [Fact]
    public async Task Dispatch_InvalidUrl_ReturnsConfigError()
    {
        var dispatcher = new HttpDeliveryDispatcher(MakeFactory(HttpStatusCode.OK), MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("not a url");
        var creds = JsonSerializer.Serialize(new { type = "none" });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"),
            "order.csv", "text/csv", config, creds, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("HTTP delivery endpoint URL is invalid.");
    }

    [Fact]
    public async Task Dispatch_MalformedCredentials_ReturnsGenericFailure()
    {
        var dispatcher = new HttpDeliveryDispatcher(MakeFactory(HttpStatusCode.OK), MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("https://example.com/orders");

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"),
            "order.csv", "text/csv", config, "{not-json", default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("HTTP delivery failed before receiving a response.");
    }

    [Fact]
    public async Task Dispatch_TimeoutSeconds_ReturnsTimeoutFailure()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromSeconds(2));
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(new HttpClient(handler));

        var dispatcher = new HttpDeliveryDispatcher(factory.Object, MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("https://example.com/orders");
        config.ConfigJson = JsonSerializer.Serialize(new { url = "https://example.com/orders", method = "POST", timeoutSeconds = 1 });
        var creds = JsonSerializer.Serialize(new { type = "none" });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"),
            "order.csv", "text/csv", config, creds, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("HTTP delivery timed out.");
    }

    [Fact]
    public async Task Dispatch_OAuth2_FetchesTokenThenSendsBearer()
    {
        string? deliveryAuth = null;
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/oauth/token"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"access_token\":\"tok-123\",\"expires_in\":3600}") };
            deliveryAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
        });
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(new HttpClient(handler));

        var dispatcher = new HttpDeliveryDispatcher(factory.Object, MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("https://supplier.example/orders");
        var creds = JsonSerializer.Serialize(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "https://supplier.example/oauth/token",
            clientId = "cid", clientSecret = "secret", scope = "orders.write",
        });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, creds, default);

        result.Success.Should().BeTrue();
        deliveryAuth.Should().Be("Bearer tok-123");
    }

    [Fact]
    public async Task Dispatch_OAuth2_TokenEndpoint401_FailsWithoutDelivering()
    {
        var deliveryCalled = false;
        var handler = new RoutingHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/oauth/token"))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") };
            deliveryCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
        });
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(new HttpClient(handler));

        var dispatcher = new HttpDeliveryDispatcher(factory.Object, MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
        var config = MakeConfig("https://supplier.example/orders");
        var creds = JsonSerializer.Serialize(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "https://supplier.example/oauth/token",
            clientId = "cid", clientSecret = "secret",
        });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, creds, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("OAuth token request failed: HTTP 401");
        deliveryCalled.Should().BeFalse();
    }
}

// ── Test helpers ──────────────────────────────────────────────────────────────

file sealed class FakeHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        });
}

file sealed class CapturingHttpMessageHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        capture(request);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("OK")
        });
    }
}

file sealed class DelayedHttpMessageHandler(TimeSpan delay) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("OK")
        };
    }
}

file sealed class RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(route(request));
}
