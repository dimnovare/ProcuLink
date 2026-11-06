using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;
using static ProcuLink.Api.Services.Documents.FlowDestinationService;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class FlowDestinationTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static FlowDestination Dest(string protocol) => new()
    {
        Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), Name = "d", Protocol = protocol,
        ConfigJson = protocol == "email" ? """{"toAddresses":["a@example.com"]}""" : """{"url":"https://example.com/in"}""",
    };

    private static JsonElement Json(SupplierDeliveryConfig c) => JsonDocument.Parse(c.ConfigJson).RootElement;
    private static string Subject(SupplierDeliveryConfig c) => Json(c).GetProperty("subjectTemplate").GetString()!;
    private static string Body(SupplierDeliveryConfig c) => Json(c).GetProperty("bodyTemplate").GetString()!;
    private static Dictionary<string, string> Headers(SupplierDeliveryConfig c) =>
        Json(c).GetProperty("headers").Deserialize<Dictionary<string, string>>()!;

    [Fact]
    public void Test_sends_are_marked_and_say_nothing_about_orders()
    {
        var email = ToDeliveryConfig(Dest("email"), "Supplier invoices", test: false);
        Subject(email).Should().Be("Supplier invoices — {fileName}");
        Subject(ToDeliveryConfig(Dest("email"), "Supplier invoices", test: true)).Should().StartWith("[TEST] ");
        (Subject(email) + Body(email)).Should().NotContainEquivalentOf("order");
        Headers(ToDeliveryConfig(Dest("http"), "x", test: true))["X-ProcuLink-Test"].Should().Be("true");
    }

    [Fact]
    public async Task Credentials_are_bound_to_their_destination_and_never_returned()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        var created = await http.PostAsJsonAsync("/api/destinations", new
        {
            name = "Accounting SFTP", protocol = "sftp",
            config = new { host = "sftp.example.com", remotePath = "/in" }, credentials = new { password = "p" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await created.Content.ReadAsStringAsync();
        json.Should().NotContain("\"p\"").And.Contain("\"hasCredentials\":true");

        using var scope = factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowDestination>().Single();
        var encryption = scope.ServiceProvider.GetRequiredService<DeliveryEncryptionService>();
        var act = () => encryption.Decrypt(row.EncryptedCredentials!,
            new CredentialScope(DocumentEngineTestFactory.OrgA, CredentialPurpose.FlowDestinationCredentials, Guid.NewGuid()));
        act.Should().Throw<CredentialUnbindableException>();

        var ftp = await http.PostAsJsonAsync("/api/destinations", new { name = "x", protocol = "ftp", config = new { } });
        ftp.StatusCode.Should().Be((HttpStatusCode)422);
    }
}
