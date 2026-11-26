using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Google;
using ProcuLink.Infrastructure.Services.Microsoft;

namespace ProcuLink.Infrastructure.Tests.Services.Google;

public class LinkedAccountTokensTests
{
    /// <summary>Stands in for a provider's token endpoint, behind that provider's named client only.</summary>
    private sealed class FakeGoogle(HttpStatusCode status, string json, string clientName = GoogleOAuthClient.HttpClientName) : HttpMessageHandler, IHttpClientFactory
    {
        public string? Form;
        public readonly List<Dictionary<string, string>> Forms = [];

        public HttpClient CreateClient(string name) =>
            name == clientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Form = await request.Content!.ReadAsStringAsync(ct);
            var parsed = System.Web.HttpUtility.ParseQueryString(Form);
            Forms.Add(parsed.AllKeys.ToDictionary(k => k!, k => parsed[k]!));
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly DeliveryEncryptionService Encryption = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
        .Build());

    private static async Task<(LinkedAccountTokens Tokens, ProcuLinkDbContext Db, LinkedAccount Account)> Build(FakeGoogle google, string provider = "google")
    {
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var encryption = Encryption;
        var account = new LinkedAccount
        {
            Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), Provider = provider, Subject = "sub-1", Email = "user@example.com",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        LinkedAccountTokens.SealRefreshToken(encryption, account, "rt-1");
        db.LinkedAccounts.Add(account);
        await db.SaveChangesAsync();

        ProcuLink.Core.Services.Google.ILinkedAccountOAuth client = provider == "google"
            ? new GoogleOAuthClient(google, Options.Create(new GoogleOptions { ClientId = "cid", ClientSecret = "secret" }))
            : new MicrosoftOAuthClient(MicrosoftProfile.Mailbox, google, Options.Create(new MicrosoftOptions { ClientId = "cid", ClientSecret = "secret" }));
        return (new LinkedAccountTokens(db, encryption, [client], NullLogger<LinkedAccountTokens>.Instance), db, account);
    }

    [Fact]
    public async Task Refresh_ReturnsToken()
    {
        var google = new FakeGoogle(HttpStatusCode.OK, """{"access_token":"at-1","expires_in":3599}""");
        var (tokens, _, account) = await Build(google);

        var result = await tokens.GetAccessTokenAsync(account.OrgId, account.Id, "google", default);

        result.Token.Should().Be("at-1");
        result.Failure.Should().BeNull();
        google.Form.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=rt-1");
    }

    [Fact]
    public async Task InvalidGrant_MarksReconnect_NotRetryable()
    {
        var (tokens, db, account) = await Build(new FakeGoogle(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var result = await tokens.GetAccessTokenAsync(account.OrgId, account.Id, "google", default);

        result.Token.Should().BeNull();
        result.Failure!.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        result.Failure.ErrorMessage.Should().Be("Google access was removed. Reconnect the Google account.");
        result.Failure.FailureKind.Should().Be(FlowDeliveryFailureKind.Setup); // only the customer can reconnect
        db.ChangeTracker.Clear();
        (await db.LinkedAccounts.SingleAsync()).Status.Should().Be("reconnect");
    }

    [Fact]
    public async Task Microsoft_Refresh_ResealsRotatedToken()
    {
        var ms = new FakeGoogle(HttpStatusCode.OK, """{"access_token":"at-1","refresh_token":"rt-new"}""", MicrosoftOAuthClient.HttpClientName);
        var (tokens, db, account) = await Build(ms, "outlook");

        (await tokens.GetAccessTokenAsync(account.OrgId, account.Id, "outlook", default)).Token.Should().Be("at-1");
        ms.Forms[0]["grant_type"].Should().Be("refresh_token");
        ms.Forms[0]["refresh_token"].Should().Be("rt-1");
        ms.Forms[0]["scope"].Should().Contain("Mail.ReadWrite");
        db.ChangeTracker.Clear();
        LinkedAccountTokens.ReadRefreshToken(Encryption, await db.LinkedAccounts.SingleAsync()).Should().Be("rt-new");

        await tokens.GetAccessTokenAsync(account.OrgId, account.Id, "outlook", default);
        ms.Forms[1]["refresh_token"].Should().Be("rt-new");
    }

    [Fact]
    public async Task Microsoft_InteractionRequired_MarksReconnect()
    {
        var (tokens, db, account) = await Build(
            new FakeGoogle(HttpStatusCode.BadRequest, """{"error":"interaction_required"}""", MicrosoftOAuthClient.HttpClientName), "outlook");

        var result = await tokens.GetAccessTokenAsync(account.OrgId, account.Id, "outlook", default);

        result.Failure!.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        result.Failure.ErrorMessage.Should().Be("Microsoft access was removed. Reconnect the Microsoft account.");
        db.ChangeTracker.Clear();
        (await db.LinkedAccounts.SingleAsync()).Status.Should().Be("reconnect");
    }
}
