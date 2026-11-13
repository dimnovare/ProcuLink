using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Infrastructure.Tests.Services.Google;

public class LinkedAccountTokensTests
{
    /// <summary>Stands in for Google's token endpoint, behind the "google" named client only.</summary>
    private sealed class FakeGoogle(HttpStatusCode status, string json) : HttpMessageHandler, IHttpClientFactory
    {
        public string? Form;

        public HttpClient CreateClient(string name) =>
            name == GoogleOAuthClient.HttpClientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Form = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(LinkedAccountTokens Tokens, ProcuLinkDbContext Db, LinkedAccount Account)> Build(FakeGoogle google)
    {
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var encryption = new DeliveryEncryptionService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build());
        var account = new LinkedAccount
        {
            Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), Subject = "sub-1", Email = "user@example.com",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        LinkedAccountTokens.SealRefreshToken(encryption, account, "rt-1");
        db.LinkedAccounts.Add(account);
        await db.SaveChangesAsync();

        var client = new GoogleOAuthClient(google, Options.Create(new GoogleOptions { ClientId = "cid", ClientSecret = "secret" }));
        return (new LinkedAccountTokens(db, encryption, client, NullLogger<LinkedAccountTokens>.Instance), db, account);
    }

    [Fact]
    public async Task Refresh_ReturnsToken()
    {
        var google = new FakeGoogle(HttpStatusCode.OK, """{"access_token":"at-1","expires_in":3599}""");
        var (tokens, _, account) = await Build(google);

        var result = await tokens.GetGoogleAccessTokenAsync(account.OrgId, account.Id, default);

        result.Token.Should().Be("at-1");
        result.Failure.Should().BeNull();
        google.Form.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=rt-1");
    }

    [Fact]
    public async Task InvalidGrant_MarksReconnect_NotRetryable()
    {
        var (tokens, db, account) = await Build(new FakeGoogle(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var result = await tokens.GetGoogleAccessTokenAsync(account.OrgId, account.Id, default);

        result.Token.Should().BeNull();
        result.Failure!.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        result.Failure.ErrorMessage.Should().Be("Google access was removed. Reconnect the Google account.");
        db.ChangeTracker.Clear();
        (await db.LinkedAccounts.SingleAsync()).Status.Should().Be("reconnect");
    }
}
