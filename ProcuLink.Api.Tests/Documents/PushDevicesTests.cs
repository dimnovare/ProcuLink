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
public class PushDevicesTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    // Real sizes, synthetic bytes: a 65-byte point starting 0x04 ("BP..." decodes to it) and a 16-byte secret.
    private static readonly string P256dh = "BPlan20" + new string('A', 80);
    private static readonly string Auth = "plan20auth" + new string('A', 12);

    private HttpClient ClientAs(string sub)
    {
        var client = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        client.DefaultRequestHeaders.Remove(RbacTestAuthHandler.SubHeader);
        client.DefaultRequestHeaders.Add(RbacTestAuthHandler.SubHeader, sub);
        return client;
    }

    private static Task<HttpResponseMessage> Remove(HttpClient client, string endpoint) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/push-devices") { Content = JsonContent.Create(new { endpoint }) });

    private async Task<List<PushDevice>> Devices()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<PushDevice>().IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    private async Task Clear()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.RemoveRange(db.Set<PushDevice>().IgnoreQueryFilters());
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_member_saves_and_removes_only_their_own_device()
    {
        await Clear();
        const string endpoint = "https://fcm.googleapis.com/fcm/send/plan20-a";
        var a = ClientAs("user_PD_A");
        var body = new { endpoint, p256dh = P256dh, auth = Auth };
        (await a.PostAsJsonAsync("/api/push-devices", body)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await a.PostAsJsonAsync("/api/push-devices", body)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var row = (await Devices()).Should().ContainSingle().Subject;
        row.ClerkUserId.Should().Be("user_PD_A");
        row.OrgId.Should().Be(DocumentEngineTestFactory.OrgA);
        row.LastCheckedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        (await Remove(ClientAs("user_PD_B"), endpoint)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Devices()).Should().HaveCount(1);
        (await Remove(a, endpoint)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Devices()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://fcm.googleapis.com/x", false)]
    [InlineData("https://169.254.169.254/latest", false)]
    [InlineData("https://fcm.googleapis.com.evil.example.test/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://fcm.googleapis.com:8443/x", false)]
    [InlineData("https://evil.example.test\uFF0F.push.apple.com/x", false)]
    [InlineData("https://anything@fcm.googleapis.com/x", false)]
    [InlineData("https://web.push.apple.com/plan20", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/plan20", true)]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=plan20", true)]
    public async Task Only_push_service_addresses_are_kept(string endpoint, bool allowed)
    {
        await Clear();
        var response = await ClientAs("user_PD_A").PostAsJsonAsync("/api/push-devices", new { endpoint, p256dh = P256dh, auth = Auth });

        response.StatusCode.Should().Be(allowed ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest);
        (await Devices()).Should().HaveCount(allowed ? 1 : 0);
        if (!allowed) (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("push_address_not_allowed");
    }
}
