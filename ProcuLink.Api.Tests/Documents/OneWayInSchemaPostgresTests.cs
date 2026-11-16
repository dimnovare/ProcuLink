using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
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
