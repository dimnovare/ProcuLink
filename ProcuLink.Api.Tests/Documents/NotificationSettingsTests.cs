using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class NotificationSettingsTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private HttpClient ClientAs(string sub, string? email = null)
    {
        var client = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        client.DefaultRequestHeaders.Remove(RbacTestAuthHandler.SubHeader);
        client.DefaultRequestHeaders.Add(RbacTestAuthHandler.SubHeader, sub);
        if (email is not null) client.DefaultRequestHeaders.Add(RbacTestAuthHandler.EmailHeader, email);
        return client;
    }

    [Fact]
    public async Task A_member_sees_and_changes_only_their_own_settings()
    {
        var a = ClientAs("user_NS_A", "a@example.test");
        (await a.PutAsJsonAsync("/api/notification-settings", new { onReview = false, onFailure = true })).EnsureSuccessStatusCode();

        var mine = await a.GetFromJsonAsync<JsonElement>("/api/notification-settings");
        mine.GetProperty("email").GetString().Should().Be("a@example.test");
        mine.GetProperty("onFailure").GetBoolean().Should().BeTrue();

        var theirs = await ClientAs("user_NS_B", "b@example.test").GetFromJsonAsync<JsonElement>("/api/notification-settings");
        theirs.GetProperty("email").GetString().Should().Be("b@example.test");
        theirs.GetProperty("onReview").GetBoolean().Should().BeFalse();
        theirs.GetProperty("onFailure").GetBoolean().Should().BeFalse();

        // No email claim and none stored: a switch could never send, so it is refused and nothing is saved.
        var noEmail = await ClientAs("user_NS_C").PutAsJsonAsync("/api/notification-settings", new { onReview = true, onFailure = false });
        noEmail.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await noEmail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("email_unknown");

        using var verify = factory.Services.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<ProcuLinkDbContext>()
            .Set<NotificationSetting>().SingleAsync();
        row.ClerkUserId.Should().Be("user_NS_A");
        row.LastSentAt.Should().NotBeNull();
    }
    private sealed class CapturingSender(bool fail = false, bool canDeliver = true) : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];
        public bool CanDeliver => canDeliver;
        public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
        {
            if (fail) throw new InvalidOperationException("down");
            Sent.Add((to, subject, body));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Digest_emails_each_opted_in_member_once()
    {
        var orgA = DocumentEngineTestFactory.OrgA;
        var orgB = DocumentEngineTestFactory.OrgB;
        var off = Guid.NewGuid(); // the engine is not enabled here
        var since = DateTime.UtcNow.AddHours(-1);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            void Doc(Guid org, string flowName, string status, string action)
            {
                var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = flowName };
                var doc = new FlowDocument { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Status = status };
                db.AddRange(flow, doc);
                db.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(), OrgId = org, EntityType = "FlowDocument", EntityId = doc.Id, Action = action, CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                });
            }
            Doc(orgA, "Invoices", "failed", "document.read_failed");
            Doc(orgA, "Invoices", "needs_review", "document.read");
            Doc(orgB, "Receipts", "failed", "document.read_failed");
            Doc(off, "Ignored", "failed", "document.read_failed");
            NotificationSetting Setting(string user, Guid org, bool review, bool failure) => new()
            {
                Id = Guid.NewGuid(), OrgId = org, ClerkUserId = user, Email = $"{user}@example.test", OnReview = review, OnFailure = failure, LastSentAt = since,
            };
            db.AddRange(
                Setting("user_D_A", orgA, true, true), Setting("user_D_B", orgB, true, false),
                Setting("user_D_C", orgB, false, true), Setting("user_D_D", off, true, true));
            await db.SaveChangesAsync();
        }

        async Task Run(IEmailSender sender)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            await new DocumentNotificationJob(db, sender, scope.ServiceProvider.GetRequiredService<DocumentEngineGate>(),
                new ConfigurationBuilder().Build(), NullLogger<DocumentNotificationJob>.Instance).RunAsync(CancellationToken.None);
        }
        async Task<DateTime?> LastSent(string user)
        {
            using var scope = factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<NotificationSetting>().SingleAsync(s => s.ClerkUserId == user)).LastSentAt;
        }

        // A sender that cannot deliver, or throws, leaves every window open.
        await Run(new CapturingSender(canDeliver: false));
        await Run(new CapturingSender(fail: true));
        (await LastSent("user_D_A")).Should().Be(since);

        var sender = new CapturingSender();
        await Run(sender);
        await Run(sender);

        sender.Sent.Select(m => m.To).Should().BeEquivalentTo("user_D_A@example.test", "user_D_C@example.test");
        var a = sender.Sent.Single(m => m.To == "user_D_A@example.test");
        a.Subject.Should().Be("ProcuLink: 2 documents need you");
        a.Body.Should().Contain("Invoices: 1 waiting for review, 1 couldn't be read or sent");
        var c = sender.Sent.Single(m => m.To == "user_D_C@example.test");
        c.Subject.Should().Be("ProcuLink: 1 document needs you");
        c.Body.Should().Contain("Receipts: 1 couldn't be read or sent");
        (await LastSent("user_D_B")).Should().BeAfter(since); // nothing to report: the window still moves on
        (await LastSent("user_D_D")).Should().Be(since); // org without the engine: untouched
    }
}
