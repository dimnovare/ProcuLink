using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
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

    private Task Event(Guid org, Guid entityId, string action, DateTime at, object? payload = null) => Save(new AuditEvent
    {
        Id = Guid.NewGuid(), OrgId = org, EntityType = "FlowDocument", EntityId = entityId, Action = action, CreatedAt = at,
        Payload = payload is null ? null : JsonSerializer.SerializeToDocument(payload),
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
        Assert.Equal(3, await Total(Org, $"q=inv%202291&{day}"));                                             // by number
        Assert.Equal(1, await Total(Org, $"q=po-1&{day}"));
        var absurd = await Get(Org, $"flowId={f1}&page=2000000000");                                        // no overflowed Skip
        Assert.Equal((0, 3), (absurd.GetProperty("events").GetArrayLength(), absurd.GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task A_filter_never_reaches_another_workspace_or_a_document_that_is_gone()
    {
        var theirs = await Doc(OrgB, await Flow(OrgB), "theirs-only.pdf");
        var t = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        await Event(OrgB, theirs.Id, "document.read", t);
        await Event(Org, Guid.NewGuid(), "document.read", t);                 // its document no longer exists
        await Event(Org, theirs.Id, "document.read", t);                      // this workspace's event naming their document
        const string day = "from=2026-03-02T00:00:00Z&to=2026-03-03T00:00:00Z";

        Assert.Equal(0, await Total(Org, $"flowId={theirs.FlowId}&{day}"));
        Assert.Equal(0, await Total(Org, $"q=theirs-only&{day}"));
        var listed = await Events(Org, day);                                  // no document filter: both still listed
        Assert.Equal(2, listed.Count);
        Assert.All(listed, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("documentId").ValueKind));
        var raw = string.Join("", listed.Select(e => e.GetRawText()));
        Assert.DoesNotContain("theirs-only", raw);                            // their file name and flow are never named
        Assert.DoesNotContain(theirs.FlowId.ToString(), raw);
    }

    [Fact]
    public async Task Document_events_say_who_which_flow_and_how_it_arrived()
    {
        var flow = await Flow(Org, "Supplier invoices");
        var uploaded = await Doc(Org, flow, "uploaded.pdf", source: "upload");
        var emailed = await Doc(Org, flow, "emailed.pdf", source: IntakeKind.Email);
        var t = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc);
        await Event(Org, uploaded.Id, "document.received", t, new { by = "user_uploader" });
        await Event(Org, uploaded.Id, "document.read", t.AddMinutes(1));
        await Event(Org, uploaded.Id, "document.sent", t.AddMinutes(2), new { mode = "person", by = "user_sender" });
        await Event(Org, uploaded.Id, "document.delivered", t.AddMinutes(3));
        await Event(Org, emailed.Id, "document.received", t);
        await Event(Org, emailed.Id, "document.sent", t.AddMinutes(2), new { mode = "auto" });
        await Event(Org, emailed.Id, "document.delivery_failed", t.AddMinutes(3), new { error = "Login refused" });

        var rows = await Events(Org, $"flowId={flow}&pageSize=200");
        JsonElement Row(Guid doc, string action) => rows.Single(e =>
            e.GetProperty("documentId").GetString() == doc.ToString() && e.GetProperty("action").GetString() == action);

        var read = Row(uploaded.Id, "document.read");
        Assert.Equal(("user_uploader", "user", "upload", "Supplier invoices"),
            (read.GetProperty("actorId").GetString(), read.GetProperty("actorType").GetString(),
             read.GetProperty("source").GetString(), read.GetProperty("flowName").GetString()));
        Assert.Equal("user_sender", Row(uploaded.Id, "document.delivered").GetProperty("actorId").GetString());

        var failed = Row(emailed.Id, "document.delivery_failed");
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("actorId").ValueKind);        // an automatic send
        Assert.Equal("system", failed.GetProperty("actorType").GetString());              // never "ai"
        Assert.Equal("email", failed.GetProperty("source").GetString());
    }
}
