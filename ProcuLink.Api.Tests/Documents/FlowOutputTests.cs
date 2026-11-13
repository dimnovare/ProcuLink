using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
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

    private HttpClient MemberClient()
    {
        var http = factory.ClientFor(Org);
        http.DefaultRequestHeaders.Remove(RbacTestAuthHandler.RoleHeader);
        http.DefaultRequestHeaders.Add(RbacTestAuthHandler.RoleHeader, "org:member");
        return http;
    }

    [Fact]
    public async Task A_member_cannot_create_an_output()
    {
        var (flowId, _) = await Seed(confirmed: true);

        var res = await MemberClient().PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("requires_org_admin");
    }

    [Fact]
    public async Task A_member_can_rename_a_flow_but_not_change_its_automation()
    {
        var (flowId, _) = await Seed(confirmed: true);
        var member = MemberClient();

        (await member.PatchAsJsonAsync($"/api/flows/{flowId}", new { name = "Renamed" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await member.PatchAsJsonAsync($"/api/flows/{flowId}", new { automation = FlowAutomation.All.First(a => a != FlowAutomation.Review) });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("requires_org_admin");
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Flows.AsNoTracking().SingleAsync(f => f.Id == flowId))
            .Automation.Should().Be(FlowAutomation.Review);
    }

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
    public async Task A_template_with_a_null_step_params_or_child_is_refused_not_a_500()
    {
        var http = factory.ClientFor(Org);
        var (flowId, _) = await Seed(confirmed: true);
        object Template(object?[] children) => new { format = "csv", root = new { name = "r", nodeType = "object", children } };
        object Leaf(object?[] steps) => new { name = "a", nodeType = "field", rule = new { fieldManipulators = steps } };

        foreach (var bad in new[]
        {
            Template([Leaf([null])]),
            Template([null]),
        })
        {
            var res = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "x", format = "csv", template = bad });
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await res.Content.ReadAsStringAsync());
            (await res.Content.ReadAsStringAsync()).Should().Contain("output_invalid").And.Contain("\"detail\":\"template\"");
        }
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
            var read = new ReadResult("Invoice",
                [new ReadField("seller", "Seller", "Brewery North", FieldTypes.Text, null, 1, null, false, true, false)],
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
        lines.Should().OnlyContain(l => !l.Description!.Contains("Brewery North"));
        candidates.Should().OnlyContain(c => c.IsCatalogProduct);
        var s = body.GetProperty("suggestions").EnumerateArray().Single();
        (s.GetProperty("lookupId").GetGuid(), s.GetProperty("value").GetString(), s.GetProperty("to").GetString(),
            s.GetProperty("confidence").GetSingle(), s.GetProperty("description").GetString())
            .Should().Be((lookupId, "A1", "SUP-1", 0.9f, "Pilsner bottle 0.5L"));

        var off = await factory.ClientFor(DocumentEngineTestFactory.OrgB).PostAsync(url + "?suggest=true", null);
        off.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await off.Content.ReadAsStringAsync()).Should().Contain("lookup_suggestions_off");
    }

    private static MultipartFormDataContent File(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new("image/png");
        return new MultipartFormDataContent { { part, "file", "logo.png" } };
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Pdf_output_keeps_its_layout_and_logo_and_previews_as_pdf()
    {
        var http = factory.ClientFor(Org);
        var (flowId, docId) = await Seed(confirmed: true);

        var created = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "PDF", format = "pdf", layout = new { title = " Order " } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var output = (await created.Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        (await http.PutAsync($"/api/flows/{flowId}/outputs/{output.Id}/logo", File(OnePixelPng))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var saved = (await http.GetFromJsonAsync<List<OutputDto>>($"/api/flows/{flowId}/outputs", DocumentJson.Options))!.Single(o => o.Id == output.Id);
        saved.Layout!.Title.Should().Be("Order");
        saved.HasLogo.Should().BeTrue();

        var preview = await (await http.PostAsync($"/api/documents/{docId}/outputs/{output.Id}/preview", null)).Content.ReadFromJsonAsync<JsonElement>();
        preview.GetProperty("base64").GetString().Should().StartWith("JVBER");
        preview.GetProperty("content").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task A_standard_output_keeps_its_standard_and_names_what_a_document_lacks()
    {
        var http = factory.ClientFor(Org);
        var (flowId, docId) = await Seed(confirmed: true);

        var created = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "Orders", format = "", standard = "cxml-1-2", layout = new { title = "x" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var output = (await created.Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        (output.Format, output.Standard, output.Layout).Should().Be(("xml", "cxml-1-2", (OutputLayout?)null));
        output.Needs.Should().Contain(FieldMeanings.LineItemCode);

        // An update can never turn a standard output into a template output, nor give it a template.
        var csv = new OutputNodeTemplate { Format = OutputFormat.Csv };
        var put = await http.PutAsJsonAsync($"/api/flows/{flowId}/outputs/{output.Id}", new { name = "Orders 2", format = "csv", standard = (string?)null, template = csv });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await put.Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        (updated.Name, updated.Format, updated.Standard).Should().Be(("Orders 2", "xml", "cxml-1-2"));
        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowOutput>().AsNoTracking().SingleAsync(o => o.Id == output.Id))
                .TemplateJson.Should().Be("{}");

        var unknown = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "Orders", format = "", standard = "nope" });
        unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("unsupported_format");

        // A test send through the standard is refused the same way, never sent as the request's csv template.
        // The render refuses before any destination is looked up, so none needs to exist.
        var preview = await http.PostAsync($"/api/documents/{docId}/outputs/{output.Id}/preview", null);
        var testSend = await http.PostAsJsonAsync($"/api/documents/{docId}/outputs/test",
            new { format = "csv", tableKey = (string?)null, destinationId = Guid.NewGuid(), template = csv, standard = "cxml-1-2" });
        foreach (var res in new[] { preview, testSend })
        {
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var refused = await res.Content.ReadFromJsonAsync<JsonElement>();
            refused.GetProperty("error").GetString().Should().Be("output_invalid");
            refused.GetProperty("detail").GetString().Should().StartWith("This standard needs");
        }
    }

    [Fact]
    public async Task Sending_through_a_standard_output_stores_the_standard_file()
    {
        var http = factory.ClientFor(Org);
        (string Key, string Meaning, string Value)[] header =
            [("po", FieldMeanings.DocumentNumber, "PO-1"), ("date", FieldMeanings.IssueDate, "13.11.2026"), ("cur", FieldMeanings.Currency, "eur")];
        (string Key, string Meaning, string Value)[] line =
            [("code", FieldMeanings.LineItemCode, "A-1"), ("qty", FieldMeanings.LineQuantity, "3"), ("price", FieldMeanings.LineUnitPrice, "9,99")];
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList(
                header.Select(f => new FlowField(f.Key, f.Key, FieldTypes.Text, f.Meaning, false)).ToList(),
                [new FlowTable("lines", "Lines", line.Select(f => new FlowField(f.Key, f.Key, FieldTypes.Text, f.Meaning, false)).ToList())])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        // The read carries no meanings of its own: the flow's are what the standard is filled from.
        var read = new ReadResult("Purchase order",
            header.Select(f => new ReadField(f.Key, f.Key, f.Value, FieldTypes.Text, null, 1, null, false, true, false)).ToList(),
            [new ReadTable("lines", "Lines", line.Select(f => new ReadColumn(f.Key, f.Key, FieldTypes.Text, null)).ToList(),
                [new ReadRow(line.ToDictionary(f => f.Key, f => new ReadCell(f.Value, 1, null, false, false)))], true)]);
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf",
            Status = DocumentStatus.Ready, ReadJson = DocumentJson.Serialize(read), ReadAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(flow);
            db.FlowDocuments.Add(doc);
            await db.SaveChangesAsync();
        }

        // X12 is stored as txt, the one standard format a layout would otherwise apply to.
        var created = await http.PostAsJsonAsync($"/api/flows/{flow.Id}/outputs", new { name = "X12", format = "", standard = "x12-850", tableKey = "lines", layout = new { title = "x" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!.Layout.Should().BeNull();

        (await http.PostAsync($"/api/documents/{doc.Id}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = factory.Services.CreateScope();
        var delivery = await check.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowDelivery>().AsNoTracking().SingleAsync(d => d.DocumentId == doc.Id);
        delivery.Status.Should().Be(FlowDeliveryStatus.Stored);
        delivery.FileName.Should().EndWith(DeliveryMediaTypes.For(OutputFormat.X12).FileExtension);
        var upload = factory.Storage.Invocations.Last(i => i.Method.Name == "UploadAsync" && (string)i.Arguments[1] == delivery.ArtifactKey);
        var body = (Stream)upload.Arguments[0];
        body.Position = 0;
        (await new StreamReader(body).ReadToEndAsync()).Should().StartWith("ISA");
    }

    [Fact]
    public async Task Fixed_output_needs_a_width_on_every_column()
    {
        var http = factory.ClientFor(Org);
        var (flowId, _) = await Seed(confirmed: true);

        var created = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "Fixed", format = "fixed" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var output = (await created.Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;

        var template = System.Text.Json.Nodes.JsonNode.Parse(DocumentJson.Serialize(output.Template))!;
        Leaves(output.Template.Root).Should().OnlyContain(l => l.Rule!.Width >= 40);
        static System.Text.Json.Nodes.JsonObject? FirstWithWidth(System.Text.Json.Nodes.JsonNode? n) => n switch
        {
            System.Text.Json.Nodes.JsonObject o when o.ContainsKey("width") => o,
            System.Text.Json.Nodes.JsonObject o => o.Select(kv => FirstWithWidth(kv.Value)).FirstOrDefault(x => x is not null),
            System.Text.Json.Nodes.JsonArray a => a.Select(FirstWithWidth).FirstOrDefault(x => x is not null),
            _ => null,
        };
        FirstWithWidth(template)!.Remove("width");

        var res = await http.PutAsJsonAsync($"/api/flows/{flowId}/outputs/{output.Id}", new { name = "Fixed", format = "fixed", template });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().Should().Be("widths");
    }

    [Fact]
    public async Task Logo_that_is_not_an_image_is_refused()
    {
        var http = factory.ClientFor(Org);
        var (flowId, _) = await Seed(confirmed: true);
        var output = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "PDF", format = "pdf" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;

        var res = await http.PutAsync($"/api/flows/{flowId}/outputs/{output.Id}/logo", File("hello"u8.ToArray()));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("invalid_logo");
    }

    private static MultipartFormDataContent Upload(string fileName, string content) =>
        new() { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content)), "file", fileName } };

    [Fact]
    public async Task From_sample_uses_names_then_ai_and_ignores_keys_it_may_not_use()
    {
        var (flowId, _) = await Seed(confirmed: true);
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            (await db.Flows.SingleAsync(f => f.Id == flowId)).FieldsJson = DocumentJson.Serialize(new FlowFieldList([Field("po_number", "PO number"), Field("ship_address", "Delivery address")]));
            await db.SaveChangesAsync();
        }
        IReadOnlyList<OutputSlot>? sent = null;
        factory.Designer.Reset();
        factory.Designer.Setup(d => d.BindAsync(Org, It.IsAny<IReadOnlyList<OutputSlot>>(), It.IsAny<FlowFieldList>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, IReadOnlyList<OutputSlot> slots, FlowFieldList _, CancellationToken _) => sent = slots)
            .ReturnsAsync([new SlotMatch("Ship to", "nope", 0.9f), new SlotMatch("Ship to", "ship_address", 0.8f)]);

        var res = await factory.ClientFor(Org).PostAsync($"/api/flows/{flowId}/outputs/from-sample", Upload("orders.csv", "PO number,Ship to\n1,x\n"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var draft = (await res.Content.ReadFromJsonAsync<OutputDraftDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        sent!.Select(s => s.Name).Should().Equal("Ship to");
        draft.Leaves.Single(l => l.Path == "PO number").Should().Match<DraftLeaf>(l => l.By == "name" && l.FieldKey == "po_number");
        draft.Leaves.Single(l => l.Path == "Ship to").Should().Match<DraftLeaf>(l => l.By == "ai" && l.FieldKey == "ship_address" && l.Confidence == 0.8f);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowOutput>().Count(o => o.FlowId == flowId).Should().Be(0);
    }

    [Fact]
    public async Task From_sample_refuses_files_it_cannot_use()
    {
        var (flowId, _) = await Seed(confirmed: true);
        var http = factory.ClientFor(Org);

        var pdf = await http.PostAsync($"/api/flows/{flowId}/outputs/from-sample", Upload("a.pdf", "x"));
        var bad = await http.PostAsync($"/api/flows/{flowId}/outputs/from-sample", Upload("a.json", "{not json"));

        pdf.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await pdf.Content.ReadAsStringAsync()).Should().Contain("unsupported_format");
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("unreadable_file");
    }

    [Fact]
    public async Task From_documentation_is_off_unless_switched_on()
    {
        var (flowId, _) = await Seed(confirmed: true);
        var other = factory.ClientFor(DocumentEngineTestFactory.OrgB);
        var created = (await (await other.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Synthetic flow"))).Content
            .ReadFromJsonAsync<FlowDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        created.DocsOutputsOn.Should().BeFalse();

        var res = await other.PostAsync($"/api/flows/{created.Id}/outputs/from-documentation", Upload("spec.txt", "x"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("docs_outputs_off");
        var flow = (await factory.ClientFor(Org).GetFromJsonAsync<FlowDto>($"/api/flows/{flowId}", new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        flow.DocsOutputsOn.Should().BeTrue();
    }

    [Fact]
    public async Task From_documentation_returns_a_cited_draft_and_maps_failures()
    {
        var (flowId, _) = await Seed(confirmed: true);
        factory.Designer.Reset();
        var proposal = new ProposedOutput("Invoice import", "csv", "Send a CSV", ",",
            new ProposedNode("document", "object", null, false, null, "", [new ProposedNode("Invoice", "field", "invoice_no", true, null, "Invoice: the number", [])]),
            null);
        factory.Designer.SetupSequence(d => d.ProposeAsync(Org, It.IsAny<string>(), It.IsAny<FlowFieldList>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProposalResult(proposal, null))
            .ReturnsAsync(new ProposalResult(null, "no_egress"))
            .ReturnsAsync(new ProposalResult(null, "ai_usage_cap"))
            .ReturnsAsync(new ProposalResult(null, "ai_error"));
        var http = factory.ClientFor(Org);
        var url = $"/api/flows/{flowId}/outputs/from-documentation";

        var ok = await http.PostAsync(url, Upload("spec.txt", "Send a CSV file.\nInvoice: the number\n"));
        var egress = await http.PostAsync(url, Upload("spec.txt", "x"));
        var cap = await http.PostAsync(url, Upload("spec.txt", "x"));
        var failed = await http.PostAsync(url, Upload("spec.txt", "x"));

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var draft = (await ok.Content.ReadFromJsonAsync<OutputDraftDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        draft.Leaves.Single().Should().Match<DraftLeaf>(l => l.FieldKey == "invoice_no" && l.Required && l.Cite!.Where == "line 2");
        draft.FormatCite!.Where.Should().Be("line 1");
        egress.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await egress.Content.ReadAsStringAsync()).Should().Contain("no_egress");
        ((int)cap.StatusCode).Should().Be(429);
        ((int)failed.StatusCode).Should().Be(502);
        (await failed.Content.ReadAsStringAsync()).Should().Contain("ai_error");
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<FlowOutput>().Count(o => o.FlowId == flowId).Should().Be(0);
    }

    [Fact]
    public async Task Draft_test_sends_the_rendered_document_marked_as_a_test()
    {
        var http = factory.ClientFor(Org);
        var (flowId, docId) = await Seed(confirmed: true);
        var output = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "CSV", format = "csv" }))
            .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        Guid Destination(Guid org)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var d = new FlowDestination { Id = Guid.NewGuid(), OrgId = org, Name = "d", Protocol = "http", ConfigJson = """{"url":"https://example.com/in"}""" };
            db.Set<FlowDestination>().Add(d);
            db.SaveChanges();
            return d.Id;
        }
        string? sentName = null, sentText = null;
        factory.Dispatcher.Setup(x => x.DispatchAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SupplierDeliveryConfig>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback((byte[] c, string n, string _, SupplierDeliveryConfig _, string _, CancellationToken _, string? _, bool _) =>
                (sentName, sentText) = (n, System.Text.Encoding.UTF8.GetString(c)))
            .ReturnsAsync(new DeliveryResult(true, null, 200));
        var url = $"/api/documents/{docId}/outputs/test";

        var res = await http.PostAsJsonAsync(url, new { format = "csv", destinationId = Destination(Org), template = output.Template });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean().Should().BeTrue();
        sentName.Should().Be("TEST_synthetic.csv");
        sentText.Should().Contain("INV-7");

        var other = await http.PostAsJsonAsync(url, new { format = "csv", destinationId = Destination(DocumentEngineTestFactory.OrgB), template = output.Template });
        string? sentType = null;
        factory.Dispatcher.Setup(x => x.DispatchAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SupplierDeliveryConfig>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback((byte[] _, string n, string t, SupplierDeliveryConfig _, string _, CancellationToken _, string? _, bool _) => (sentName, sentType) = (n, t))
            .ReturnsAsync(new DeliveryResult(true, null, 200));
        var xlsx = (await (await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "Sheet", format = "xlsx" })).Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!;
        (await http.PostAsJsonAsync(url, new { format = "xlsx", destinationId = Destination(Org), template = xlsx.Template })).StatusCode.Should().Be(HttpStatusCode.OK);
        sentName.Should().Be("TEST_synthetic.xlsx");
        sentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Content.ReadAsStringAsync()).Should().Contain("destination_not_found");
    }
}
