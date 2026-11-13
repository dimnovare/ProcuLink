using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Google;
using ProcuLink.Core.Services.Google;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ProcuLink.Api.Services.Documents.FlowDestinationService;
using static ProcuLink.Api.Tests.Documents.DocumentEngineTestFactory;

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

    private async Task<Guid> SeedAccountAsync(Guid org)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var id = Guid.NewGuid();
        db.LinkedAccounts.Add(new LinkedAccount { Id = id, OrgId = org, Subject = "sub-" + id, Email = "user@example.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Google_destination_with_the_flag_off_is_unsupported()
    {
        await using var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new FlowDestinationService(db, null!, [],
            new DocumentEngineGate(Options.Create(new DocumentEngineOptions { EnabledOrgIds = [OrgA.ToString()] })),
            Options.Create(new GoogleOptions { ClientId = "c", ClientSecret = "s" }));
        using var config = JsonDocument.Parse("""{"accountId":"x","folderId":"f"}""");

        var act = () => service.SaveAsync(OrgA, null, new DestinationRequest("d", "google_drive", config.RootElement, null), default);

        (await act.Should().ThrowAsync<DestinationException>()).Which.Code.Should().Be("unsupported_destination");
    }

    [Fact]
    public async Task Google_drive_destination_needs_an_own_org_account_and_pins_it()
    {
        var http = factory.ClientFor(OrgA);
        var other = await SeedAccountAsync(OrgB);
        var own = await SeedAccountAsync(OrgA);

        var foreign = await http.PostAsJsonAsync("/api/destinations", new { name = "Drive", protocol = "google_drive", config = new { accountId = other, folderId = "fold-1" } });
        foreign.StatusCode.Should().Be((HttpStatusCode)422);
        (await foreign.Content.ReadAsStringAsync()).Should().Contain("invalid_destination");

        var created = await http.PostAsJsonAsync("/api/destinations", new
        {
            name = "Drive", protocol = "google_drive", config = new { accountId = own, folderId = "fold-1", folderName = "Invoices" }, credentials = new { password = "ignored" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadAsStringAsync()).Should().Contain("\"hasCredentials\":false");

        var inUse = await http.DeleteAsync($"/api/linked-accounts/{own}");
        inUse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await inUse.Content.ReadAsStringAsync()).Should().Contain("account_in_use");
    }

    private sealed class SheetsFake : HttpMessageHandler, IHttpClientFactory, ILinkedAccountTokens
    {
        public readonly List<(HttpMethod Method, string Url)> Requests = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        public Task<LinkedAccessToken> GetGoogleAccessTokenAsync(Guid orgId, Guid accountId, CancellationToken ct) => Task.FromResult(new LinkedAccessToken("at-1", null));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task Sheets_test_without_a_payload_sends_a_csv_and_only_reads_the_sheet()
    {
        await using var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var fake = new SheetsFake();
        var d = new FlowDestination
        {
            Id = Guid.NewGuid(), OrgId = OrgA, Name = "Sheet", Protocol = DeliveryProtocolConstants.GoogleSheets,
            ConfigJson = $$"""{"accountId":"{{Guid.NewGuid()}}","spreadsheetId":"sh-1"}""",
        };
        db.Set<FlowDestination>().Add(d);
        await db.SaveChangesAsync();
        var service = new FlowDestinationService(db, null!,
            [new GoogleSheetsDeliveryDispatcher(fake, fake, NullLogger<GoogleSheetsDeliveryDispatcher>.Instance)],
            new DocumentEngineGate(Options.Create(new DocumentEngineOptions { EnabledOrgIds = [OrgA.ToString()] })),
            Options.Create(new GoogleOptions { ClientId = "c", ClientSecret = "s" }));

        var result = await service.TestAsync(OrgA, d.Id, default);

        result!.Success.Should().BeTrue();
        fake.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
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
        var row = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowDestination>().Single(d => d.Protocol == "sftp");
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
}
