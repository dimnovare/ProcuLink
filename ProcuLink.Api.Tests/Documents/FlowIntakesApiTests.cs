using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Constants;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class FlowIntakesApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private async Task<(HttpClient Http, Guid FlowId)> NewFlow()
    {
        var http = factory.ClientFor(Org);
        var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Intake flow"));
        return (http, (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task First_get_creates_one_flow_address_and_second_get_returns_it()
    {
        var (http, flowId) = await NewFlow();

        var first = await Body(await http.GetAsync($"/api/flows/{flowId}/intakes"));
        var second = await Body(await http.GetAsync($"/api/flows/{flowId}/intakes"));

        var addr = first.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "email").GetProperty("address").GetString();
        addr.Should().NotBeNullOrWhiteSpace();
        second.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "email").GetProperty("address").GetString().Should().Be(addr);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.OrgInboundAddresses.Count(a => a.FlowId == flowId && a.Kind == InboundAddressKind.Flow).Should().Be(1);

        // A new address revokes the old one, and the org's own Settings list never shows flow addresses.
        var emailId = first.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "email").GetProperty("id").GetGuid();
        var rotated = await Body(await http.PostAsync($"/api/flows/{flowId}/intakes/{emailId}/new-address", null));
        rotated.GetProperty("address").GetString().Should().NotBe(addr);
        db.OrgInboundAddresses.Count(a => a.FlowId == flowId && a.RevokedAt == null).Should().Be(1);
        (await http.DeleteAsync($"/api/flows/{flowId}/intakes/{emailId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Pickup_credentials_are_kept_secret()
    {
        var (http, flowId) = await NewFlow();
        var url = $"/api/flows/{flowId}/intakes";
        var created = await http.PostAsJsonAsync(url, new
        {
            kind = "sftp", config = new { host = "sftp.example.test", username = "u", junk = 1 }, credentials = new { password = "s3cret" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var text = await created.Content.ReadAsStringAsync();
        text.Should().NotContain("s3cret").And.Contain("\"hasCredentials\":true").And.NotContain("junk").And.Contain("\"port\":22");
        var id = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();

        string? Saved()
        {
            using var scope = factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowIntake>().Single(i => i.Id == id).EncryptedCredentials;
        }

        var before = Saved();
        var put = await http.PutAsJsonAsync($"{url}/{id}", new
        {
            kind = "sftp", config = new { host = "sftp.example.test", username = "u", directory = "/in" }, credentials = (object?)null,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        Saved().Should().Be(before);

        var moved = await http.PutAsJsonAsync($"{url}/{id}", new { kind = "sftp", config = new { host = "other.example.test", username = "u" } });
        moved.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await moved.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_intake").And.Contain("Enter the password again when you change the server or user name.");
        Saved().Should().Be(before);
        (await http.PutAsJsonAsync($"{url}/{id}", new
        {
            kind = "sftp", config = new { host = "other.example.test", username = "u" }, credentials = new { password = "new" },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await http.PostAsJsonAsync(url, new { kind = "sftp", config = new { host = "h" } })).StatusCode.Should().Be((HttpStatusCode)422);
        (await http.PostAsJsonAsync(url, new { kind = "email", config = new { } })).StatusCode.Should().Be((HttpStatusCode)422);
        (await http.PostAsJsonAsync(url, new { kind = "api", config = new { webhookUrl = "http://example.test/hook" } }))
            .StatusCode.Should().Be((HttpStatusCode)422);

        var member = factory.ClientFor(Org);
        member.DefaultRequestHeaders.Remove(RbacTestAuthHandler.RoleHeader);
        member.DefaultRequestHeaders.Add(RbacTestAuthHandler.RoleHeader, "org:member");
        var denied = await member.PostAsJsonAsync(url, new { kind = "api", config = new { } });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("requires_org_admin");
    }
}
