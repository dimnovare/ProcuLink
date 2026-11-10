using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class NotificationSettingsTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private HttpClient ClientAs(string sub)
    {
        var client = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        client.DefaultRequestHeaders.Remove(RbacTestAuthHandler.SubHeader);
        client.DefaultRequestHeaders.Add(RbacTestAuthHandler.SubHeader, sub);
        return client;
    }

    [Fact]
    public async Task A_member_sees_and_changes_only_their_own_settings()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Users.AddRange(
                new AppUser { Id = Guid.NewGuid(), ClerkUserId = "user_NS_A", Email = "a@example.test", CreatedAt = DateTime.UtcNow },
                new AppUser { Id = Guid.NewGuid(), ClerkUserId = "user_NS_B", Email = "b@example.test", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var a = ClientAs("user_NS_A");
        (await a.PutAsJsonAsync("/api/notification-settings", new { onReview = false, onFailure = true })).EnsureSuccessStatusCode();

        var mine = await a.GetFromJsonAsync<JsonElement>("/api/notification-settings");
        mine.GetProperty("email").GetString().Should().Be("a@example.test");
        mine.GetProperty("onFailure").GetBoolean().Should().BeTrue();

        var theirs = await ClientAs("user_NS_B").GetFromJsonAsync<JsonElement>("/api/notification-settings");
        theirs.GetProperty("email").GetString().Should().Be("b@example.test");
        theirs.GetProperty("onReview").GetBoolean().Should().BeFalse();
        theirs.GetProperty("onFailure").GetBoolean().Should().BeFalse();

        using var verify = factory.Services.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<ProcuLinkDbContext>()
            .Set<NotificationSetting>().SingleAsync();
        row.LastSentAt.Should().NotBeNull();
    }
}
