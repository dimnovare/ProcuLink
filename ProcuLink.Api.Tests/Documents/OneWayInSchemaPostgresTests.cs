using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Ingress;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class OneWayInSchemaPostgresTests(PostgresContainerFixture postgres)
{
    private async Task<(DbContextOptions<ProcuLinkDbContext> Options, Guid OrgId, Guid First, Guid Second)> SeedAsync()
    {
        var cs = await postgres.CreateDatabaseAsync("one_way_in_schema");
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(cs, n => n.CommandTimeout(180)).Options;
        var orgId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(options);
        db.Organisations.Add(new Organisation { Id = orgId, Name = "Alex Example Ltd", Slug = "alex-" + orgId.ToString("N") });
        db.Flows.Add(new Flow { Id = first, OrgId = orgId, Name = "Supplier invoices", CreatedAt = now, UpdatedAt = now });
        db.Flows.Add(new Flow { Id = second, OrgId = orgId, Name = "Item codes", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (options, orgId, first, second);
    }

    private static FlowSortMemory Memory(Guid orgId, Guid flowId) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, SenderKey = "name:alex example supplies", Kind = "invoice",
        FlowId = flowId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [DockerRequiredFact]
    public async Task One_inbox_per_org_and_one_memory_row_per_sender_and_kind()
    {
        var (options, orgId, first, second) = await SeedAsync();
        var now = DateTime.UtcNow;
        await using var db = new ProcuLinkDbContext(options);

        db.Flows.Add(new Flow { Id = Guid.NewGuid(), OrgId = orgId, Name = "New documents", IsInbox = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        db.Flows.Add(new Flow { Id = Guid.NewGuid(), OrgId = orgId, Name = "New documents", IsInbox = true, CreatedAt = now, UpdatedAt = now });
        var inbox = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IngressDedupe.IsUniqueViolation(inbox));

        db.ChangeTracker.Clear();
        db.Set<FlowSortMemory>().Add(Memory(orgId, first));
        await db.SaveChangesAsync();

        db.Set<FlowSortMemory>().Add(Memory(orgId, second));
        var memory = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IngressDedupe.IsUniqueViolation(memory));
    }

    [DockerRequiredFact]
    public async Task Two_people_remembering_the_same_sender_and_kind_keep_one_row_for_the_later_flow()
    {
        var (options, orgId, first, second) = await SeedAsync();
        FlowDocument Doc(Guid flowId) => new() { Id = Guid.NewGuid(), OrgId = orgId, FlowId = flowId, SenderKey = "name:alex example supplies", Kind = "invoice" };
        var (docA, docB) = (Doc(first), Doc(second));
        await using var a = new ProcuLinkDbContext(options);
        await using var b = new ProcuLinkDbContext(options);

        // Both look before either saves: both add a row.
        await DocumentSort.RememberAsync(a, docA, CancellationToken.None);
        await DocumentSort.RememberAsync(b, docB, CancellationToken.None);
        await DocumentSort.SaveRememberedAsync(a, docA, CancellationToken.None);
        await DocumentSort.SaveRememberedAsync(b, docB, CancellationToken.None);

        await using var check = new ProcuLinkDbContext(options);
        var row = Assert.Single(await check.Set<FlowSortMemory>().Where(m => m.OrgId == orgId).ToListAsync());
        Assert.Equal(second, row.FlowId);
    }

    [DockerRequiredFact]
    public async Task Two_set_ups_of_one_inbox_document_make_one_flow()
    {
        using var factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("one_way_in_setup_race") };
        var org = DocumentEngineTestFactory.OrgA;
        var http = factory.ClientFor(org);
        var now = DateTime.UtcNow;
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FileName = "invoice-2292.pdf", ContentType = "application/pdf", SourceFileKey = "k", BillingUnits = 1,
            Kind = "invoice", Status = DocumentStatus.NeedsReview, StatusReason = DocumentSort.NewKind, ReadAt = now, CreatedAt = now, UpdatedAt = now,
            ReadJson = DocumentJson.Serialize(new ReadResult("Invoice",
                [new ReadField("invoice_number", "Invoice number", "INV-2292", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false)])),
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            doc.FlowId = (await InboxFlow.GetOrCreateAsync(db, org, CancellationToken.None)).Id;
            db.FlowDocuments.Add(doc);
            await db.SaveChangesAsync();
        }

        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => http.PostAsJsonAsync($"/api/documents/{doc.Id}/set-up", new { })));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = Assert.Single(await checkDb.Flows.Where(f => f.OrgId == org && !f.IsInbox).ToListAsync());
        Assert.Single(await checkDb.Set<FlowOutput>().Where(o => o.OrgId == org && o.FlowId == flow.Id).ToListAsync());
    }

    [DockerRequiredFact]
    public async Task A_released_claim_can_be_claimed_again_and_a_stale_release_changes_nothing()
    {
        var (options, orgId, first, second) = await SeedAsync();
        var now = DateTime.UtcNow;
        await using (var seed = new ProcuLinkDbContext(options))
        {
            seed.FlowDocuments.Add(new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = orgId, FlowId = first, FileName = "invoice-2292.pdf", ContentType = "application/pdf",
                Status = DocumentStatus.NeedsReview, CreatedAt = now, UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }
        async Task<FlowDocument> LoadAsync() { await using var db = new ProcuLinkDbContext(options); return await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.OrgId == orgId); }

        await using var a = new ProcuLinkDbContext(options);
        var doc = await a.FlowDocuments.SingleAsync(d => d.OrgId == orgId);
        Assert.True(await DocumentSort.ClaimAsync(a, doc, DocumentStatus.Received, CancellationToken.None));
        await DocumentSort.ReleaseAsync(a, doc, first, DocumentStatus.NeedsReview, DocumentStatus.Received);
        Assert.Equal(DocumentStatus.NeedsReview, (await LoadAsync()).Status);

        await using var b = new ProcuLinkDbContext(options);
        Assert.True(await DocumentSort.ClaimAsync(b, await b.FlowDocuments.SingleAsync(d => d.OrgId == orgId), DocumentStatus.Received, CancellationToken.None));
        // A release keyed on a flow the document is not in (its work already moved it on) matches nothing.
        await DocumentSort.ReleaseAsync(b, doc, second, DocumentStatus.NeedsReview, DocumentStatus.Received);
        Assert.Equal(DocumentStatus.Received, (await LoadAsync()).Status);
    }

    [DockerRequiredFact]
    public async Task Backfill_stamps_kinds_across_organisations_in_pages_and_then_stops()
    {
        var (options, orgA, flowA, _) = await SeedAsync();
        var (orgB, flowB) = (Guid.NewGuid(), Guid.NewGuid());
        FlowDocument Doc(Guid orgId, Guid flowId, string? kindLabel) => new()
        {
            Id = Guid.NewGuid(), OrgId = orgId, FlowId = flowId, FileName = "invoice-2292.pdf", ContentType = "application/pdf",
            Status = DocumentStatus.NeedsReview, ReadAt = DateTime.UtcNow, ReadJson = DocumentJson.Serialize(new ReadResult(kindLabel)),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await using (var seed = new ProcuLinkDbContext(options))
        {
            seed.Organisations.Add(new Organisation { Id = orgB, Name = "Sample Traders Ltd", Slug = "sample-" + orgB.ToString("N"), ClerkOrgId = "org_" + orgB.ToString("N") });
            seed.Flows.Add(new Flow { Id = flowB, OrgId = orgB, Name = "Supplier invoices", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            seed.FlowDocuments.AddRange(Doc(orgA, flowA, "Invoice"), Doc(orgB, flowB, "Invoice"), Doc(orgB, flowB, null));
            await seed.SaveChangesAsync();
        }

        await using var db = new ProcuLinkDbContext(options);
        Assert.Equal(2, await DocumentSort.BackfillKindsAsync(db, CancellationToken.None));
        Assert.Equal(0, await DocumentSort.BackfillKindsAsync(db, CancellationToken.None));
        await using var check = new ProcuLinkDbContext(options);
        Assert.Equal(["invoice", "invoice"], await check.FlowDocuments.Where(d => d.Kind != null).Select(d => d.Kind!).ToListAsync());
    }

    [DockerRequiredFact]
    public async Task Deleting_a_flow_removes_its_memory_rows()
    {
        var (options, orgId, first, _) = await SeedAsync();
        await using var db = new ProcuLinkDbContext(options);
        db.Set<FlowSortMemory>().Add(Memory(orgId, first));
        await db.SaveChangesAsync();

        await db.Flows.Where(f => f.Id == first).ExecuteDeleteAsync();

        Assert.Equal(0, await db.Set<FlowSortMemory>().CountAsync());
    }
}
