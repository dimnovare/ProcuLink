using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
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
            (await res.Content.ReadAsStringAsync()).Should().Contain("output_invalid").And.Contain("\"detail\":\"send_when\"");
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

    [Fact]
    public async Task Preview_with_a_draft_template_renders_the_draft_and_keeps_the_saved_one()
    {
        var http = factory.ClientFor(Org);
        var (flowId, docId) = await Seed(confirmed: true);
        var output = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        var first = output.Template.Root.Children[0];
        var draft = output.Template with
        {
            Root = output.Template.Root with
            {
                Children = [first with { Rule = first.Rule! with { FixedValue = "DRAFT-1", CanonicalField = null } }, .. output.Template.Root.Children.Skip(1)],
            },
        };
        string Saved()
        {
            using var scope = factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowOutput>().AsNoTracking().Single(o => o.Id == output.Id).TemplateJson;
        }
        var before = Saved();

        var res = await http.PostAsJsonAsync($"/api/documents/{docId}/outputs/{output.Id}/preview", new { template = draft });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").GetString().Should().Contain("DRAFT-1");
        Saved().Should().Be(before);
    }

    [Fact]
    public async Task Preview_with_a_draft_of_the_wrong_format_is_refused()
    {
        var http = factory.ClientFor(Org);
        var (flowId, docId) = await Seed(confirmed: true);
        var csv = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        var json = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "JSON", format = "json" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;

        var res = await http.PostAsJsonAsync($"/api/documents/{docId}/outputs/{csv.Id}/preview", new { template = json.Template });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("output_invalid").And.Contain("must be a Csv template");
    }

    [Fact]
    public async Task LookupStep_IsCheckedOnSave_AndPreviewListsMisses()
    {
        var http = factory.ClientFor(Org);
        var (flowId, docId) = await Seed(confirmed: true);
        var output = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        OutputNode Leaf(string name, string field, Guid lookupId) => OutputNode.FieldOf(name, new OutputFieldRule
        {
            OutputPath = name, CanonicalField = field,
            FieldManipulators = [new ManipulatorEntry { Type = "Lookup", Params = [lookupId.ToString()] }],
        });
        object Body(Guid lookupId) => new
        {
            name = "CSV", format = "csv",
            template = new OutputNodeTemplate
            {
                Format = OutputFormat.Csv,
                Root = OutputNode.Obj("root", Leaf("No", "invoice_no", lookupId), OutputNode.Arr("Lines", OutputNode.Obj("Line", Leaf("SKU", "sku", lookupId)))),
            },
        };

        var refused = await http.PutAsJsonAsync($"/api/flows/{flowId}/outputs/{output.Id}", Body(Guid.NewGuid()));
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("output_invalid").And.Contain("\"detail\":\"lookup\"");

        var lookup = (await (await http.PostAsJsonAsync("/api/lookups", new { name = "Item codes", entries = new[] { new { from = "a1", to = "A-7" } } }))
            .Content.ReadFromJsonAsync<LookupDto>(DocumentJson.Options))!;
        (await http.PutAsJsonAsync($"/api/flows/{flowId}/outputs/{output.Id}", Body(lookup.Id))).StatusCode.Should().Be(HttpStatusCode.OK);

        var p = await (await http.PostAsync($"/api/documents/{docId}/outputs/{output.Id}/preview", null)).Content.ReadFromJsonAsync<JsonElement>();
        p.GetProperty("content").GetString().Should().Contain("INV-7,A-7");
        var miss = p.GetProperty("lookupMisses").EnumerateArray().Single();
        (miss.GetProperty("lookupId").GetGuid(), miss.GetProperty("value").GetString()).Should().Be((lookup.Id, "INV-7"));
    }

    [Fact]
    public async Task Preview_with_suggest_sends_each_miss_with_its_own_row_and_returns_the_matches()
    {
        var http = factory.ClientFor(Org);
        Guid flowId, docId, lookupId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var cols = new[] { Field("sku", "SKU"), Field("name", "Name") };
            var flow = new Flow
            {
                Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true,
                FieldsJson = DocumentJson.Serialize(new FlowFieldList([], [new FlowTable("items", "Items", cols)])),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ReadRow Row(string sku, string name) => new(new Dictionary<string, ReadCell>
                { ["sku"] = new(sku, 1, null, false, false), ["name"] = new(name, 1, null, false, false) });
            var read = new ReadResult("Invoice", [],
                [new ReadTable("items", "Items", cols.Select(c => new ReadColumn(c.Key, c.Label, FieldTypes.Text, null)).ToList(),
                    [Row("A1", "Pilsner 0.5"), Row("B2", "Stout")], true)]);
            var doc = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf",
                Status = DocumentStatus.Ready, ReadJson = DocumentJson.Serialize(read), ReadAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Flows.Add(flow);
            db.FlowDocuments.Add(doc);
            db.Set<Lookup>().Add(new Lookup
            {
                Id = lookupId, OrgId = Org, Name = "Codes",
                CandidatesJson = DocumentJson.Serialize(new[] { new LookupCandidate("SUP-1", "Pilsner bottle 0.5L"), new LookupCandidate("SUP-2", "Stout can") }),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            (flowId, docId) = (flow.Id, doc.Id);
        }
        var output = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv", tableKey = "items" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        var leaf = OutputNode.FieldOf("SKU", new OutputFieldRule
        {
            OutputPath = "SKU", CanonicalField = "sku",
            FieldManipulators = [new ManipulatorEntry { Type = "Lookup", Params = [lookupId.ToString(), "keep"] }],
        });
        (await http.PutAsJsonAsync($"/api/flows/{flowId}/outputs/{output.Id}", new
        {
            name = "CSV", format = "csv", tableKey = "items",
            template = new OutputNodeTemplate { Format = OutputFormat.Csv, Root = OutputNode.Obj("root", OutputNode.Arr("Lines", OutputNode.Obj("Line", leaf))) },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var lines = new List<AiMappingLineContext>();
        var candidates = new List<AiMappingCandidate>();
        factory.Ai.Setup(a => a.SuggestSupplierItemCodesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(), It.IsAny<IReadOnlyList<AiMappingCandidate>>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, string _, IReadOnlyList<AiMappingLineContext> l, IReadOnlyList<AiMappingCandidate> c, CancellationToken _) =>
                { lines.AddRange(l); candidates.AddRange(c); })
            .ReturnsAsync(new Dictionary<int, AiMappingSuggestion> { [0] = new("SUP-1", 0.9f, "name match", "lookup") });

        var url = $"/api/documents/{docId}/outputs/{output.Id}/preview";
        var plain = await (await http.PostAsync(url, null)).Content.ReadFromJsonAsync<JsonElement>();
        plain.GetProperty("lookupMisses").EnumerateArray().Should().OnlyContain(m =>
            m.EnumerateObject().Select(p => p.Name).OrderBy(n => n).SequenceEqual(new[] { "lookupId", "value" }));

        var res = await http.PostAsync(url + "?suggest=true", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        lines.Single(l => l.BuyerItemCode == "A1").Description.Should().Contain("Pilsner 0.5");
        lines.Single(l => l.BuyerItemCode == "B2").Description.Should().Contain("Stout").And.NotContain("Pilsner");
        candidates.Should().OnlyContain(c => c.IsCatalogProduct);
        var s = body.GetProperty("suggestions").EnumerateArray().Single();
        (s.GetProperty("lookupId").GetGuid(), s.GetProperty("value").GetString(), s.GetProperty("to").GetString(),
            s.GetProperty("confidence").GetSingle(), s.GetProperty("description").GetString())
            .Should().Be((lookupId, "A1", "SUP-1", 0.9f, "Pilsner bottle 0.5L"));

        var off = await factory.ClientFor(DocumentEngineTestFactory.OrgB).PostAsync(url + "?suggest=true", null);
        off.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await off.Content.ReadAsStringAsync()).Should().Contain("lookup_suggestions_off");
    }
}
