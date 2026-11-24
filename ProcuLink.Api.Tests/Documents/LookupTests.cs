using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class LookupTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private static object[] Entries(params (string From, string To)[] e) => e.Select(x => (object)new { from = x.From, to = x.To }).ToArray();

    private async Task<LookupDto> Create(HttpClient http, string name, params (string From, string To)[] e) =>
        (await (await http.PostAsJsonAsync("/api/lookups", new { name, entries = Entries(e) })).Content.ReadFromJsonAsync<LookupDto>(DocumentJson.Options))!;

    [Fact]
    public async Task Lookup_CrudRoundTrip_IsOrgScopedAndGated()
    {
        var http = factory.ClientFor(Org);
        var created = await Create(http, "Item codes", ("BLT-10", "A"), ("BLT-20", "B"));
        created.Entries.Should().HaveCount(2);

        var put = await http.PutAsJsonAsync($"/api/lookups/{created.Id}",
            new { name = "Supplier item codes", entries = Entries(("BLT-10", "A"), ("BLT-20", "B"), ("BLT-30", "C")) });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var got = (await http.GetFromJsonAsync<LookupDto>($"/api/lookups/{created.Id}", DocumentJson.Options))!;
        got.Name.Should().Be("Supplier item codes");
        got.Entries.Select(e => e.To).Should().Equal("A", "B", "C");
        (await http.GetFromJsonAsync<List<LookupSummaryDto>>("/api/lookups", DocumentJson.Options))!
            .Single(l => l.Id == created.Id).Count.Should().Be(3);

        (await factory.ClientFor(DocumentEngineTestFactory.OrgB).GetAsync($"/api/lookups/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var off = await factory.ClientFor(DocumentEngineTestFactory.OrgOff).GetAsync("/api/lookups");
        off.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await off.Content.ReadAsStringAsync()).Should().Contain("document_engine_not_enabled");
    }

    [Fact]
    public async Task Lookup_RefusesDuplicateFromAndOversize()
    {
        var http = factory.ClientFor(Org);
        var id = (await Create(http, "Codes", ("BLT-10", "A"))).Id;

        var dup = await http.PutAsJsonAsync($"/api/lookups/{id}", new { name = "Codes", entries = Entries(("BLT-10", "A"), (" blt-10 ", "B")) });
        dup.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await dup.Content.ReadAsStringAsync()).Should().Contain("lookup_invalid");

        var big = Enumerable.Range(0, LookupRules.MaxEntries + 1).Select(i => (object)new { from = $"K{i}", to = "x" }).ToArray();
        (await http.PutAsJsonAsync($"/api/lookups/{id}", new { name = "Codes", entries = big })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public void A_null_entry_is_a_problem_not_a_crash() =>
        LookupRules.Problem([null!]).Should().Be("Every entry needs a value to look for.");

    [Fact]
    public async Task Candidates_round_trip_and_a_save_without_them_keeps_them()
    {
        var http = factory.ClientFor(Org);
        var id = (await Create(http, "Codes", ("A", "SUP-1"))).Id;
        var candidates = new object[] { new { value = "SUP-1", description = "Pilsner bottle 0.5L" }, new { value = " SUP-2 ", description = "  " } };
        (await http.PutAsJsonAsync($"/api/lookups/{id}", new { name = "Codes", entries = Entries(("A", "SUP-1")), candidates })).StatusCode.Should().Be(HttpStatusCode.OK);

        var got = (await http.GetFromJsonAsync<LookupDto>($"/api/lookups/{id}", DocumentJson.Options))!;
        got.Candidates.Should().Equal(new LookupCandidate("SUP-1", "Pilsner bottle 0.5L"), new LookupCandidate("SUP-2", null));
        got.SuggestionsOn.Should().BeTrue();

        (await http.PutAsJsonAsync($"/api/lookups/{id}", new { name = "Codes", entries = Entries(("A", "SUP-1")) })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetFromJsonAsync<LookupDto>($"/api/lookups/{id}", DocumentJson.Options))!.Candidates.Should().HaveCount(2);
        (await http.GetFromJsonAsync<List<LookupSummaryDto>>("/api/lookups", DocumentJson.Options))!
            .Single(l => l.Id == id).Should().Match<LookupSummaryDto>(l => l.HasCandidates && l.SuggestionsOn);

        var dup = await http.PutAsJsonAsync($"/api/lookups/{id}", new { name = "Codes", entries = Entries(("A", "SUP-1")), candidates = new object[] { new { value = "A" }, new { value = "a" } } });
        dup.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await dup.Content.ReadAsStringAsync();
        body.Should().Contain("lookup_invalid").And.Contain("'a' appears more than once in the list to choose from.");
        var blank = await http.PutAsJsonAsync($"/api/lookups/{id}", new { name = "Codes", entries = Entries(("A", "SUP-1")), candidates = new object?[] { null } });
        blank.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await blank.Content.ReadAsStringAsync()).Should().Contain("Each row in the list to choose from needs a value.");
    }

    [Fact]
    public async Task Lookup_DeleteRefusedWhileAnOutputUsesIt()
    {
        var http = factory.ClientFor(Org);
        var id = (await Create(http, "Codes", ("BLT-10", "A"))).Id;
        var leaf = OutputNode.FieldOf("Code", new OutputFieldRule
        {
            CanonicalField = "sku",
            FieldManipulators = [new ManipulatorEntry { Type = "Lookup", Params = [id.ToString(), "keep"] }],
        });
        var template = new OutputNodeTemplate { Format = OutputFormat.Csv, Root = new OutputNode { Name = "root", NodeType = OutputNodeType.Object, Children = [leaf] } };
        var flowId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(new Flow { Id = flowId, OrgId = Org, Name = "Synthetic flow", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Set<FlowOutput>().Add(new FlowOutput
            {
                Id = outputId, OrgId = Org, FlowId = flowId, Name = "CSV", Format = "csv",
                TemplateJson = DocumentJson.Serialize(template), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var refused = await http.DeleteAsync($"/api/lookups/{id}");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("lookup_in_use");

        // Removed through the context: the output DELETE route uses ExecuteDelete, which the in-memory test provider cannot run.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Set<FlowOutput>().Remove(await db.Set<FlowOutput>().FindAsync(outputId) ?? throw new InvalidOperationException());
            await db.SaveChangesAsync();
        }
        (await http.DeleteAsync($"/api/lookups/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deleting_a_lookup_ignores_outputs_with_empty_steps()
    {
        var http = factory.ClientFor(Org);
        var id = (await Create(http, "Unrelated", ("A", "B"))).Id;
        var flowId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(new Flow { Id = flowId, OrgId = Org, Name = "Empty steps", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Set<FlowOutput>().Add(new FlowOutput
            {
                Id = Guid.NewGuid(), OrgId = Org, FlowId = flowId, Name = "CSV", Format = "csv",
                TemplateJson = """{"format":"csv","root":{"name":"root","children":[null,{"name":"c","children":null,"rule":{"fieldManipulators":[null,{"type":"Lookup","params":null}]}}]}}""",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        (await http.DeleteAsync($"/api/lookups/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_list_says_which_flows_use_a_lookup_draft_or_live_each_flow_once_never_another_organisations()
    {
        var http = factory.ClientFor(Org);
        var (used, idle) = ((await Create(http, "Item codes", ("BLT-10", "A"))).Id, (await Create(http, "Unused codes", ("X", "Y"))).Id);
        var t = DateTime.UtcNow;
        string Template(Guid lookup) => DocumentJson.Serialize(new OutputNodeTemplate
        {
            Format = OutputFormat.Csv,
            Root = new OutputNode { Name = "root", NodeType = OutputNodeType.Object, Children = [OutputNode.FieldOf("Code", new OutputFieldRule
            {
                OutputPath = "Code", CanonicalField = "sku", FieldManipulators = [new ManipulatorEntry { Type = "Lookup", Params = [lookup.ToString(), "keep"] }],
            })] },
        });
        Flow FlowOf(Guid org, string name) => new()   // confirmed fields, as FlowVersionTests.Seed does, so publish accepts it
            { Id = Guid.NewGuid(), OrgId = org, Name = name, FieldsConfirmed = true, FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty), CreatedAt = t, UpdatedAt = t };
        FlowOutput OutputOf(Flow f, string name, string template) => new()
            { Id = Guid.NewGuid(), OrgId = f.OrgId, FlowId = f.Id, Name = name, Format = "csv", TemplateJson = template, CreatedAt = t, UpdatedAt = t };
        var (draftOnly, liveOnly, theirs) = (FlowOf(Org, "Supplier invoices"), FlowOf(Org, "Delivery notes"), FlowOf(DocumentEngineTestFactory.OrgB, "Their flow"));
        var liveOutput = OutputOf(liveOnly, "Notes CSV", Template(used));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.AddRange(draftOnly, liveOnly, theirs, liveOutput,
                OutputOf(draftOnly, "Invoice CSV", Template(used)), OutputOf(draftOnly, "Credit CSV", Template(used)),   // one flow, two outputs
                OutputOf(theirs, "Their CSV", Template(used)));                                                        // another organisation naming our id
            await db.SaveChangesAsync();
        }
        (await http.PostAsync($"/api/flows/{liveOnly.Id}/publish", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            (await db.Set<FlowOutput>().SingleAsync(o => o.Id == liveOutput.Id)).TemplateJson = Template(idle);  // the draft moves on; the live version still names `used`
            await db.SaveChangesAsync();
        }

        var list = (await http.GetFromJsonAsync<List<LookupSummaryDto>>("/api/lookups", DocumentJson.Options))!;
        list.Single(l => l.Id == used).UsedBy.Select(u => (u.FlowId, u.FlowName))
            .Should().Equal((liveOnly.Id, "Delivery notes"), (draftOnly.Id, "Supplier invoices"));    // by name, each once, no "Their flow"
        list.Single(l => l.Id == idle).UsedBy.Select(u => u.FlowName).Should().Equal("Delivery notes");   // used by a draft only
        (await http.DeleteAsync($"/api/lookups/{used}")).StatusCode.Should().Be(HttpStatusCode.Conflict); // the list and the refusal agree
        var unused = (await Create(http, "Pallet types")).Id;
        (await http.GetFromJsonAsync<List<LookupSummaryDto>>("/api/lookups", DocumentJson.Options))!.Single(l => l.Id == unused).UsedBy.Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_DeleteRefusedWhileAFieldCheckUsesIt()
    {
        var http = factory.ClientFor(Org);
        var id = (await Create(http, "Cost centres", ("CC-10", "CC-10"))).Id;
        var flowId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(new Flow
            {
                Id = flowId, OrgId = Org, Name = "Checked flow", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("cost_centre", "Cost centre", FieldTypes.Text, null, false, new FieldCheck(FieldCheckOps.InLookup, LookupId: id))])),
            });
            await db.SaveChangesAsync();
        }

        var refused = await http.DeleteAsync($"/api/lookups/{id}");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("lookup_in_use");
        (await http.GetFromJsonAsync<List<LookupSummaryDto>>("/api/lookups", DocumentJson.Options))!
            .Single(l => l.Id == id).UsedBy.Select(u => u.FlowId).Should().Equal(flowId);
    }
}
