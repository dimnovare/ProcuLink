using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class NotificationMembersJobTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private const string Key = "sk_test_fake";

    private sealed class FakeClerk : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (!request.RequestUri!.AbsolutePath.Contains("org_DOC_a000")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            const string json = "{\"data\":[{\"public_user_data\":{\"user_id\":\"u1\",\"identifier\":\"u1@example.test\"}},{\"public_user_data\":{\"user_id\":\"u2\",\"identifier\":\"+3725550000\"}}],\"total_count\":2}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://api.clerk.com/v1/") };
    }

    private async Task Run(FakeClerk handler, string? key)
    {
        using var scope = factory.Services.CreateScope();
        var config = new ConfigurationBuilder().AddInMemoryCollection(key is null ? [] : new Dictionary<string, string?> { ["Clerk:SecretKey"] = key }).Build();
        await new NotificationMembersJob(new Factory(handler), config, scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>(),
            scope.ServiceProvider.GetRequiredService<DocumentEngineGate>(), NullLogger<NotificationMembersJob>.Instance).RunAsync(CancellationToken.None);
    }

    private async Task<List<NotificationSetting>> Rows()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<NotificationSetting>().AsNoTracking().ToListAsync();
    }

    private async Task Seed()
    {
        factory.ClientFor(DocumentEngineTestFactory.OrgA); factory.ClientFor(DocumentEngineTestFactory.OrgB); // seeds the two organisations
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.RemoveRange(db.Set<NotificationSetting>());
        NotificationSetting S(Guid org, string user, string? email, bool review) =>
            new() { Id = Guid.NewGuid(), OrgId = org, ClerkUserId = user, Email = email, OnReview = review };
        db.AddRange(S(DocumentEngineTestFactory.OrgA, "u2", "keep@example.test", true), S(DocumentEngineTestFactory.OrgA, "u9", null, false),
            S(DocumentEngineTestFactory.OrgB, "u1", null, false));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sync_adds_fills_and_removes_members_and_skips_an_org_whose_fetch_failed()
    {
        await Seed();
        var handler = new FakeClerk();
        await Run(handler, Key);

        var rows = await Rows();
        var a = rows.Where(r => r.OrgId == DocumentEngineTestFactory.OrgA).ToList();
        a.Select(r => r.ClerkUserId).Order().Should().Equal("u1", "u2");
        var u1 = a.Single(r => r.ClerkUserId == "u1");
        u1.Email.Should().Be("u1@example.test");
        (u1.OnReview || u1.OnFailure).Should().BeFalse();
        var u2 = a.Single(r => r.ClerkUserId == "u2");
        u2.Email.Should().Be("keep@example.test");
        u2.OnReview.Should().BeTrue();
        rows.Should().ContainSingle(r => r.OrgId == DocumentEngineTestFactory.OrgB && r.ClerkUserId == "u1");

        var first = handler.Requests.First(r => r.RequestUri!.AbsolutePath.Contains("org_DOC_a000"));
        first.Headers.Authorization!.ToString().Should().Be($"Bearer {Key}");
        first.RequestUri!.Query.Should().Contain("limit=100&offset=0");
    }

    [Fact]
    public async Task Without_a_Clerk_key_nothing_changes_and_no_call_is_made()
    {
        await Seed();
        var handler = new FakeClerk();
        await Run(handler, null);

        handler.Requests.Should().BeEmpty();
        (await Rows()).Should().HaveCount(3);
    }
}
