using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Auth;
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

    private async Task<(bool Detail, bool Row)> CanRemove(HttpClient http, FlowDocument doc)
    {
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{doc.Id}");
        var page = await http.GetFromJsonAsync<JsonElement>($"/api/documents?flowId={doc.FlowId}&page=1&pageSize=50");
        return (detail.GetProperty("canRemove").GetBoolean(), page.GetProperty("items")[0].GetProperty("canRemove").GetBoolean());
    }

    [DockerRequiredFact]
    public async Task An_admin_removes_a_sent_document_the_month_still_counts_it_and_its_flow_history_keeps_it()
    {
        var doc = await Seed(OrgA, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1, delivery: FlowDeliveryStatus.Stored);
        var before = await OrdersThisMonth(OrgA);
        var admin = factory.ClientFor(OrgA);
        Assert.Equal((false, false), await CanRemove(Member(OrgA), doc));
        Assert.Equal((true, true), await CanRemove(admin, doc));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/documents/{doc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{doc.Id}")).StatusCode);
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
        var http = factory.ClientFor(OrgA);
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
    public async Task A_member_removes_an_unread_duplicate_but_never_what_was_sent_or_another_workspaces()
    {
        var member = Member(OrgA);
        var sent = await Seed(OrgA, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1, delivery: FlowDeliveryStatus.Delivered);
        var refused = await member.DeleteAsync($"/api/documents/{sent.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(RequireOrgAdminAttribute.ErrorCode, await refused.Content.ReadAsStringAsync());
        Assert.True(await Exists(sent.Id));
        // Downloaded: sent with no delivery row. Still refused.
        var downloaded = await Seed(OrgA, DocumentStatus.Sent, reason: DocumentSort.Downloaded, readAt: DateTime.UtcNow, units: 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/documents/{downloaded.Id}")).StatusCode);
        Assert.True(await Exists(downloaded.Id));
        Assert.Empty(await Removals());

        var duplicate = await Seed(OrgA, DocumentStatus.NeedsReview, reason: "duplicate_file");
        Assert.Equal((true, true), await CanRemove(member, duplicate));
        var counted = await OrdersThisMonth(OrgA);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/documents/{duplicate.Id}")).StatusCode);
        await using (var db = Db())
            Assert.False(await db.ErasedDocumentUnits.AnyAsync(u => u.OrgId == OrgA));
        Assert.Equal(counted, await OrdersThisMonth(OrgA));
        Assert.Single(await Removals());

        var theirs = await Seed(OrgB, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1);
        var missing = await member.DeleteAsync($"/api/documents/{theirs.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("document_not_found", await missing.Content.ReadAsStringAsync());
        Assert.True(await Exists(theirs.Id));
    }
}