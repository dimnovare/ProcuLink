using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves on REAL Postgres (not EF InMemory) that the 16 denormalised cXML address columns
/// (ship_to_* / bill_to_*) on <c>purchase_orders</c> are REAL persisted schema: an INSERT setting
/// all 16 survives a fresh-context reload. The migration <c>AddCxmlAddressBlocks</c> creates them;
/// an EF-Ignored property or a forgotten migration column would silently vanish here (the repo's
/// hard-won lesson: InMemory masks Postgres, and an EF-Ignored / ExecuteUpdate field silently
/// drops). Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class CxmlAddressBlockPersistencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_cxmladdr_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [DockerRequiredFact]
    public async Task ShipTo_and_BillTo_address_columns_survive_reload()
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
                ClerkOrgId    = $"org_cxmladdr_{orgId:N}",
                Name          = "cXML Address Org",
                Slug          = $"cxmladdr-{orgId:N}",
                Plan          = "operations",
                AccountStatus = "active",
                CreatedAt     = now,
            });
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "cXML Supplier", CreatedAt = now });
            await db.SaveChangesAsync();

            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = orderId,
                OrgId      = orgId,
                SupplierId = supplierId,
                PoNumber   = "4500012345",
                Currency   = "EUR",
                Status     = "ready",
                OrderDate  = new DateOnly(2024, 1, 15),
                CreatedAt  = now,
                UpdatedAt  = now,
                // REDACTED-PARTY-shaped ship-to + bill-to (denormalised cXML address columns).
                ShipToName       = "REDACTED-PARTY",
                ShipToDeliverTo  = "REDACTED-NAME",
                ShipToStreet     = "REDACTED-ADDRESS)",
                ShipToCity       = "REDACTED-ADDRESS",
                ShipToPostalCode = "63040",
                ShipToCountry    = "FRANCE",
                ShipToEmail      = "redacted@example.invalid",
                ShipToPhone      = "REDACTED-PHONE",
                BillToName       = "REDACTED-PARTY",
                BillToDeliverTo  = "Service Comptable",
                BillToStreet     = "REDACTED-ADDRESS",
                BillToCity       = "REDACTED-ADDRESS",
                BillToPostalCode = "63000",
                BillToCountry    = "FRANCE",
                BillToEmail      = "redacted@example.invalid",
                BillToPhone      = "REDACTED-PHONE",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var o = await db.PurchaseOrders.AsNoTracking().SingleAsync(x => x.Id == orderId);

            Assert.Equal("REDACTED-PARTY", o.ShipToName);
            Assert.Equal("REDACTED-NAME", o.ShipToDeliverTo);
            Assert.Equal("REDACTED-ADDRESS)", o.ShipToStreet);
            Assert.Equal("REDACTED-ADDRESS", o.ShipToCity);
            Assert.Equal("63040", o.ShipToPostalCode);
            Assert.Equal("FRANCE", o.ShipToCountry);
            Assert.Equal("redacted@example.invalid", o.ShipToEmail);
            Assert.Equal("REDACTED-PHONE", o.ShipToPhone);

            Assert.Equal("REDACTED-PARTY", o.BillToName);
            Assert.Equal("Service Comptable", o.BillToDeliverTo);
            Assert.Equal("REDACTED-ADDRESS", o.BillToStreet);
            Assert.Equal("REDACTED-ADDRESS", o.BillToCity);
            Assert.Equal("63000", o.BillToPostalCode);
            Assert.Equal("FRANCE", o.BillToCountry);
            Assert.Equal("redacted@example.invalid", o.BillToEmail);
            Assert.Equal("REDACTED-PHONE", o.BillToPhone);
        }
    }
}
