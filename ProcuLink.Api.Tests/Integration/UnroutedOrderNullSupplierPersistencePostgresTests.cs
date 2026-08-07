using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves on REAL Postgres (not EF InMemory) that an <c>unrouted</c> order persists with a NULL
/// <c>supplier_id</c> and reloads cleanly. This is the load-bearing Phase-0 guarantee of the
/// supplier-routing track: an order can be ingested BEFORE its supplier is known. The migration
/// <c>MakeOrderSupplierNullable</c> drops the NOT NULL + makes the FK <c>ON DELETE SET NULL</c>;
/// InMemory cannot enforce nullability/FK semantics, so this Docker-gated test is the real proof.
/// A second case pins that a NORMAL order (supplier set) still round-trips unchanged — byte-parity
/// for the existing path. Skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class UnroutedOrderNullSupplierPersistencePostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_unrouted");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    [DockerRequiredFact]
    public async Task Unrouted_order_persists_with_null_supplier_and_reloads()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now     = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_unrouted_{orgId:N}",
                Name          = "Unrouted Org",
                Slug          = $"unrouted-{orgId:N}",
                Plan          = "operations",
                AccountStatus = "active",
                CreatedAt     = now,
            });
            await db.SaveChangesAsync();

            // No supplier seeded, no SupplierId set — the order arrived before routing.
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = orderId,
                OrgId      = orgId,
                SupplierId = null,
                PoNumber   = "PO-UNROUTED-1",
                Currency   = "EUR",
                Status     = OrderStatusConstants.Unrouted,
                OrderDate  = new DateOnly(2026, 6, 26),
                CreatedAt  = now,
                UpdatedAt  = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var o = await db.PurchaseOrders.AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.Null(o.SupplierId);
            Assert.Equal(OrderStatusConstants.Unrouted, o.Status);
            Assert.Equal("PO-UNROUTED-1", o.PoNumber);
        }
    }

    [DockerRequiredFact]
    public async Task Normal_order_with_supplier_still_round_trips_unchanged()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_routed_{orgId:N}",
                Name          = "Routed Org",
                Slug          = $"routed-{orgId:N}",
                Plan          = "operations",
                AccountStatus = "active",
                CreatedAt     = now,
            });
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = now });
            await db.SaveChangesAsync();

            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = orderId,
                OrgId      = orgId,
                SupplierId = supplierId,
                PoNumber   = "PO-ROUTED-1",
                Currency   = "EUR",
                Status     = OrderStatusConstants.Ready,
                OrderDate  = new DateOnly(2026, 6, 26),
                CreatedAt  = now,
                UpdatedAt  = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var o = await db.PurchaseOrders.AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.Equal(supplierId, o.SupplierId);
            Assert.Equal(OrderStatusConstants.Ready, o.Status);
        }
    }
}
