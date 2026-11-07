using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class FlowOutputTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private static FlowField Field(string key, string label) => new(key, label, FieldTypes.Text, null, Required: false);

    private async Task<(Guid FlowId, Guid DocumentId)> Seed(bool confirmed)
    {
        factory.ClientFor(Org);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = confirmed,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList(
                [Field("invoice_no", "Invoice number"), Field("total", "Total")],
                [new FlowTable("items", "Items", [Field("sku", "SKU")])])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var read = new ReadResult("Invoice",
            [new ReadField("invoice_no", "Invoice number", "INV-7", FieldTypes.Text, null, 1, null, false, true, false),
             new ReadField("total", "Total", "1.234,50", FieldTypes.Text, null, 1, null, false, true, false)],
            [new ReadTable("items", "Items", [new ReadColumn("sku", "SKU", FieldTypes.Text, null)],
                [new ReadRow(new Dictionary<string, ReadCell> { ["sku"] = new("A1", 1, null, false, false) })], true)]);
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf",
            Status = DocumentStatus.Ready, ReadJson = DocumentJson.Serialize(read), ReadAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return (flow.Id, doc.Id);
    }

    private static IEnumerable<OutputNode> Leaves(OutputNode n) =>
        n.NodeType is OutputNodeType.Field ? [n] : n.Children.SelectMany(Leaves);

    [Fact]
    public async Task A_new_output_is_pre_bound_to_the_flow()
    {
        var http = factory.ClientFor(Org);
        var (flowId, _) = await Seed(confirmed: true);

        var res = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv" });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var o = (await res.Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;

        Leaves(o.Template.Root).Select(l => l.Rule!.CanonicalField).Should().Equal("invoice_no", "total", "sku");
        Leaves(o.Template.Root).Select(l => l.Name).Should().Equal("Invoice number", "Total", "SKU");
        o.Template.Root.Children.Single(c => c.NodeType == OutputNodeType.Array).Collection.Should().Be("items");

        var json = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "JSON", format = "json" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        Leaves(json.Template.Root).Select(l => l.Name).Should().Equal("invoice_no", "total", "sku");

        var (unconfirmedFlow, _) = await Seed(confirmed: false);
        (await http.PostAsJsonAsync($"/api/flows/{unconfirmedFlow}/outputs", new { name = "x", format = "csv" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SendWhen_RoundTrips_AndMustNameAHeaderField()
    {
        var http = factory.ClientFor(Org);
        var (flowId, _) = await Seed(confirmed: true);
        var rule = new SendWhen("invoice_no", "is", "Acme");

        var created = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv", sendWhen = rule }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        created.SendWhen.Should().Be(rule);
        var changed = rule with { Op = "contains", Value = "Beta" };
        (await http.PutAsJsonAsync($"/api/flows/{flowId}/outputs/{created.Id}", new { name = "CSV", format = "csv", sendWhen = changed })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetFromJsonAsync<List<OutputDto>>($"/api/flows/{flowId}/outputs", DocumentJson.Options))!.Single().SendWhen.Should().Be(changed);

        foreach (var bad in new[] { rule with { Field = "sku" }, rule with { Field = "nope" }, rule with { Op = "nope" }, rule with { Value = "  " } })
        {
            var res = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "x", format = "csv", sendWhen = bad });
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadAsStringAsync()).Should().Contain("output_invalid");
        }
    }

    [Fact]
    public async Task Preview_renders_the_document_without_sending()
    {
        var http = factory.ClientFor(Org);
        var (flowId, docId) = await Seed(confirmed: true);
        var output = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "Sheet", format = "xlsx" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;

        var res = await http.PostAsync($"/api/documents/{docId}/outputs/{output.Id}/preview", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var p = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").GetString()!;

        p.Should().StartWith("Invoice number,Total,SKU");
        p.Should().Contain("1.234,50");

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowDelivery>().CountAsync(d => d.DocumentId == docId)).Should().Be(0);
    }
}
