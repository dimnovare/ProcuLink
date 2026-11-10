using System.Net;
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
}
