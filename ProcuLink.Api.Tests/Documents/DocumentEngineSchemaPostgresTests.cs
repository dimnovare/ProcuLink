using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class DocumentEngineSchemaPostgresTests(PostgresContainerFixture postgres)
{
    [DockerRequiredFact]
    public async Task Flow_and_document_round_trip_jsonb()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_docengine");
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(cs, n => n.CommandTimeout(180)).Options;
        var orgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(options))
        {
            db.Organisations.Add(new Organisation { Id = orgId, Name = "Acme Components Ltd", Slug = "acme-" + orgId.ToString("N") });
            db.Flows.Add(new Flow { Id = flowId, OrgId = orgId, Name = "Invoices", FieldsJson = "{\"fields\":[]}", CreatedAt = now, UpdatedAt = now });
            db.FlowDocuments.Add(new FlowDocument
            {
                Id = docId, OrgId = orgId, FlowId = flowId, FileName = "a.pdf", ContentType = "application/pdf",
                SourceFileKey = "k", Sha256 = new string('a', 64), BillingUnits = 1, Status = DocumentStatus.Received,
                ReadJson = "{\"fields\":[{\"key\":\"a\"}]}", CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await using var verify = new ProcuLinkDbContext(options);
        var flow = await verify.Flows.AsNoTracking().SingleAsync(f => f.Id == flowId);
        var doc = await verify.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == docId);
        Assert.Empty(JsonDocument.Parse(flow.FieldsJson).RootElement.GetProperty("fields").EnumerateArray());
        Assert.Equal("a", JsonDocument.Parse(doc.ReadJson).RootElement.GetProperty("fields")[0].GetProperty("key").GetString());
        Assert.Equal(flowId, doc.FlowId);
        Assert.Equal("[]", doc.ChecksJson);
    }

    [DockerRequiredFact]
    public async Task Intake_ledger_rejects_the_same_item_twice()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_docintake");
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(cs, n => n.CommandTimeout(180)).Options;
        var orgId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();

        await using var db = new ProcuLinkDbContext(options);
        db.Organisations.Add(new Organisation { Id = orgId, Name = "Synthetic org", Slug = "synthetic-" + orgId.ToString("N") });
        db.FlowIntakeItems.Add(new FlowIntakeItem { Id = Guid.NewGuid(), OrgId = orgId, ScopeId = scopeId, ItemKey = "path:/in/a.csv", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        db.FlowIntakeItems.Add(new FlowIntakeItem { Id = Guid.NewGuid(), OrgId = orgId, ScopeId = scopeId, ItemKey = "path:/in/a.csv", CreatedAt = DateTime.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ProcuLink.Infrastructure.Services.Ingress.IngressDedupe.IsUniqueViolation(ex));
    }
}
