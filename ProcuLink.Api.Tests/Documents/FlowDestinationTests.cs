using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
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
    [Fact]
    public async Task The_list_says_what_uses_a_destination_and_what_was_sent_there_last()
    {
        var t = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        FlowDestination Destination(Guid org, string name) => new()
        {
            Id = Guid.NewGuid(), OrgId = org, Name = name, Protocol = "http", ConfigJson = """{"url":"https://example.test/in"}""", CreatedAt = t, UpdatedAt = t,
        };
        var (tested, failed, idle, theirs) = (Destination(OrgA, "Tested only"), Destination(OrgA, "Failed after a test"),
            Destination(OrgA, "Never used"), Destination(OrgB, "Theirs"));
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = OrgA, Name = "Supplier invoices", CreatedAt = t, UpdatedAt = t };
        var output = new FlowOutput { Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flow.Id, Name = "Invoice CSV", Format = "csv", DestinationId = failed.Id, CreatedAt = t, UpdatedAt = t };
        var document = new FlowDocument { Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flow.Id, FileName = "invoice.pdf", ContentType = "application/pdf", CreatedAt = t, UpdatedAt = t };
        AuditEvent Test(FlowDestination d, bool success, DateTime at) => new()
        {
            Id = Guid.NewGuid(), OrgId = d.OrgId, EntityType = "FlowDestination", EntityId = d.Id, Action = "destination.tested", CreatedAt = at,
            Payload = JsonSerializer.SerializeToDocument(new { success }),
        };
        FlowDelivery Delivery(string status, DateTime at) => new()
        {
            Id = Guid.NewGuid(), OrgId = OrgA, DocumentId = document.Id, OutputId = output.Id, OutputName = output.Name, DestinationId = failed.Id,
            Protocol = "http", Status = status, DispatchKey = Guid.NewGuid(), CreatedAt = at, UpdatedAt = at,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.AddRange(tested, failed, idle, theirs, flow, output, document);
            db.AddRange(Test(tested, false, t.AddMinutes(1)), Test(tested, true, t.AddMinutes(2)), Test(failed, true, t.AddMinutes(1)), Test(theirs, true, t.AddMinutes(5)));
            db.AddRange(Delivery(FlowDeliveryStatus.Delivered, t.AddMinutes(3)), Delivery(FlowDeliveryStatus.DeadLetter, t.AddMinutes(4)), Delivery(FlowDeliveryStatus.Failed, t.AddMinutes(9)));
            await db.SaveChangesAsync();
        }

        var rows = (await factory.ClientFor(DocumentEngineTestFactory.OrgA).GetFromJsonAsync<JsonElement>("/api/destinations")).EnumerateArray().ToList();
        JsonElement Row(FlowDestination d) => rows.Single(r => r.GetProperty("id").GetGuid() == d.Id);
        (string, bool, DateTime) Last(FlowDestination d) => (Row(d).GetProperty("lastSend").GetProperty("kind").GetString()!,
            Row(d).GetProperty("lastSend").GetProperty("ok").GetBoolean(), Row(d).GetProperty("lastSend").GetProperty("at").GetDateTime());

        rows.Should().NotContain(r => r.GetProperty("id").GetGuid() == theirs.Id);
        Last(tested).Should().Be(("test", true, t.AddMinutes(2)));                       // the newer test
        Last(failed).Should().Be(("document", false, t.AddMinutes(4)));                  // the newest finished send; the retrying one is not finished
        Row(idle).GetProperty("lastSend").ValueKind.Should().Be(JsonValueKind.Null);
        Row(idle).GetProperty("usedBy").GetArrayLength().Should().Be(0);
        var use = Row(failed).GetProperty("usedBy").EnumerateArray().Single();
        (use.GetProperty("flowName").GetString(), use.GetProperty("outputName").GetString()).Should().Be(("Supplier invoices", "Invoice CSV"));
    }
}
