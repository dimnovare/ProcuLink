using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Real-Postgres proof that resolving a line on the review screen — which always sends
/// <c>saveMappings: true</c> — still works on an order that has no supplier.
///
/// <see cref="OrderResolutionService.ResolveAsync"/> used to learn the mapping under
/// <c>entity.SupplierId ?? Guid.Empty</c>. There is no supplier row with that id, so the
/// <c>item_mappings.supplier_id</c> foreign key refused the insert, the whole resolve rolled back, and
/// the operator got a 500 for fixing a line. <c>AcceptAiSuggestionsAsync</c> already skipped learning
/// for a supplier-less order; this pins the one-line path to the same rule. It runs on Postgres
/// because the EF InMemory provider enforces no foreign keys, so it cannot see the failure at all.
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class ResolveSaveMappingsWithoutSupplierPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_resolvenosupplier");

        var cs = new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    [DockerRequiredFact]
    public async Task Resolving_a_line_with_save_mappings_on_an_order_with_no_supplier_resolves_it_and_learns_nothing()
    {
        var (orgId, _, orderId) = await SeedAsync(withSupplier: false);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildResolution(db).ResolveAsync(
                orgId, orderId, [new LineResolution(1, "SUP-1")], saveMappings: true, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        Assert.False(await verify.ItemMappings.AnyAsync(m => m.OrgId == orgId),
            "an order with no supplier has nothing to scope a mapping to");
        var line = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == orderId);
        Assert.Equal("SUP-1", line.SupplierItemCode);
        Assert.False(line.NeedsReview);
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal("ready", order.Status);
        // The audit row still records what was asked for, and now also what was actually written.
        var payload = (await ResolvedAuditAsync(verify, orderId)).RootElement;
        Assert.True(payload.GetProperty("savedMappings").GetBoolean());
        Assert.Equal(0, payload.GetProperty("learnedMappings").GetInt32());
    }

    [DockerRequiredFact]
    public async Task Resolving_a_line_with_save_mappings_on_an_order_with_a_supplier_learns_the_mapping()
    {
        // The control: the same resolve on a routed order DOES write the mapping. Without it the test
        // above would also pass if saveMappings had stopped learning anything at all.
        var (orgId, supplierId, orderId) = await SeedAsync(withSupplier: true);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildResolution(db).ResolveAsync(
                orgId, orderId, [new LineResolution(1, "SUP-1")], saveMappings: true, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        var mapping = await verify.ItemMappings.AsNoTracking().SingleAsync(m => m.OrgId == orgId);
        Assert.Equal(supplierId, mapping.SupplierId);
        Assert.Equal("BUY-1", mapping.BuyerItemCode);
        Assert.Equal("SUP-1", mapping.SupplierItemCode);
        Assert.Equal("manual", mapping.Source);
        var payload = (await ResolvedAuditAsync(verify, orderId)).RootElement;
        Assert.Equal(1, payload.GetProperty("learnedMappings").GetInt32());
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<System.Text.Json.JsonDocument> ResolvedAuditAsync(ProcuLinkDbContext db, Guid orderId)
    {
        var audit = await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.EntityId == orderId && a.Action == "Resolved");
        return audit.Payload!;
    }

    private static OrderResolutionService BuildResolution(ProcuLinkDbContext db)
    {
        var shared = new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance);
        return new OrderResolutionService(
            db,
            new ItemMappingService(db),
            NullLogger<OrderService>.Instance,
            shared,
            new AiSuggestionDecisionService(db));
    }

    private async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedAsync(bool withSupplier)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_rns_{orgId:N}", Name = "Resolve No Supplier Org",
            Slug = $"rns-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        if (withSupplier)
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "RNS Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        var order = new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = withSupplier ? supplierId : null, PoNumber = "PO-RNS-1",
            Currency = "EUR", Status = "pending_review", CreatedAt = now, UpdatedAt = now,
        };
        order.Lines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), LineNumber = 1, BuyerItemCode = "BUY-1",
            SupplierItemCode = null, NeedsReview = true, Quantity = 1m, UnitPrice = 10m,
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId);
    }
}
