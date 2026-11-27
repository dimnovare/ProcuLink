using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.Google;
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

        // The default message is in the document's language, unless the destination chose one.
        Body(ToDeliveryConfig(d, "Flow", test: false, documentLanguage: "de")).Should().Be("Im Anhang: {fileName}. Gesendet über ProcuLink.");
        d.ConfigJson = """{"toAddresses":["a@example.com"],"language":"fr"}""";
        Body(ToDeliveryConfig(d, "Flow", test: false, documentLanguage: "de")).Should().Be("Pièce jointe : {fileName}. Envoyé par ProcuLink.");
    }

    /// <summary>Engine on for OrgA, native connectors off, Google configured.</summary>
    private static LinkedAccountAvailability Availability()
    {
        var google = Options.Create(new GoogleOptions { ClientId = "c", ClientSecret = "s" });
        return new(new DocumentEngineGate(Options.Create(new DocumentEngineOptions { EnabledOrgIds = [OrgA.ToString()] })), [new GoogleOAuthClient(null!, google)], google);
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
            Availability(), null!);
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

    private sealed class FakeLinks : ProcuLink.Core.Services.Microsoft.IMicrosoftDriveLinks
    {
        public int Calls;
        public Task<ProcuLink.Core.Services.Microsoft.DriveItemRef> ResolveAsync(Guid orgId, Guid accountId, string link, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ProcuLink.Core.Services.Microsoft.DriveItemRef("drive-1", "item-1", "Orders"));
        }
    }

    [Fact]
    public async Task Microsoft_destination_resolves_its_link_and_refuses_other_accounts()
    {
        await using var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid Seed(string provider)
        {
            var account = new LinkedAccount { Id = Guid.NewGuid(), OrgId = OrgA, Provider = provider, Subject = "sub-" + provider, Email = "user@example.com" };
            db.LinkedAccounts.Add(account);
            return account.Id;
        }
        var (ms, google, outlook) = (Seed("microsoft"), Seed("google"), Seed("outlook"));
        await db.SaveChangesAsync();
        var files = new ProcuLink.Infrastructure.Services.Microsoft.MicrosoftOAuthClient(ProcuLink.Infrastructure.Services.Microsoft.MicrosoftProfile.Files, null!,
            Options.Create(new ProcuLink.Infrastructure.Services.Microsoft.MicrosoftOptions { ClientId = "c", ClientSecret = "s" }));
        var gate = new DocumentEngineGate(Options.Create(new DocumentEngineOptions { EnabledOrgIds = [OrgA.ToString()], NativeConnectorOrgIds = [OrgA.ToString()] }));
        var links = new FakeLinks();
        var service = new FlowDestinationService(db, null!, [], new LinkedAccountAvailability(gate, [files], Options.Create(new GoogleOptions())), links);
        DestinationRequest Req(object config) =>
            new("OneDrive", "microsoft_onedrive", JsonSerializer.SerializeToElement(config), null);

        var saved = await service.SaveAsync(OrgA, null, Req(new { accountId = ms, link = "https://contoso.sharepoint.com/:f:/s/team/EaBc1", driveId = "forged" }), default);
        var stored = JsonDocument.Parse(saved.ConfigJson).RootElement;
        stored.GetProperty("driveId").GetString().Should().Be("drive-1");
        stored.GetProperty("itemName").GetString().Should().Be("Orders");
        stored.TryGetProperty("link", out _).Should().BeFalse();

        var kept = await service.SaveAsync(OrgA, saved.Id, Req(new { accountId = ms }), default);
        JsonDocument.Parse(kept.ConfigJson).RootElement.GetProperty("itemId").GetString().Should().Be("item-1");

        foreach (var wrong in new[] { google, outlook }) // the mailbox account must not save files either
            (await FluentActions.Awaiting(() => service.SaveAsync(OrgA, saved.Id, Req(new { accountId = wrong, link = "https://1drv.ms/f/s!x" }), default))
                .Should().ThrowAsync<DestinationException>()).Which.Message.Should().Be("Choose a connected Microsoft account.");
        links.Calls.Should().Be(1);
    }

    private sealed class SheetsFake : HttpMessageHandler, IHttpClientFactory, ILinkedAccountTokens
    {
        public readonly List<(HttpMethod Method, string Url)> Requests = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        public Task<LinkedAccessToken> GetAccessTokenAsync(Guid orgId, Guid accountId, string provider, CancellationToken ct) => Task.FromResult(new LinkedAccessToken("at-1", null));
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
            Availability(), null!);

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

    [Fact]
    public async Task B2Brouter_destination_is_gated_and_never_echoes_the_key()
    {
        object Body(object? credentials) => new { name = "E-invoices", protocol = "b2brouter", config = new { account = "acc-1", sandbox = false }, credentials };
        var off = await factory.ClientFor(OrgB).PostAsJsonAsync("/api/destinations", Body(new { apiKey = "test_k" })); // engine on, Peppol off
        off.StatusCode.Should().Be((HttpStatusCode)422);
        (await off.Content.ReadAsStringAsync()).Should().Contain("unsupported_destination");

        var http = factory.ClientFor(OrgA);
        var created = await http.PostAsJsonAsync("/api/destinations", Body(new { apiKey = "test_k" }));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await created.Content.ReadAsStringAsync();
        json.Should().NotContain("test_k").And.Contain("\"hasCredentials\":true").And.Contain("\"sandbox\":true");

        (await (await http.PostAsJsonAsync("/api/destinations", Body(null))).Content.ReadAsStringAsync()).Should().Contain("invalid_destination");
        var live = await http.PostAsJsonAsync("/api/destinations", Body(new { apiKey = "live_k" }));
        live.StatusCode.Should().Be(HttpStatusCode.Created);
        (await live.Content.ReadAsStringAsync()).Should().Contain("\"sandbox\":false"); // a live key saves, marked live by the server
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
        var retested = Destination(OrgA, "Test failed after a send");
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = OrgA, Name = "Supplier invoices", CreatedAt = t, UpdatedAt = t };
        var output = new FlowOutput { Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flow.Id, Name = "Invoice CSV", Format = "csv", DestinationId = failed.Id, CreatedAt = t, UpdatedAt = t };
        FlowOutput OutputTo(FlowDestination d, string name) => new() { Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flow.Id, Name = name, Format = "csv", DestinationId = d.Id, CreatedAt = t, UpdatedAt = t };
        var document = new FlowDocument { Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flow.Id, FileName = "invoice.pdf", ContentType = "application/pdf", CreatedAt = t, UpdatedAt = t };
        AuditEvent Test(FlowDestination d, bool success, DateTime at) => new()
        {
            Id = Guid.NewGuid(), OrgId = d.OrgId, EntityType = "FlowDestination", EntityId = d.Id, Action = "destination.tested", CreatedAt = at,
            Payload = JsonSerializer.SerializeToDocument(new { success }),
        };
        FlowDelivery Delivery(string status, DateTime at, FlowDestination? to = null) => new()
        {
            Id = Guid.NewGuid(), OrgId = OrgA, DocumentId = document.Id, OutputId = output.Id, OutputName = output.Name, DestinationId = (to ?? failed).Id,
            Protocol = "http", Status = status, DispatchKey = Guid.NewGuid(), CreatedAt = at, UpdatedAt = at,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.AddRange(tested, failed, idle, theirs, retested, flow, output, document,
                OutputTo(retested, "Remittance CSV"), OutputTo(retested, "Credit notes CSV"));   // inserted against name order
            db.AddRange(Test(tested, false, t.AddMinutes(1)), Test(tested, true, t.AddMinutes(2)), Test(failed, true, t.AddMinutes(1)), Test(theirs, true, t.AddMinutes(5)));
            db.AddRange(Delivery(FlowDeliveryStatus.Delivered, t.AddMinutes(3)), Delivery(FlowDeliveryStatus.DeadLetter, t.AddMinutes(4)), Delivery(FlowDeliveryStatus.Failed, t.AddMinutes(9)));
            db.AddRange(Delivery(FlowDeliveryStatus.Delivered, t.AddMinutes(3), retested), Test(retested, false, t.AddMinutes(6)));
            await db.SaveChangesAsync();
        }

        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        var rows = (await http.GetFromJsonAsync<JsonElement>("/api/destinations")).EnumerateArray().ToList();
        JsonElement Row(FlowDestination d) => rows.Single(r => r.GetProperty("id").GetGuid() == d.Id);
        (string, bool, DateTime) Last(FlowDestination d) => (Row(d).GetProperty("lastSend").GetProperty("kind").GetString()!,
            Row(d).GetProperty("lastSend").GetProperty("ok").GetBoolean(), Row(d).GetProperty("lastSend").GetProperty("at").GetDateTime());

        rows.Should().NotContain(r => r.GetProperty("id").GetGuid() == theirs.Id);
        Last(tested).Should().Be(("test", true, t.AddMinutes(2)));                       // the newer test
        Last(failed).Should().Be(("document", false, t.AddMinutes(4)));                  // the newest finished send; the retrying one is not finished
        Last(retested).Should().Be(("test", false, t.AddMinutes(6)));                    // a later failed test beats an older delivery
        Row(idle).GetProperty("lastSend").ValueKind.Should().Be(JsonValueKind.Null);
        Row(idle).GetProperty("usedBy").GetArrayLength().Should().Be(0);
        var use = Row(failed).GetProperty("usedBy").EnumerateArray().Single();
        (use.GetProperty("flowName").GetString(), use.GetProperty("outputName").GetString()).Should().Be(("Supplier invoices", "Invoice CSV"));
        Row(retested).GetProperty("usedBy").EnumerateArray().Select(u => u.GetProperty("outputName").GetString())
            .Should().Equal("Credit notes CSV", "Remittance CSV");                         // a steady order: by output name

        var updated = await (await http.PutAsJsonAsync($"/api/destinations/{failed.Id}",
            new { name = failed.Name, protocol = "http", config = JsonDocument.Parse(failed.ConfigJson).RootElement })).Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("usedBy").EnumerateArray().Single().GetProperty("outputId").GetGuid().Should().Be(output.Id);   // an update answers what is true

        var refused = await http.DeleteAsync($"/api/destinations/{failed.Id}");          // refused because the EDITABLE output uses it
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("destination_in_use");
        using var check = factory.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowDestination>().Any(d => d.Id == failed.Id).Should().BeTrue();
    }
}
