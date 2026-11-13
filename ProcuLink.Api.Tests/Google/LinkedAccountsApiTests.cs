using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Tests.Documents;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Google;
using Xunit;
using static ProcuLink.Api.Tests.Documents.DocumentEngineTestFactory;

namespace ProcuLink.Api.Tests.Google;

[Collection("postgres-container")]
public class LinkedAccountsApiTests : IClassFixture<DocumentEngineTestFactory>
{
    private readonly DocumentEngineTestFactory _factory;

    public LinkedAccountsApiTests(DocumentEngineTestFactory factory)
    {
        _factory = factory;
        _factory.Google.Grant = new GoogleGrant("at-1", "rt-1", $"openid email {GoogleOAuthClient.DriveFileScope}");
        _factory.Google.Exchanged = null;
    }

    private static async Task<string> StartAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/linked-accounts/google/start", new { origin = "http://localhost:8082", returnTo = "/flows/1?tab=outputs" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
    }

    private static string Param(string url, string name) => QueryHelpers.ParseQuery(new Uri(url).Query)[name].ToString();

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, string authorizeUrl) =>
        client.PostAsJsonAsync("/api/linked-accounts/google/complete", new { code = "code-1", state = Param(authorizeUrl, "state") });

    private int AccountsIn(Guid org)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().LinkedAccounts.Count(a => a.OrgId == org);
    }

    [Fact]
    public async Task Connect_RoundTrip_SavesEncryptedAccount()
    {
        var client = _factory.ClientFor(OrgA);
        var url = await StartAsync(client);
        url.Should().Contain("client_id=").And.Contain("redirect_uri=http%3A%2F%2Flocalhost%3A8082%2Fsettings%2Fgoogle-callback")
            .And.Contain("code_challenge_method=S256").And.Contain("access_type=offline");

        var res = await CompleteAsync(client, url);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnTo").GetString().Should().Be("/flows/1?tab=outputs");
        // PKCE: the verifier sealed in the state is the one whose hash Google was shown.
        var (_, redirectUri, verifier) = _factory.Google.Exchanged!.Value;
        redirectUri.Should().Be("http://localhost:8082/settings/google-callback");
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).Should().Be(Param(url, "code_challenge"));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/linked-accounts");
        list.GetProperty("available").GetBoolean().Should().BeTrue();
        var account = list.GetProperty("accounts").EnumerateArray().Should().ContainSingle().Subject;
        account.GetProperty("email").GetString().Should().Be("user@example.com");
        account.GetProperty("status").GetString().Should().Be("active");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var row = await db.LinkedAccounts.SingleAsync(a => a.OrgId == OrgA);
        row.EncryptedTokens.Should().NotContain("rt-");
        LinkedAccountTokens.ReadRefreshToken(scope.ServiceProvider.GetRequiredService<DeliveryEncryptionService>(), row).Should().Be("rt-1");
        (await db.AuditEvents.CountAsync(e => e.OrgId == OrgA && e.Action == "linked_account.connected")).Should().Be(1);
    }

    [Fact]
    public async Task Complete_StateFromOtherOrg_IsRefused()
    {
        var url = await StartAsync(_factory.ClientFor(OrgA));

        var res = await CompleteAsync(_factory.ClientFor(OrgB), url);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("invalid_state");
        _factory.Google.Exchanged.Should().BeNull("the code must never reach Google on a foreign state");
        AccountsIn(OrgB).Should().Be(0);
    }

    [Fact]
    public async Task Complete_WithoutDriveScope_IsRefused()
    {
        _factory.Google.Grant = new GoogleGrant("at-1", "rt-1", "openid email");
        var client = _factory.ClientFor(OrgB);

        var res = await CompleteAsync(client, await StartAsync(client));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("google_scope_missing");
        AccountsIn(OrgB).Should().Be(0);
    }
}
