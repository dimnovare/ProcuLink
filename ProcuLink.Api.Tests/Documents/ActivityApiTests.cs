using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class ActivityApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;
    private static readonly Guid OrgB = DocumentEngineTestFactory.OrgB;

    private async Task Save(object entity)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Add(entity);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> Flow(Guid org, string name = "Synthetic flow")
    {
        factory.ClientFor(org).Dispose();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        await Save(flow);
        return flow.Id;
    }

    private async Task<FlowDocument> Doc(Guid org, Guid flowId, string fileName, string? dedupKey = null, string source = "upload")
    {
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flowId, FileName = fileName, ContentType = "application/pdf",
            DedupKey = dedupKey, Source = source, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await Save(doc);
        return doc;
    }

    private Task Event(Guid org, Guid entityId, string action, DateTime at, JsonDocument? payload = null) => Save(new AuditEvent
    {
        Id = Guid.NewGuid(), OrgId = org, EntityType = "FlowDocument", EntityId = entityId, Action = action, Payload = payload, CreatedAt = at,
    });

    private Task<JsonElement> Get(Guid org, string query) =>
        factory.ClientFor(org).GetFromJsonAsync<JsonElement>($"/api/audit?{query}");

    private async Task<int> Total(Guid org, string query) => (await Get(org, query)).GetProperty("total").GetInt32();

    private async Task<List<JsonElement>> Events(Guid org, string query) =>
        (await Get(org, query)).GetProperty("events").EnumerateArray().ToList();

    [Theory]
    [InlineData(PlanConstants.Pilot, AccountStatusConstants.Trialing)]
    [InlineData(PlanConstants.Pilot, AccountStatusConstants.TrialExpired)]
    [InlineData(PlanConstants.Growth, AccountStatusConstants.PastDue)]
    public async Task The_list_is_open_on_every_plan_and_stays_readable_when_processing_is_paused(string plan, string status)
    {
        var http = factory.ClientFor(Org);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var org = await db.Organisations.SingleAsync(o => o.Id == Org);
            (org.Plan, org.AccountStatus) = (plan, status);
            await db.SaveChangesAsync();
        }

        var res = await http.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Filters_narrow_the_list_by_flow_action_dates_and_text()
    {
        var (f1, f2) = (await Flow(Org), await Flow(Org));
        var invoice = await Doc(Org, f1, "invoice-2291.pdf", dedupKey: "INV2291");
        var order = await Doc(Org, f2, "po-1.csv");
        var t = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        await Event(Org, invoice.Id, "document.received", t);
        await Event(Org, invoice.Id, "document.read", t.AddMinutes(1));
        await Event(Org, invoice.Id, "document.delivered", t.AddMinutes(2));
        await Event(Org, order.Id, "document.read", t.AddMinutes(3));
        const string day = "from=2026-03-01T00:00:00Z&to=2026-03-02T00:00:00Z";

        Assert.Equal(3, await Total(Org, $"flowId={f1}"));
        Assert.Equal(1, await Total(Org, $"flowId={f1}&actions=document.read"));
        Assert.Equal(2, await Total(Org, $"flowId={f1}&actions=document.read&actions=document.delivered"));
        Assert.Equal(1, await Total(Org, $"flowId={f1}&from=2026-03-01T09:01:00Z&to=2026-03-01T09:02:00Z"));   // from included, to not
        Assert.Equal(3, await Total(Org, $"q=2291&{day}"));                                                  // by number
        Assert.Equal(1, await Total(Org, $"q=po-1&{day}"));
    }

    [Fact]
    public async Task A_filter_never_reaches_another_workspace_or_a_document_that_is_gone()
    {
        var theirs = await Doc(OrgB, await Flow(OrgB), "theirs-only.pdf");
        var t = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        await Event(OrgB, theirs.Id, "document.read", t);
        await Event(Org, Guid.NewGuid(), "document.read", t);                 // its document no longer exists
        const string day = "from=2026-03-02T00:00:00Z&to=2026-03-03T00:00:00Z";

        Assert.Equal(0, await Total(Org, $"flowId={theirs.FlowId}&{day}"));
        Assert.Equal(0, await Total(Org, $"q=theirs-only&{day}"));
        var listed = Assert.Single(await Events(Org, day));                   // no document filter: still listed
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("documentId").ValueKind);
    }
}
