using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// B6 gate for the shared <see cref="HttpAuthApplier"/> — the ONE auth implementation used by
/// BOTH the delivery dispatcher and the catalog HTTP pull. Exercises all five auth methods
/// (none/apikey/bearer/basic/oauth2_client_credentials), and the OAuth token-fetch failure
/// path returning a SAFE message. The delivery dispatcher's own tests stay green unmodified —
/// proving the extraction was behaviour-preserving.
/// </summary>
public class HttpAuthApplierTests
{
    private static OutboundRequestGuard PermissiveGuard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static HttpAuthApplier NewApplier(OutboundRequestGuard? guard = null) =>
        new(guard ?? PermissiveGuard(), NullLogger<HttpAuthApplier>.Instance);

    private static JsonElement Creds(object o) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(o));

    [Fact]
    public async Task None_AppliesNoAuth()
    {
        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        var error = await applier.ApplyAsync(req, Creds(new { type = "none" }), new HttpClient(), default);

        error.Should().BeNull();
        req.Headers.Authorization.Should().BeNull();
        req.Headers.Should().NotContain(h => h.Key == "Authorization");
    }

    [Fact]
    public async Task Undefined_Creds_AppliesNoAuth()
    {
        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        var error = await applier.ApplyAsync(req, default, new HttpClient(), default);

        error.Should().BeNull();
        req.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task ApiKey_AddsCustomHeader()
    {
        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        var error = await applier.ApplyAsync(
            req, Creds(new { type = "apikey", header = "X-Api-Key", value = "sk-secret" }), new HttpClient(), default);

        error.Should().BeNull();
        req.Headers.TryGetValues("X-Api-Key", out var vals).Should().BeTrue();
        vals!.Single().Should().Be("sk-secret");
    }

    [Fact]
    public async Task Bearer_SetsAuthorizationHeader()
    {
        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        var error = await applier.ApplyAsync(
            req, Creds(new { type = "bearer", token = "tok-abc" }), new HttpClient(), default);

        error.Should().BeNull();
        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization!.Parameter.Should().Be("tok-abc");
    }

    [Fact]
    public async Task Basic_SetsBase64Authorization()
    {
        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        var error = await applier.ApplyAsync(
            req, Creds(new { type = "basic", username = "user", password = "pass" }), new HttpClient(), default);

        error.Should().BeNull();
        req.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization!.Parameter!))
            .Should().Be("user:pass");
    }

    [Fact]
    public async Task OAuth2_FetchesTokenViaSuppliedClient_ThenSetsBearer()
    {
        var tokenRequested = false;
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/oauth/token"))
            {
                tokenRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"access_token\":\"tok-123\",\"expires_in\":3600}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
        });

        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://supplier.example/catalog");
        var creds = Creds(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "https://supplier.example/oauth/token",
            clientId = "cid", clientSecret = "secret", scope = "catalog.read",
        });

        var error = await applier.ApplyAsync(req, creds, new HttpClient(handler), default);

        error.Should().BeNull();
        tokenRequested.Should().BeTrue();
        req.Headers.Authorization.Should().Be(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "tok-123"));
    }

    [Fact]
    public async Task OAuth2_TokenEndpoint401_ReturnsSafeMessage_NoBodyLeak()
    {
        var handler = new RoutingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("client secret abc123 is wrong") });

        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://supplier.example/catalog");
        var creds = Creds(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "https://supplier.example/oauth/token",
            clientId = "cid", clientSecret = "secret",
        });

        var error = await applier.ApplyAsync(req, creds, new HttpClient(handler), default);

        error.Should().NotBeNull();
        error.Should().Contain("OAuth token request failed: HTTP 401");
        error.Should().NotContain("abc123", "the token-endpoint response body must never leak");
        error.Should().NotContain("secret");
        req.Headers.Authorization.Should().BeNull("no bearer is applied when the token fetch fails");
    }

    [Fact]
    public async Task OAuth2_MissingTokenUrl_ReturnsSafeMessage()
    {
        var applier = NewApplier();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://supplier.example/catalog");
        var creds = Creds(new { type = "oauth2_client_credentials", clientId = "cid", clientSecret = "secret" });

        var error = await applier.ApplyAsync(req, creds, new HttpClient(), default);

        error.Should().NotBeNull();
        error.Should().NotContain("cid");
        error.Should().NotContain("secret");
    }

    [Fact]
    public async Task OAuth2_TokenUrlBlockedByGuard_ReturnsSafeMessage_NoTokenFetch()
    {
        // A strict (non-permissive) guard must block a private token URL before any fetch.
        var strictConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "false",
            })
            .Build();
        var guard = new OutboundRequestGuard(strictConfig, NullLogger<OutboundRequestGuard>.Instance);

        var fetched = false;
        var handler = new RoutingHandler(_ => { fetched = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        var applier = NewApplier(guard);
        var req = new HttpRequestMessage(HttpMethod.Get, "https://supplier.example/catalog");
        var creds = Creds(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "http://169.254.169.254/token", // cloud-metadata endpoint
            clientId = "cid", clientSecret = "secret",
        });

        var error = await applier.ApplyAsync(req, creds, new HttpClient(handler), default);

        error.Should().NotBeNull();
        error.Should().Contain("blocked");
        fetched.Should().BeFalse("the SSRF guard must reject the token URL before any request is sent");
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }
}
