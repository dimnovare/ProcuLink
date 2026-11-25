using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>Plan 33: a KSeF Production send needs a person who confirmed exactly this file, and is never issued twice. Synthetic NIPs only.</summary>
[Collection("postgres-container")]
public class KsefSendTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;
    private const string Issued = "1111111111-20261126-ABCDEF123456-7A";

    // Two lines at 23 % and one at 8 %: net 250.00, VAT 50.00, gross 300.00.
    private static ReadResult Read(string firstLine = "Usługa A")
    {
        ReadField F(string label, string value, string meaning) => new(FieldKey.Normalise(label), label, value, "text", meaning, 1, null, false, true, false);
        (string Label, string Meaning)[] cols = [("Description", FieldMeanings.LineDescription), ("Qty", FieldMeanings.LineQuantity),
            ("Price", FieldMeanings.LineUnitPrice), ("Amount", FieldMeanings.LineAmount), ("VAT rate", FieldMeanings.LineTaxRate)];
        string[][] rows = [[firstLine, "2", "50.00", "100.00", "23"], ["Usługa B", "1", "100.00", "100.00", "23"], ["Towar C", "1", "50.00", "50.00", "8"]];
        return new ReadResult("Invoice",
            [F("Invoice number", "FV/2026/11/0001", FieldMeanings.DocumentNumber), F("Issue date", "20.11.2026", FieldMeanings.IssueDate),
             F("Currency", "PLN", FieldMeanings.Currency), F("Seller", "Przykładowa Spółka Testowa sp. z o.o.", FieldMeanings.SenderName),
             F("Seller NIP", "1111111111", FieldMeanings.SenderVatId), F("Seller country", "PL", FieldMeanings.SenderCountry),
             F("Seller street", "ul. Testowa 1", FieldMeanings.SenderStreet), F("Seller postcode", "00-001", FieldMeanings.SenderPostcode),
             F("Seller city", "Warszawa", FieldMeanings.SenderCity), F("Buyer", "Fikcyjny Odbiorca S.A.", FieldMeanings.ReceiverName),
             F("Buyer NIP", "9999999999", FieldMeanings.ReceiverVatId), F("Net", "250.00", FieldMeanings.TotalNet),
             F("VAT", "50.00", FieldMeanings.TotalTax), F("Gross", "300.00", FieldMeanings.TotalGross)],
            [new ReadTable("lines", "Lines", [.. cols.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning))],
                [.. rows.Select(r => new ReadRow(cols.Select((c, i) => (c, i)).ToDictionary(x => FieldKey.Normalise(x.c.Label), x => new ReadCell(r[x.i], 1, null, false, false))))], true)]);
    }

    private static ProcuLinkDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

    /// <summary>A flow whose KSeF invoice file output goes to a KSeF destination in <paramref name="environment"/>, and one read document.</summary>
    private async Task<(Guid DocId, Guid OutputId)> Seed(string environment)
    {
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([])), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var destination = new FlowDestination
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "KSeF", Protocol = "ksef",
            ConfigJson = "{\"environment\":\"" + environment + "\",\"nip\":\"1111111111\"}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var output = new FlowOutput
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, Name = "KSeF invoice file", Format = FlowOutputFormats.Xml,
            Standard = "ksef-fa-3", TableKey = "lines", TemplateJson = "{}", DestinationId = destination.Id, CreatedAt = DateTime.UtcNow,
        };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "fv.pdf", ContentType = "application/pdf", Status = DocumentStatus.Ready,
            ReadJson = DocumentJson.Serialize(Read()), ReadAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        using var scope = factory.Services.CreateScope();
        var db = Db(scope);
        db.AddRange(flow, destination, output, doc);
        await db.SaveChangesAsync();
        return (doc.Id, output.Id);
    }

    private Task<HttpResponseMessage> Send(Guid docId, string? confirm = null) =>
        confirm is null
            ? factory.ClientFor(Org).PostAsync($"/api/documents/{docId}/send", null)
            : factory.ClientFor(Org).PostAsJsonAsync($"/api/documents/{docId}/send", new { ksefConfirm = confirm });

    private async Task<List<FlowDelivery>> Deliveries(Guid docId)
    {
        using var scope = factory.Services.CreateScope();
        return await Db(scope).Set<FlowDelivery>().AsNoTracking().Where(d => d.DocumentId == docId).ToListAsync();
    }

    [Fact]
    public async Task Production_destination_starts_on_test_and_switches_only_with_acceptance()
    {
        var http = factory.ClientFor(Org);
        static async Task<string> Detail(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        static object Body(object config) => new { name = "KSeF", protocol = "ksef", config, credentials = new { token = "t" } };

        var refused = await http.PostAsJsonAsync("/api/destinations", Body(new { environment = "production", nip = "1111111111" }));
        (await Detail(refused)).Should().Be("A new KSeF destination starts on Test or Demo. Switch it to Production once you have checked a test invoice.");

        var created = await http.PostAsJsonAsync("/api/destinations", Body(new { environment = "test", nip = "1111111111" }));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var unticked = await http.PutAsJsonAsync($"/api/destinations/{id}", Body(new { environment = "production", nip = "1111111111" }));
        (await Detail(unticked)).Should().Be("To switch to Production, confirm that your company is the issuer of every invoice sent from here.");
        (await http.PutAsJsonAsync($"/api/destinations/{id}", Body(new { environment = "production", nip = "1111111111", acceptedIssuerTerms = true })))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = factory.Services.CreateScope())
        {
            var config = JsonDocument.Parse((await Db(scope).Set<FlowDestination>().SingleAsync(d => d.Id == id)).ConfigJson).RootElement;
            config.GetProperty("productionConfirmedBy").GetString().Should().Be("user_DOC_TEST");
            config.TryGetProperty("acceptedIssuerTerms", out _).Should().BeFalse();
        }
        (await Detail(await http.PutAsJsonAsync($"/api/destinations/{id}", Body(new { environment = "prod", nip = "1111111111" }))))
            .Should().Be("Choose the KSeF environment: Test, Demo or Production.");
    }

    [Fact]
    public async Task Production_send_needs_a_person_who_saw_this_file()
    {
        var (docId, _) = await Seed("production");
        using (var scope = factory.Services.CreateScope())
        {
            var doc = await Db(scope).FlowDocuments.SingleAsync(d => d.Id == docId);
            (await scope.ServiceProvider.GetRequiredService<DocumentSender>().SendAsync(doc, SendMode.Automatic, null, CancellationToken.None)).Error
                .Should().Be("ksef_confirm");
        }
        using (var scope = factory.Services.CreateScope())
        {
            var held = await Db(scope).FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == docId);
            (held.Status, held.StatusReason).Should().Be((DocumentStatus.NeedsReview, "ksef_confirm"));
        }
        (await Deliveries(docId)).Should().BeEmpty();

        var asked = await Send(docId);
        asked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var preview = await asked.Content.ReadFromJsonAsync<JsonElement>();
        preview.GetProperty("error").GetString().Should().Be("ksef_confirm");
        var summary = preview.GetProperty("summaries")[0];
        (summary.GetProperty("sellerNip").GetString(), summary.GetProperty("gross").GetString()).Should().Be(("1111111111", "300.00"));
        var oldConfirm = preview.GetProperty("confirm").GetString()!;
        oldConfirm.Should().MatchRegex("^[0-9a-f]{64}$");

        // Someone edits a value after the person checked: the old confirmation no longer fits the file.
        using (var scope = factory.Services.CreateScope())
        {
            var db = Db(scope);
            var doc = await db.FlowDocuments.SingleAsync(d => d.Id == docId);
            doc.ReadJson = DocumentJson.Serialize(Read(firstLine: "Usługa A (zmieniona)"));
            await db.SaveChangesAsync();
        }
        var changed = await Send(docId, oldConfirm);
        (changed.StatusCode, (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString())
            .Should().Be((HttpStatusCode.Conflict, "ksef_changed"));
        (await Deliveries(docId)).Should().BeEmpty();

        var fresh = (await (await Send(docId)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirm").GetString()!;
        fresh.Should().NotBe(oldConfirm);
        (await Send(docId, fresh)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Deliveries(docId)).Should().ContainSingle(d => d.Protocol == "ksef" && d.Status == FlowDeliveryStatus.Queued);
        using (var scope = factory.Services.CreateScope())
            (await Db(scope).AuditEvents.CountAsync(a => a.EntityId == docId && a.Action == "document.ksef_issue_confirmed")).Should().Be(1);
    }

    [Theory]
    [InlineData("production", "ksef_already_issued")]
    [InlineData("test", "ksef_confirm")]   // a TEST issue does not block production
    public async Task A_document_issued_in_production_is_never_issued_again(string issuedIn, string expected)
    {
        var (docId, outputId) = await Seed("production");
        using (var scope = factory.Services.CreateScope())
        {
            var db = Db(scope);
            db.Set<FlowDelivery>().Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = docId, OutputId = outputId, OutputName = "KSeF invoice file", Protocol = "ksef",
                Status = FlowDeliveryStatus.Delivered, RemoteReference = Issued, RemoteEnvironment = issuedIn, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var answer = await (await Send(docId)).Content.ReadFromJsonAsync<JsonElement>();

        answer.GetProperty("error").GetString().Should().Be(expected);
        if (expected == "ksef_already_issued") answer.GetProperty("ksefNumber").GetString().Should().Be(Issued);
        (await Deliveries(docId)).Should().ContainSingle();
    }
}
