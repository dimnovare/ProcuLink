using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Tests.Documents;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure;
using Xunit;
using static ProcuLink.Api.Tests.Documents.DocumentEngineTestFactory;

namespace ProcuLink.Api.Tests.Google;

/// <summary>Its own host (and database), so its accounts never meet the Google tests' single-account assertions.</summary>
[Collection("postgres-container")]
public class MicrosoftLinkedAccountsApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static string Param(string url, string name) => QueryHelpers.ParseQuery(new Uri(url).Query)[name].ToString();

    private static async Task<string> StartAsync(HttpClient client, string provider)
    {
        var res = await client.PostAsJsonAsync($"/api/linked-accounts/{provider}/start", new { origin = "http://localhost:8082" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, string url) =>
        client.PostAsJsonAsync("/api/linked-accounts/outlook/complete", new { code = "code-1", state = Param(url, "state") });

    [Fact]
    public async Task Outlook_Connect_RoundTrip()
    {
        var client = factory.ClientFor(OrgA);
        var url = await StartAsync(client, "outlook");
        url.Should().StartWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?")
            .And.Contain("redirect_uri=http%3A%2F%2Flocalhost%3A8082%2Fsettings%2Foutlook-callback")
            .And.Contain("code_challenge_method=S256").And.Contain("response_mode=query").And.Contain("prompt=select_account")
            .And.Contain("Mail.ReadWrite").And.NotContain("Files.").And.NotContain("prompt=consent");

        // A state minted for Google never completes a Microsoft connect: the protector purposes differ.
        var foreign = await CompleteAsync(client, await StartAsync(client, "google"));
        foreign.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await foreign.Content.ReadAsStringAsync()).Should().Contain("invalid_state");

        // Mail.Read is not Mail.ReadWrite: whole scope tokens only.
        factory.Outlook.Grant = new OAuthGrant("at-1", "rt-1", "https://graph.microsoft.com/Mail.Read https://graph.microsoft.com/User.Read");
        var narrow = await CompleteAsync(client, url);
        narrow.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await narrow.Content.ReadAsStringAsync()).Should().Contain("outlook_scope_missing");

        factory.Outlook.Grant = new OAuthGrant("at-1", "rt-1", "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/User.Read");
        factory.Outlook.User = new OAuthUser("ms-sub-1", "user@example.com");
        (await CompleteAsync(client, await StartAsync(client, "outlook"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/linked-accounts");
        list.GetProperty("microsoftAvailable").GetBoolean().Should().BeTrue();
        var account = list.GetProperty("accounts").EnumerateArray().Should().ContainSingle().Subject;
        account.GetProperty("provider").GetString().Should().Be("outlook");
        account.GetProperty("status").GetString().Should().Be("active");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        (await db.LinkedAccounts.SingleAsync(a => a.OrgId == OrgA)).EncryptedTokens.Should().NotContain("rt-");
        var audit = await db.AuditEvents.SingleAsync(e => e.OrgId == OrgA && e.Action == "linked_account.connected");
        audit.Payload!.RootElement.GetProperty("provider").GetString().Should().Be("outlook");
    }
}
