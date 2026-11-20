using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class DocumentRemoveApiTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // Postgres, not InMemory: the eraser runs in a transaction with ExecuteDelete.
    private DocumentEngineTestFactory factory = null!;

    public async Task InitializeAsync() =>
        factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("proculink_docremove") };

    public Task DisposeAsync() { factory.Dispose(); return Task.CompletedTask; }

    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;
    private static readonly Guid OrgB = DocumentEngineTestFactory.OrgB;

    private ProcuLinkDbContext Db() => factory.Services.CreateScope().ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

    private HttpClient Member(Guid org)
    {
        var http = factory.ClientFor(org);
        http.DefaultRequestHeaders.Remove(RbacTestAuthHandler.RoleHeader);
        http.DefaultRequestHeaders.Add(RbacTestAuthHandler.RoleHeader, "org:member");
        return http;
    }

    private async Task<int> OrdersThisMonth(Guid org)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IBillingService>().GetStatusAsync(org)).OrdersThisMonth;
    }

    private async Task<FlowDocument> Seed(Guid org, string status, string? reason = null, DateTime? readAt = null, int units = 0, string? delivery = null)
    {
        factory.ClientFor(org).Dispose();
        var now = DateTime.UtcNow;
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Supplier invoices", CreatedAt = now, UpdatedAt = now };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "invoice-1042.pdf", ContentType = "application/pdf",
            SourceFileKey = $"docs/{Guid.NewGuid():N}.pdf", Status = status, StatusReason = reason, ReadAt = readAt, BillingUnits = units,
            CreatedAt = now, UpdatedAt = now,
        };
        await using var db = Db();
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        if (delivery is not null)
            db.Set<FlowDelivery>().Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = org, DocumentId = doc.Id, OutputName = "CSV", Status = delivery, DispatchKey = Guid.NewGuid(),
                ArtifactKey = $"out/{Guid.NewGuid():N}.csv", FileName = "invoice-1042.csv", CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();
        return doc;
    }

    private async Task<bool> Exists(Guid id)
    {
        await using var db = Db();
        return await db.FlowDocuments.AnyAsync(d => d.Id == id);
    }

    private async Task<List<AuditEvent>> Removals()
    {
        await using var db = Db();
        return await db.AuditEvents.AsNoTracking().Where(a => a.OrgId == OrgA && a.Action == DocumentRemoval.Action).ToListAsync();
    }

    [DockerRequiredFact]
    public async Task A_member_removes_a_read_document_and_the_month_still_counts_it()
    {
        var doc = await Seed(OrgA, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1, delivery: FlowDeliveryStatus.Stored);
        var before = await OrdersThisMonth(OrgA);
        var http = Member(OrgA);

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/documents/{doc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/documents/{doc.Id}")).StatusCode);
        Assert.Equal(before, await OrdersThisMonth(OrgA));

        var audit = Assert.Single(await Removals());
        Assert.Equal(("Flow", doc.FlowId), (audit.EntityType, audit.EntityId));
        var payload = audit.Payload!.RootElement;
        Assert.Equal("Supplier invoices", payload.GetProperty("flow").GetString());
        Assert.Equal("user_DOC_TEST", payload.GetProperty("by").GetString());
        Assert.DoesNotContain("invoice-1042", JsonSerializer.Serialize(audit.Payload));
    }

    [DockerRequiredFact]
    public async Task Removing_refuses_a_document_being_read_or_sent()
    {
        var http = Member(OrgA);
        foreach (var (status, delivery) in new[]
                 {
                     (DocumentStatus.Received, (string?)null), (DocumentStatus.Reading, null), (DocumentStatus.Sending, null),
                     (DocumentStatus.Ready, FlowDeliveryStatus.Queued),
                 })
        {
            var doc = await Seed(OrgA, status, delivery: delivery);
            var res = await http.DeleteAsync($"/api/documents/{doc.Id}");
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("document_busy", JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
            Assert.True(await Exists(doc.Id));
        }
        Assert.Empty(await Removals());
    }

    [DockerRequiredFact]
    public async Task An_unread_duplicate_never_counts_and_another_workspace_is_not_found()
    {
        var duplicate = await Seed(OrgA, DocumentStatus.NeedsReview, reason: "duplicate_file");
        var http = Member(OrgA);

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/documents/{duplicate.Id}")).StatusCode);
        await using (var db = Db())
            Assert.False(await db.ErasedDocumentUnits.AnyAsync(u => u.OrgId == OrgA));
        Assert.Equal(0, await OrdersThisMonth(OrgA));

        var theirs = await Seed(OrgB, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1);
        var refused = await http.DeleteAsync($"/api/documents/{theirs.Id}");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains("document_not_found", await refused.Content.ReadAsStringAsync());
        Assert.True(await Exists(theirs.Id));
    }
}
