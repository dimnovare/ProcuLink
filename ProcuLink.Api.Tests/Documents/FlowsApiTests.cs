using System.Net;
using System.Net.Http.Json;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class FlowsApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static FlowFieldList Fields(params string[] keys) =>
        new(keys.Select(k => new FlowField(k, k, FieldTypes.Money, null, Required: true)).ToList());

    [Fact]
    public async Task Create_list_get_and_confirm_fields()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);

        var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("  Supplier invoices  "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var flow = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!;
        Assert.Equal("Supplier invoices", flow.Name);

        var list = (await http.GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!;
        Assert.Contains(list, f => f.Id == flow.Id);

        var put = await http.PutAsJsonAsync($"/api/flows/{flow.Id}/fields", Fields("total_gross"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await put.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.FieldsConfirmed);

        var dup = await http.PutAsJsonAsync($"/api/flows/{flow.Id}/fields", Fields("total_gross", "total_gross"));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Contains("invalid_fields", await dup.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Flow_reports_whether_scans_are_on()
    {
        foreach (var (org, expected) in new[] { (DocumentEngineTestFactory.OrgB, true), (DocumentEngineTestFactory.OrgA, false) })
        {
            var http = factory.ClientFor(org);
            var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Scans flag"));
            var id = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id;
            var flow = (await http.GetFromJsonAsync<FlowDto>($"/api/flows/{id}", DocumentJson.Options))!;
            Assert.Equal(expected, flow.ScansOn);
        }
    }

    [Fact]
    public async Task Other_org_flow_is_404_and_disabled_org_is_403()
    {
        var created = await factory.ClientFor(DocumentEngineTestFactory.OrgA).PostAsJsonAsync("/api/flows", new CreateFlowRequest("Mine"));
        var flow = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!;

        var other = await factory.ClientFor(DocumentEngineTestFactory.OrgB).GetAsync($"/api/flows/{flow.Id}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Contains("flow_not_found", await other.Content.ReadAsStringAsync());

        var off = await factory.ClientFor(DocumentEngineTestFactory.OrgOff).GetAsync("/api/flows");
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Contains("document_engine_not_enabled", await off.Content.ReadAsStringAsync());
    }
}
