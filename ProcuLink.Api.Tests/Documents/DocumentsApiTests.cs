using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class DocumentsApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private async Task<Guid> SeedDocument(string status, string? reason = null, Action<Flow, FlowDocument>? arrange = null)
    {
        factory.ClientFor(Org);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList(
                [new FlowField("total_gross", "Total", FieldTypes.Money, null, Required: true)])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf",
            Status = status, StatusReason = reason, ReadJson = DocumentJson.Serialize(new ReadResult("Synthetic invoice")),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        arrange?.Invoke(flow, doc);
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    /// <summary>A flow with one unconfirmed field <c>total</c>, and the document's original file in storage.</summary>
    private Action<Flow, FlowDocument> Source(string fileName, string text, string source = "upload", string? sender = null) => (f, d) =>
    {
        var key = $"teach/{d.Id:N}";
        factory.Storage.Setup(s => s.DownloadAsync(key, It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(text)));
        (f.FieldsConfirmed, f.FieldsJson) = (false, DocumentJson.Serialize(new FlowFieldList([new FlowField("total", "Total", FieldTypes.Money, null, false)])));
        (d.FileName, d.SourceFileKey, d.Source, d.SourceRef) = (fileName, key, source, sender);
    };

    private Flow FlowOf(Guid docId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        return db.Flows.Single(f => f.Id == db.FlowDocuments.Single(d => d.Id == docId).FlowId);
    }

    private static List<FieldHint> Hints(Flow flow) => DocumentJson.Deserialize<List<FieldHint>>(flow.HintsJson);

    [Fact]
    public async Task Teach_a_cell_sets_the_value_and_saves_a_hint()
    {
        var http = factory.ClientFor(Org);
        var id = await SeedDocument(DocumentStatus.NeedsReview, arrange: Source("orders.csv", "Field,Value\r\nTotal,99.50\r\n", IntakeKind.Email, "a@shop.example"));

        var res = await http.PostAsJsonAsync($"/api/documents/{id}/teach", new TeachRequest("total", null, null, null, "cell:r2c2", null));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<DocumentDto>(DocumentJson.Options))!;
        var total = Assert.Single(dto.Read.Fields, f => f.Key == "total");
        Assert.Equal(("99.50", "cell:r2c2"), (total.Value, total.Location?.Address));
        Assert.Equal("shop.example", dto.SenderLabel);
        var hint = Assert.Single(Hints(FlowOf(id)));
        Assert.Equal(("total", "email:shop.example", "Total", "99.50"), (hint.FieldKey, hint.SenderKey, hint.Near, hint.Example));

        await http.PostAsJsonAsync($"/api/documents/{id}/teach", new TeachRequest("total", null, null, null, "cell:r2c2", null));
        Assert.Single(Hints(FlowOf(id)));

        // The cap: the oldest of 200 other hints makes room for the new one.
        var capped = FieldHints.Upsert(Enumerable.Range(0, FieldHints.MaxPerFlow).Select(i => hint with { FieldKey = $"f{i}" }).ToList(), hint);
        Assert.Equal((FieldHints.MaxPerFlow, "f1", hint), (capped.Count, capped[0].FieldKey, capped[^1]));
    }

    [Fact]
    public async Task Teach_a_new_field_adds_it_to_the_flow()
    {
        var http = factory.ClientFor(Org);
        var id = await SeedDocument(DocumentStatus.NeedsReview, arrange: Source("orders.csv", "Field,Value\r\nTotal,99.50\r\n"));

        var res = await http.PostAsJsonAsync($"/api/documents/{id}/teach", new TeachRequest(null, "Delivery note", null, null, "cell:r2c2", null));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<DocumentDto>(DocumentJson.Options))!;
        Assert.Equal("99.50", Assert.Single(dto.Read.Fields, f => f.Key == "delivery_note").Value);
        var flow = FlowOf(id);
        Assert.Contains(DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson).Fields, f => f.Key == "delivery_note");
        Assert.True(flow.FieldsConfirmed);
    }

    [Fact]
    public async Task Teach_refusals()
    {
        var http = factory.ClientFor(Org);
        var scan = await SeedDocument(DocumentStatus.NeedsReview, arrange: Source("scan.png", "not a real image"));
        var sent = await SeedDocument(DocumentStatus.Sent);
        var box = new Box(0.1, 0.2, 0.3, 0.05);

        var blank = await http.PostAsJsonAsync($"/api/documents/{scan}/teach", new TeachRequest("total", null, 1, box, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("no_text_in_area", await blank.Content.ReadAsStringAsync());
        Assert.Equal("[]", FlowOf(scan).HintsJson);

        var typed = await http.PostAsJsonAsync($"/api/documents/{scan}/teach", new TeachRequest("total", null, 1, box, null, "INV-9"));
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);

        var locked = await http.PostAsJsonAsync($"/api/documents/{sent}/teach", new TeachRequest("total_gross", null, null, null, "cell:r2c2", null));
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Contains("document_sent", await locked.Content.ReadAsStringAsync());

        var unknown = await http.PostAsJsonAsync($"/api/documents/{scan}/teach", new TeachRequest("nope", null, 1, box, null, "x"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown_target", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Edit_value_reruns_checks_and_writes_history()
    {
        var http = factory.ClientFor(Org);
        var id = await SeedDocument(DocumentStatus.NeedsReview);

        var res = await http.PatchAsJsonAsync($"/api/documents/{id}/values",
            new EditValuesRequest([new FieldEdit("total_gross", "2,380.00")], null, null, null));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<DocumentDto>(DocumentJson.Options))!;
        Assert.Equal(DocumentStatus.Ready, dto.Summary.Status);
        var field = Assert.Single(dto.Read.Fields, f => f.Key == "total_gross");
        Assert.True(field.Edited);
        Assert.Equal(1.0, field.Confidence);

        var history = (await http.GetFromJsonAsync<List<HistoryEntryDto>>($"/api/documents/{id}/history", DocumentJson.Options))!;
        var entry = Assert.Single(history, h => h.Action == "document.value_edited");
        Assert.Equal(JsonValueKind.Null, entry.Payload.GetProperty("old").ValueKind);
        Assert.Equal("2,380.00", entry.Payload.GetProperty("new").GetString());
    }

    [Fact]
    public async Task Edit_unknown_key_400_and_reading_document_409()
    {
        var http = factory.ClientFor(Org);
        var review = await SeedDocument(DocumentStatus.NeedsReview);
        var reading = await SeedDocument(DocumentStatus.Reading);
        var held = await SeedDocument(DocumentStatus.NeedsReview, "duplicate_file");

        var unknown = await http.PatchAsJsonAsync($"/api/documents/{review}/values",
            new EditValuesRequest([new FieldEdit("no_such_field", "x")], null, null, null));
        var locked = await http.PatchAsJsonAsync($"/api/documents/{reading}/values",
            new EditValuesRequest([new FieldEdit("total_gross", "1")], null, null, null));

        var heldRes = await http.PatchAsJsonAsync($"/api/documents/{held}/values",
            new EditValuesRequest([new FieldEdit("total_gross", "1")], null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown_target", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, heldRes.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Contains("not_editable", await locked.Content.ReadAsStringAsync());
    }
}
