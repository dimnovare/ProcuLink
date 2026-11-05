using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class DocumentsApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private async Task<Guid> SeedDocument(string status, string? reason = null)
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
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
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
