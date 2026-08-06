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
/// Real-Postgres proof that the connection-level price-variance guard HOLDs an order when a PO
/// line's unit price diverges from the catalog price past the connection threshold — and does NOT
/// hold when within threshold. Catalog price is a SUGGESTION: the guard sets <c>NeedsReview</c> +
/// a reason and forces <c>pending_review</c>, but NEVER mutates the PO <c>UnitPrice</c>.
/// Exercised through the real <see cref="OrderResolutionService.ResolveAsync"/> (the guard's wiring
/// site) against a migrated Postgres schema, so the new <c>price_variance_*</c> columns and the
/// catalog query are proven for real. Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class PriceVarianceGuardPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_pvguard");

        var cs = new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    [DockerRequiredFact]
    public async Task Diverging_unit_price_holds_the_order_and_never_overwrites_the_po_price()
    {
        var (orgId, supplierId, orderId) = await SeedAsync(
            guardEnabled: true, thresholdPercent: 20m, catalogPrice: 100m, poUnitPrice: 130m); // +30% > 20%

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = BuildResolution(db);
            // Resolve the line to the catalog code "SUP-1" — the resolve clears NeedsReview, but the
            // guard must RE-set it because 130 vs catalog 100 is a 30% breach.
            var result = await svc.ResolveAsync(
                orgId, orderId, new[] { new LineResolution(1, "SUP-1") }, saveMappings: false, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            var line  = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == orderId);

            Assert.Equal("pending_review", order.Status);         // HELD
            Assert.True(line.NeedsReview);                         // line flagged
            Assert.NotNull(line.ReviewReason);
            Assert.Contains("catalog", line.ReviewReason!);
            Assert.Equal(130m, line.UnitPrice);                    // PO price UNTOUCHED — catalog is a suggestion
            Assert.Equal("SUP-1", line.SupplierItemCode);
        }
    }

    [DockerRequiredFact]
    public async Task Within_threshold_does_not_hold_the_order()
    {
        var (orgId, supplierId, orderId) = await SeedAsync(
            guardEnabled: true, thresholdPercent: 20m, catalogPrice: 100m, poUnitPrice: 110m); // +10% ≤ 20%

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = BuildResolution(db);
            var result = await svc.ResolveAsync(
                orgId, orderId, new[] { new LineResolution(1, "SUP-1") }, saveMappings: false, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            var line  = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == orderId);

            Assert.Equal("ready", order.Status);   // NOT held
            Assert.False(line.NeedsReview);
            Assert.Null(line.ReviewReason);
        }
    }

    [DockerRequiredFact]
    public async Task Guard_disabled_never_holds_even_on_large_drift()
    {
        var (orgId, supplierId, orderId) = await SeedAsync(
            guardEnabled: false, thresholdPercent: 20m, catalogPrice: 100m, poUnitPrice: 999m); // huge drift, but guard OFF

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = BuildResolution(db);
            var result = await svc.ResolveAsync(
                orgId, orderId, new[] { new LineResolution(1, "SUP-1") }, saveMappings: false, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal("ready", order.Status); // guard off → byte-identical to today
        }
    }

    [DockerRequiredFact]
    public async Task Re_resolving_is_idempotent_does_not_thrash_status()
    {
        var (orgId, supplierId, orderId) = await SeedAsync(
            guardEnabled: true, thresholdPercent: 20m, catalogPrice: 100m, poUnitPrice: 130m); // breach

        // First resolve → held.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            await BuildResolution(db).ResolveAsync(
                orgId, orderId, new[] { new LineResolution(1, "SUP-1") }, saveMappings: false, CancellationToken.None);
        }
        // Second resolve, same input → still held (no double-hold / no flip to ready).
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildResolution(db).ResolveAsync(
                orgId, orderId, new[] { new LineResolution(1, "SUP-1") }, saveMappings: false, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            var line  = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == orderId);
            Assert.Equal("pending_review", order.Status);
            Assert.True(line.NeedsReview);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

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

    private async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedAsync(
        bool guardEnabled, decimal thresholdPercent, decimal catalogPrice, decimal poUnitPrice)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_pv_{orgId:N}", Name = "PV Org",
            Slug = $"pv-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "PV Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        // The connection carries the guard config.
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, Name = "PV Conn",
            ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
            PriceVarianceGuardEnabled = guardEnabled,
            PriceVarianceThresholdPercent = thresholdPercent,
        });

        // The catalog "ground truth" price for SUP-1.
        db.SupplierProducts.Add(new SupplierProduct
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = "SUP-1", Name = "Widget", Price = catalogPrice, Currency = "EUR",
            IsActive = true, CreatedAt = now, UpdatedAt = now,
        });

        // The order: one line still needing review, priced to (maybe) breach.
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-PV-1",
            Currency = "EUR", Status = "pending_review", CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), LineNumber = 1, BuyerItemCode = "BUY-1",
                    SupplierItemCode = null, NeedsReview = true,
                    Quantity = 1m, UnitPrice = poUnitPrice,
                },
            },
        });
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId);
    }
}
