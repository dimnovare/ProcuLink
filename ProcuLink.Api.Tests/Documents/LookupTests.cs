using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
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
}
