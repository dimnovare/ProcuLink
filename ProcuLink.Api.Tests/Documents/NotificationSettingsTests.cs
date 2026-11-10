using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Jobs;
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
    private sealed class CapturingSender(bool fail = false) : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];
        public bool CanDeliver => true;
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
        var org = Guid.NewGuid();
        var since = DateTime.UtcNow.AddHours(-1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Invoices" };
        FlowDocument Doc(string status) => new() { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Status = status };
        var failedDoc = Doc("failed");
        var reviewDoc = Doc("needs_review");
        db.Add(flow);
        db.AddRange(failedDoc, reviewDoc);
        db.AddRange(
            new AuditEvent { Id = Guid.NewGuid(), OrgId = org, EntityType = "FlowDocument", EntityId = failedDoc.Id, Action = "document.read_failed", CreatedAt = DateTime.UtcNow.AddMinutes(-10) },
            new AuditEvent { Id = Guid.NewGuid(), OrgId = org, EntityType = "FlowDocument", EntityId = reviewDoc.Id, Action = "document.read", CreatedAt = DateTime.UtcNow.AddMinutes(-10) });
        NotificationSetting Setting(string user, bool on) => new()
        {
            Id = Guid.NewGuid(), OrgId = org, ClerkUserId = user, Email = $"{user}@example.test", OnReview = on, OnFailure = on, LastSentAt = since,
        };
        var a = Setting("user_D_A", true);
        db.AddRange(a, Setting("user_D_B", false));
        await db.SaveChangesAsync();

        DocumentNotificationJob Job(IEmailSender sender) => new(db, sender, new ConfigurationBuilder().Build(), NullLogger<DocumentNotificationJob>.Instance);
        var sender = new CapturingSender();
        await Job(sender).RunAsync(CancellationToken.None);
        await Job(sender).RunAsync(CancellationToken.None);

        sender.Sent.Should().ContainSingle().Which.To.Should().Be("user_D_A@example.test");
        sender.Sent[0].Subject.Should().Be("ProcuLink: 2 documents need you");
        sender.Sent[0].Body.Should().Contain("Invoices: 1 waiting for review, 1 couldn't be read or sent");

        a.LastSentAt = since;
        await db.SaveChangesAsync();
        await Job(new CapturingSender(fail: true)).RunAsync(CancellationToken.None);
        a.LastSentAt.Should().Be(since);
    }
}
