using System.Net.Http.Json;
using FluentAssertions;
using ProcuLink.Api.Tests.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Auth;

[Collection("postgres-container")]
public class ApiKeyCanSendTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private sealed record KeyRow(Guid Id, string Label, bool CanSend);

    [Fact]
    public async Task CreateKey_CanSendDefaultsFalse_AndIsListed()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        (await http.PostAsJsonAsync("/api/api-keys", new { label = "Synthetic A" })).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("/api/api-keys", new { label = "Synthetic B", canSend = true })).EnsureSuccessStatusCode();

        var keys = await http.GetFromJsonAsync<List<KeyRow>>("/api/api-keys");
        keys!.Single(k => k.Label == "Synthetic A").CanSend.Should().BeFalse();
        keys!.Single(k => k.Label == "Synthetic B").CanSend.Should().BeTrue();
    }
}
