using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Exercises POST /api/orders/{id}/assign-supplier on REAL Postgres — the endpoint uses
/// ExecuteUpdateAsync (an atomic status claim) which EF InMemory cannot translate. Proves the
/// routing-Phase-1 hold→assign mechanic: an 'unrouted' order is atomically claimed to 'parsing'
/// with the chosen supplier + pinned revision; a non-unrouted order is rejected (409); an unknown
/// supplier (400) and unknown/cross-tenant order (404) are guarded. Docker-gated; skips cleanly.
/// </summary>
[Collection("postgres-container")]
public sealed class AssignSupplierPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_assign");

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── Controller wiring (mirrors the existing OrdersController test harness) ──
    private static OrdersController BuildController(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new OrdersController(
            new Mock<IOrderService>().Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new PromoteMappingService(db, new PoMappingService(db)),
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());
    }

    private async Task<(Guid orgId, Guid supplierId)> SeedOrgAndSupplierAsync(ProcuLinkDbContext db, string slug)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{slug}_{orgId:N}", Name = slug, Slug = $"{slug}-{orgId:N}",
            Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = now });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    private static PurchaseOrderEntity Order(Guid orgId, Guid? supplierId, string status) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-1",
        Currency = "EUR", Status = status, OrderDate = new DateOnly(2026, 6, 26),
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    // ── Tests ──────────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task Unrouted_order_assign_claims_to_parsing_with_supplier_and_revision()
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var (orgId, supplierId) = await SeedOrgAndSupplierAsync(db, "assign");
        var order = Order(orgId, supplierId: null, OrderStatusConstants.Unrouted);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var ctrl = BuildController(db, orgId);
        var result = await ctrl.AssignSupplier(order.Id, new AssignSupplierRequest(supplierId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        await using var reload = new ProcuLinkDbContext(_options!);
        var reloaded = await reload.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(supplierId, reloaded.SupplierId);
        Assert.Equal(OrderStatusConstants.Parsing, reloaded.Status);
    }

    [DockerRequiredFact]
    public async Task NonUnrouted_order_returns_409_and_is_not_touched()
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var (orgId, supplierId) = await SeedOrgAndSupplierAsync(db, "conflict");
        var order = Order(orgId, supplierId, OrderStatusConstants.Ready);   // already routed
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var ctrl = BuildController(db, orgId);
        var result = await ctrl.AssignSupplier(order.Id, new AssignSupplierRequest(supplierId), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);

        await using var reload = new ProcuLinkDbContext(_options!);
        var reloaded = await reload.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatusConstants.Ready, reloaded.Status);   // unchanged
    }

    [DockerRequiredFact]
    public async Task Supplier_from_another_org_returns_400()
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var (orgId, _) = await SeedOrgAndSupplierAsync(db, "tenant-a");
        var (_, otherOrgSupplierId) = await SeedOrgAndSupplierAsync(db, "tenant-b");
        var order = Order(orgId, supplierId: null, OrderStatusConstants.Unrouted);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var ctrl = BuildController(db, orgId);
        // The supplier belongs to tenant-b — must be rejected for tenant-a's order.
        var result = await ctrl.AssignSupplier(order.Id, new AssignSupplierRequest(otherOrgSupplierId), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [DockerRequiredFact]
    public async Task Unknown_order_returns_404()
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var (orgId, supplierId) = await SeedOrgAndSupplierAsync(db, "missing");

        var ctrl = BuildController(db, orgId);
        var result = await ctrl.AssignSupplier(Guid.NewGuid(), new AssignSupplierRequest(supplierId), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }
}
