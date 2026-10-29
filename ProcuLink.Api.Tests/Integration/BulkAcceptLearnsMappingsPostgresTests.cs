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
/// Real-Postgres proof that bulk "accept AI suggestions" teaches the item mapping for every line it
/// accepts, the way a one-line accept on the review screen does — against the real schema, so the
/// item_mappings unique index and supplier foreign key are in force, and inside the same explicit
/// transaction shape as <see cref="OrderResolutionService.ResolveAsync"/>.
///
/// The rollback test is the reason this runs on Postgres: <c>IItemMappingService.UpsertAsync</c>
/// saves the shared context itself, so the first learned mapping also flushes the accepted lines.
/// Only a real transaction can prove a later failure leaves nothing half-written; the EF InMemory
/// provider has no transactions. Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class BulkAcceptLearnsMappingsPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_bulklearn");

        var cs = new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    [DockerRequiredFact]
    public async Task Bulk_accept_saves_one_suggested_mapping_per_buyer_code_with_the_model_score()
    {
        // Two lines of the same product on one order must converge on ONE mapping row (the unique
        // index on org + supplier + buyer code would refuse a second insert).
        var (orgId, supplierId, orderId) = await SeedAsync(withSupplier: true,
            ("BUY-1", "SUP-1", 0.91f),
            ("BUY-1", "SUP-1", 0.93f),
            ("BUY-2", "SUP-2", 0.95f));

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildResolution(db).AcceptAiSuggestionsAsync(orgId, orderId, 0.85, CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.Equal(3, result.Value);
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        var mappings = await verify.ItemMappings.AsNoTracking()
            .Where(m => m.OrgId == orgId && m.SupplierId == supplierId)
            .OrderBy(m => m.BuyerItemCode)
            .ToListAsync();

        Assert.Collection(mappings,
            m =>
            {
                Assert.Equal("BUY-1", m.BuyerItemCode);
                Assert.Equal("SUP-1", m.SupplierItemCode);
                Assert.Equal("suggested", m.Source);
                Assert.Equal(0.93f, m.Confidence); // the later line's score — the row was updated, not duplicated
            },
            m =>
            {
                Assert.Equal("BUY-2", m.BuyerItemCode);
                Assert.Equal("SUP-2", m.SupplierItemCode);
                Assert.Equal("suggested", m.Source);
                Assert.Equal(0.95f, m.Confidence);
            });

        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal("ready", order.Status);
    }

    [DockerRequiredFact]
    public async Task Bulk_accept_on_an_order_with_no_supplier_accepts_the_lines_and_learns_nothing()
    {
        // There is no supplier to scope a mapping to; writing one under Guid.Empty would violate
        // the item_mappings.supplier_id foreign key and roll the whole accept back.
        var (orgId, _, orderId) = await SeedAsync(withSupplier: false, ("BUY-1", "SUP-1", 0.95f));

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildResolution(db).AcceptAiSuggestionsAsync(orgId, orderId, 0.85, CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value);
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        Assert.False(await verify.ItemMappings.AnyAsync(m => m.OrgId == orgId));
        var line = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == orderId);
        Assert.Equal("SUP-1", line.SupplierItemCode);
        Assert.False(line.NeedsReview);
    }

    [DockerRequiredFact]
    public async Task A_failure_after_the_mapping_is_learned_rolls_back_the_mapping_and_the_accepted_lines()
    {
        var (orgId, supplierId, orderId) = await SeedAsync(withSupplier: true, ("BUY-1", "SUP-1", 0.95f));

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = BuildResolution(db, new ThrowingDecisionService());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.AcceptAiSuggestionsAsync(orgId, orderId, 0.85, CancellationToken.None));
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        Assert.False(await verify.ItemMappings.AnyAsync(m => m.OrgId == orgId && m.SupplierId == supplierId),
            "the learned mapping must not survive a failed accept");
        var line = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == orderId);
        Assert.True(line.NeedsReview, "the line must still be awaiting review");
        Assert.Null(line.SupplierItemCode);
        Assert.Equal("SUP-1", line.AiSuggestedSupplierItemCode);
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal("pending_review", order.Status);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static OrderResolutionService BuildResolution(
        ProcuLinkDbContext db, IAiSuggestionDecisionService? decisions = null)
    {
        var shared = new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance);
        return new OrderResolutionService(
            db,
            new ItemMappingService(db),
            NullLogger<OrderService>.Instance,
            shared,
            decisions ?? new AiSuggestionDecisionService(db));
    }

    /// <summary>Fails the step that runs after the lines and mappings were saved.</summary>
    private sealed class ThrowingDecisionService : IAiSuggestionDecisionService
    {
        public Task RecordAsync(Guid orgId, Guid orderId, AiSuggestionDecisionRecord record, CancellationToken ct)
            => throw new InvalidOperationException("decision store down");

        public Task RecordManyAsync(Guid orgId, Guid orderId, IEnumerable<AiSuggestionDecisionRecord> records, CancellationToken ct)
            => throw new InvalidOperationException("decision store down");

        public Task<IReadOnlyList<AiSuggestionDecision>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct)
            => throw new InvalidOperationException("decision store down");
    }

    private async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedAsync(
        bool withSupplier, params (string buyerCode, string suggestion, float confidence)[] lines)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_bl_{orgId:N}", Name = "Bulk Learn Org",
            Slug = $"bl-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        if (withSupplier)
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "BL Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        var order = new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = withSupplier ? supplierId : null, PoNumber = "PO-BL-1",
            Currency = "EUR", Status = "pending_review", CreatedAt = now, UpdatedAt = now,
        };
        var lineNumber = 1;
        foreach (var (buyerCode, suggestion, confidence) in lines)
        {
            order.Lines.Add(new PurchaseOrderLineEntity
            {
                Id = Guid.NewGuid(), LineNumber = lineNumber++, BuyerItemCode = buyerCode,
                SupplierItemCode = null, NeedsReview = true, Quantity = 1m, UnitPrice = 10m,
                AiSuggestedSupplierItemCode = suggestion, AiSuggestionConfidence = confidence,
            });
        }
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId);
    }
}
