using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ProcuLink.Api.Mcp;
using ProcuLink.Api.Tests.Documents;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Mcp;

public sealed class McpOAuthFixture : IDisposable
{
    public DocumentEngineTestFactory Factory { get; } = new() { McpOAuthKey = new RsaSecurityKey(RSA.Create(2048)) };
    public void Dispose() => Factory.Dispose();
}

[Collection("postgres-container")]
public class McpOAuthTests(McpOAuthFixture fixture) : IClassFixture<McpOAuthFixture>
{
    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;
    private DocumentEngineTestFactory Factory => fixture.Factory;

    private HttpClient Bearer(string token)
    {
        var http = Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    [Fact]
    public async Task OAuthToken_ResolvesWorkspaceFromOrgId()
    {
        Factory.ClientFor(OrgA).Dispose();
        Guid flowId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var flow = ProcuLink.Api.Services.Documents.FlowCreation.New(OrgA, "Synthetic flow", ProcuLink.Core.Documents.FlowFieldList.Empty, confirmed: true, ProcuLink.Core.Documents.FlowAutomation.Review);
            db.Flows.Add(flow);
            db.SaveChanges();
            flowId = flow.Id;
        }
        await using var client = await McpToolsTests.Connect(Bearer(Factory.McpToken(OrgA)));

        (await McpToolsTests.Call(client, "list_flows")).Text.Should().Contain(flowId.ToString());
        var (isError, added) = await McpToolsTests.Call(client, "add_document", new()
        {
            ["fileName"] = "synthetic.csv", ["contentBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("a,b\n1,2\n")), ["flowId"] = flowId,
        });
        isError.Should().BeFalse(added);
        var id = JsonDocument.Parse(added).RootElement.GetProperty("document").GetProperty("id").GetGuid();
        using var check = Factory.Services.CreateScope();
        var received = await check.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().AuditEvents
            .SingleAsync(e => e.EntityId == id && e.Action == "document.received");
        received.Payload!.RootElement.GetProperty("by").GetString().Should().Be("user_MCP_TEST");
    }

    [Fact]
    public async Task WrongAudience_Is401_WithResourceMetadataChallenge()
    {
        // A token for another resource, and no credential at all (what a client's first GET and Clerk's connection check send).
        foreach (var send in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => Bearer(Factory.McpToken(OrgA, audience: "https://elsewhere.example")).PostAsync(McpSetup.Path, new StringContent("{}", Encoding.UTF8, "application/json")),
                     () => Factory.CreateClient().GetAsync(McpSetup.Path),
                 })
        {
            var response = await send();
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var challenge = response.Headers.WwwAuthenticate.ToString();
            challenge.Should().Contain("Bearer").And.Contain("resource_metadata=").And.Contain(".well-known/oauth-protected-resource");
        }

        var metadata = await Factory.CreateClient().GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        metadata.GetProperty("resource").GetString().Should().Be(DocumentEngineTestFactory.McpResourceUrl);
        metadata.GetProperty("authorization_servers")[0].GetString()
            .Should().Be(Factory.Services.GetRequiredService<IConfiguration>()["Clerk:Authority"]!.TrimEnd('/'));
    }

    [Fact]
    public void OAuthToken_IsRefusedByTheAppApi()
    {
        // The app API never tries the OAuth scheme: its own scheme refuses a token with no azp (ClerkTokenValidationTests.NullAzp_IsRejected).
        static HttpContext At(string path, bool key = false)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = path;
            ctx.Request.Headers.Authorization = "Bearer synthetic";
            if (key) ctx.Request.Headers["X-ProcuLink-Key"] = "plk_synthetic";
            return ctx;
        }

        McpSetup.SelectScheme(At("/api/flows"), "Bearer", oauth: true).Should().Be("Bearer");
        McpSetup.SelectScheme(At("/mcp"), "Bearer", oauth: true).Should().Be(McpSetup.OAuthScheme);
        McpSetup.SelectScheme(At("/mcp", key: true), "Bearer", oauth: true).Should().Be("ApiKey");
    }
}
