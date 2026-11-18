using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class FlowDeleteApiTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // Postgres, not InMemory: the delete runs in a transaction with ExecuteDelete, and so does the eraser.
    private DocumentEngineTestFactory factory = null!;

    public async Task InitializeAsync() =>
        factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("proculink_flowdelete") };

    public Task DisposeAsync() { factory.Dispose(); return Task.CompletedTask; }

    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;

    private static async Task<Guid> NewFlow(HttpClient http, string name = "Standards test")
    {
        var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest(name));
        return (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id;
    }

    private static async Task<Guid> UploadCsv(HttpClient http, Guid flowId)
    {
        var res = await http.PostAsync($"/api/flows/{flowId}/documents",
            new MultipartFormDataContent { { new ByteArrayContent("sku,qty\nA-1,2\n"u8.ToArray()), "file", "orders.csv" } });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<DocumentSummaryDto>(DocumentJson.Options))!.Id;
    }

    private ProcuLinkDbContext Db() => factory.Services.CreateScope().ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

    [DockerRequiredFact]
    public async Task Delete_removes_an_unused_flow_its_sample_and_everything_only_it_owns()
    {
        var http = factory.ClientFor(OrgA);
        var flowId = await NewFlow(http);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/flows/{flowId}/intakes")).StatusCode);
        Assert.True((await http.PostAsync($"/api/flows/{flowId}/documents/sample", null)).IsSuccessStatusCode);
        var realId = await UploadCsv(http, flowId);
        var now = DateTime.UtcNow;
        var destinationId = Guid.NewGuid();
        var lookupId = Guid.NewGuid();
        string sampleKey, realKey;
        Guid emailIntakeId;
        await using (var db = Db())
        {
            db.Set<FlowDestination>().Add(new FlowDestination { Id = destinationId, OrgId = OrgA, Name = "Accounting server", Protocol = "http", CreatedAt = now, UpdatedAt = now });
            db.Set<FlowOutput>().Add(new FlowOutput { Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flowId, Name = "Invoice CSV", Format = "csv", DestinationId = destinationId, Logo = [1, 2, 3], CreatedAt = now, UpdatedAt = now });
            db.Set<FlowSortMemory>().Add(new FlowSortMemory { Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flowId, SenderKey = "name:test", Kind = "invoice", CreatedAt = now, UpdatedAt = now });
            db.FlowIntakeItems.Add(new FlowIntakeItem { Id = Guid.NewGuid(), OrgId = OrgA, ScopeId = flowId, ItemKey = "api:test", CreatedAt = now });
            emailIntakeId = await db.FlowIntakes.Where(i => i.OrgId == OrgA && i.FlowId == flowId && i.Kind == IntakeKind.Email).Select(i => i.Id).SingleAsync();
            db.FlowIntakeItems.Add(new FlowIntakeItem { Id = Guid.NewGuid(), OrgId = OrgA, ScopeId = emailIntakeId, ItemKey = "email:test", CreatedAt = now });
            db.Set<Lookup>().Add(new Lookup { Id = lookupId, OrgId = OrgA, Name = "Codes", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            sampleKey = await db.FlowDocuments.Where(d => d.FlowId == flowId && d.IsSample).Select(d => d.SourceFileKey).SingleAsync();
            realKey = await db.FlowDocuments.Where(d => d.Id == realId).Select(d => d.SourceFileKey).SingleAsync();
        }

        // A document still being read: refused before anything is erased.
        await using (var db = Db())
            await db.FlowDocuments.Where(d => d.FlowId == flowId && d.IsSample).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DocumentStatus.Reading));
        var busy = await http.DeleteAsync($"/api/flows/{flowId}");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("flow_busy", await busy.Content.ReadAsStringAsync());
        await using (var db = Db())
        {
            Assert.Equal(2, await db.FlowDocuments.CountAsync(d => d.OrgId == OrgA && d.FlowId == flowId));
            await db.FlowDocuments.Where(d => d.FlowId == flowId && d.IsSample).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DocumentStatus.Received));
        }

        var res = await http.DeleteAsync($"/api/flows/{flowId}");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/flows/{flowId}")).StatusCode);
        await using (var db = Db())
        {
            Assert.False(await db.FlowDocuments.AnyAsync(d => d.OrgId == OrgA && d.FlowId == flowId));
            Assert.False(await db.Set<FlowOutput>().AnyAsync(o => o.OrgId == OrgA && o.FlowId == flowId));
            Assert.False(await db.FlowIntakes.AnyAsync(i => i.OrgId == OrgA && i.FlowId == flowId));
            Assert.False(await db.OrgInboundAddresses.AnyAsync(a => a.OrganisationId == OrgA && a.FlowId == flowId));
            Assert.False(await db.FlowIntakeItems.AnyAsync(i => i.OrgId == OrgA && i.ScopeId == flowId));
            Assert.False(await db.FlowIntakeItems.AnyAsync(i => i.OrgId == OrgA && i.ScopeId == emailIntakeId));
            Assert.False(await db.Set<FlowSortMemory>().AnyAsync(m => m.OrgId == OrgA && m.FlowId == flowId));
            Assert.True(await db.Set<FlowDestination>().AnyAsync(d => d.Id == destinationId));
            Assert.True(await db.Set<Lookup>().AnyAsync(l => l.Id == lookupId));
            var audit = await db.AuditEvents.SingleAsync(e => e.OrgId == OrgA && e.EntityId == flowId && e.Action == "flow.deleted");
            Assert.Equal("Standards test", audit.Payload!.RootElement.GetProperty("flow").GetString());
        }
        factory.Storage.Verify(s => s.DeleteAsync(sampleKey, It.IsAny<CancellationToken>()), Times.Once);
        factory.Storage.Verify(s => s.DeleteAsync(realKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [DockerRequiredFact]
    public async Task Delete_refuses_a_flow_that_was_put_in_use_or_holds_a_real_document()
    {
        var http = factory.ClientFor(OrgA);
        var published = await NewFlow(http, "Published test");
        await http.PutAsJsonAsync($"/api/flows/{published}/fields",
            new FlowFieldList([new FlowField("total_gross", "total_gross", FieldTypes.Money, null, Required: true)]));
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/flows/{published}/publish", null)).StatusCode);

        var del1 = await http.DeleteAsync($"/api/flows/{published}");

        Assert.Equal(HttpStatusCode.Conflict, del1.StatusCode);
        Assert.Contains("flow_in_use", await del1.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/flows/{published}")).StatusCode);

        var withDoc = await NewFlow(http, "Sent test");
        var docId = await UploadCsv(http, withDoc);
        string realKey;
        await using (var db = Db())
        {
            Assert.Equal(1, await db.Set<FlowVersion>().CountAsync(v => v.OrgId == OrgA && v.FlowId == published));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.OrgId == OrgA && e.EntityId == published && e.Action == "flow.published"));
            var now = DateTime.UtcNow;
            db.Set<FlowDelivery>().Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = OrgA, DocumentId = docId, OutputName = "Invoice CSV", Protocol = "http",
                Status = FlowDeliveryStatus.Delivered, DispatchKey = Guid.NewGuid(), ContentType = "text/csv", FileName = "out.csv",
                DeliveredAt = now, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            realKey = await db.FlowDocuments.Where(d => d.Id == docId).Select(d => d.SourceFileKey).SingleAsync();
        }

        var del2 = await http.DeleteAsync($"/api/flows/{withDoc}");

        Assert.Equal(HttpStatusCode.Conflict, del2.StatusCode);
        Assert.Contains("flow_in_use", await del2.Content.ReadAsStringAsync());
        await using (var db = Db())
            Assert.True(await db.FlowDocuments.AnyAsync(d => d.OrgId == OrgA && d.FlowId == withDoc && d.Id == docId));
        factory.Storage.Verify(s => s.DeleteAsync(realKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [DockerRequiredFact]
    public async Task Delete_cannot_reach_another_workspaces_flow_or_the_inbox()
    {
        var httpA = factory.ClientFor(OrgA);
        var flowOfA = await NewFlow(httpA);
        await httpA.GetAsync($"/api/flows/{flowOfA}/intakes");

        var other = await factory.ClientFor(DocumentEngineTestFactory.OrgB).DeleteAsync($"/api/flows/{flowOfA}");

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await httpA.GetAsync($"/api/flows/{flowOfA}")).StatusCode);
        var inboxId = (await httpA.GetFromJsonAsync<InboxDto>("/api/inbox", DocumentJson.Options))!.FlowId;
        Assert.Equal(HttpStatusCode.NotFound, (await httpA.DeleteAsync($"/api/flows/{inboxId}")).StatusCode);
        await using var db = Db();
        Assert.True(await db.FlowIntakes.AnyAsync(i => i.OrgId == OrgA && i.FlowId == flowOfA));
        Assert.True(await db.Flows.AnyAsync(f => f.OrgId == OrgA && f.Id == inboxId && f.IsInbox));
        Assert.True(await db.OrgInboundAddresses.AnyAsync(a => a.OrganisationId == OrgA && a.FlowId == inboxId));
    }
}
