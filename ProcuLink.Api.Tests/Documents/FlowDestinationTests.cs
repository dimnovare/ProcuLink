using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Constants;
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
        ConfigJson = protocol == DeliveryProtocolConstants.Email ? """{"toAddresses":["a@example.com"]}""" : """{"url":"https://example.com/in"}""",
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
    public void Email_keeps_the_users_subject_and_message_and_falls_back_when_blank()
    {
        var d = Dest("email");
        d.ConfigJson = """{"toAddresses":["a@example.com"],"subjectTemplate":"Invoice {fileName}","bodyTemplate":"Hi","attachmentFileName":"x.csv"}""";
        var kept = ToDeliveryConfig(d, "Flow", test: true);
        Subject(kept).Should().Be("[TEST] Invoice {fileName}");
        Body(kept).Should().Be("Hi");
        Json(kept).TryGetProperty("attachmentFileName", out _).Should().BeFalse();

        d.ConfigJson = """{"toAddresses":["a@example.com"],"subjectTemplate":"  ","bodyTemplate":""}""";
        var blank = ToDeliveryConfig(d, "Flow", test: false);
        Subject(blank).Should().Be("Flow — {fileName}");
        Body(blank).Should().Be("Attached: {fileName}. Sent by ProcuLink.");
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
        var row = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowDestination>().Single(d => d.Name == "Accounting SFTP");
        var encryption = scope.ServiceProvider.GetRequiredService<DeliveryEncryptionService>();
        var act = () => encryption.Decrypt(row.EncryptedCredentials!,
            new CredentialScope(DocumentEngineTestFactory.OrgA, CredentialPurpose.FlowDestinationCredentials, Guid.NewGuid()));
        act.Should().Throw<CredentialUnbindableException>();

        var ftp = await http.PostAsJsonAsync("/api/destinations", new { name = "x", protocol = "ftp", config = new { } });
        ftp.StatusCode.Should().Be((HttpStatusCode)422);

        var s3 = await http.PostAsJsonAsync("/api/destinations", new
        {
            name = "Bucket", protocol = "s3", credentials = new { secretKey = "s" },
            config = new { bucket = "b", region = "auto", accessKeyId = "k", serviceUrl = "http://169.254.169.254" },
        });
        s3.StatusCode.Should().Be((HttpStatusCode)422);
        (await s3.Content.ReadAsStringAsync()).Should().Contain("url_requires_tls");
    }

    private const string HeaderConfig = """{"url":"https://supplier.example/in","headers":{"Authorization":"Bearer t0ps3cret"}}""";

    /// <summary>The destination save path is the only caller of the delivery-config rules: each refusal is a 422 and writes nothing.</summary>
    [Theory]
    [InlineData("""{"url":"http://supplier.example/in"}""", null)]
    [InlineData(HeaderConfig, null)]
    [InlineData("""{"url":"https://supplier.example/in"}""",
        """{"type":"oauth2_client_credentials","tokenUrl":"http://auth.supplier.example/token","clientId":"c","clientSecret":"s"}""")]
    public async Task A_destination_the_delivery_rules_refuse_is_rejected_and_not_saved(string config, string? credentials)
    {
        var name = $"refused-{Guid.NewGuid():N}";
        var response = await factory.ClientFor(DocumentEngineTestFactory.OrgA).PostAsJsonAsync("/api/destinations", new
        {
            name, protocol = "http", config = JsonDocument.Parse(config).RootElement,
            credentials = credentials is null ? (JsonElement?)null : JsonDocument.Parse(credentials).RootElement,
        });

        response.StatusCode.Should().Be((HttpStatusCode)422);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"error\":\"invalid_destination\"");
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowDestination>().Any(d => d.Name == name).Should().BeFalse();
    }

    /// <summary>A credential header stored before the rule existed survives an unchanged round-trip: the save hands the stored config to the rule.</summary>
    [Fact]
    public async Task An_unchanged_stored_credential_header_does_not_block_an_edit()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Set<FlowDestination>().Add(new FlowDestination
            {
                Id = id, OrgId = DocumentEngineTestFactory.OrgA, Name = "Webhook", Protocol = "http", ConfigJson = HeaderConfig,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var put = await http.PutAsJsonAsync($"/api/destinations/{id}",
            new { name = "Webhook (renamed)", protocol = "http", config = JsonDocument.Parse(HeaderConfig).RootElement });

        put.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
