using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
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
public sealed class CxmlAddressBlockPersistencePostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_cxmladdr");

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
                // Exemple-shaped ship-to + bill-to (denormalised cXML address columns).
                ShipToName       = "Usine EXEMPLE de la REDACTED-PARTY",
                ShipToDeliverTo  = "Testperson Alex",
                ShipToStreet     = "12 rue des Essais B12-3 (CTX_0000)",
                ShipToCity       = "VILLE-EXEMPLE",
                ShipToPostalCode = "63040",
                ShipToCountry    = "FRANCE",
                ShipToEmail      = "ship@buyer.example.com",
                ShipToPhone      = "33100000000",
                BillToName       = "EXEMPLE Comptabilite Fournisseurs",
                BillToDeliverTo  = "Service Comptable",
                BillToStreet     = "Place des Essais Nord",
                BillToCity       = "VILLE-EXEMPLE",
                BillToPostalCode = "63000",
                BillToCountry    = "FRANCE",
                BillToEmail      = "compta@buyer.example.com",
                BillToPhone      = "33100000001",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var o = await db.PurchaseOrders.AsNoTracking().SingleAsync(x => x.Id == orderId);

            Assert.Equal("Usine EXEMPLE de la REDACTED-PARTY", o.ShipToName);
            Assert.Equal("Testperson Alex", o.ShipToDeliverTo);
            Assert.Equal("12 rue des Essais B12-3 (CTX_0000)", o.ShipToStreet);
            Assert.Equal("VILLE-EXEMPLE", o.ShipToCity);
            Assert.Equal("63040", o.ShipToPostalCode);
            Assert.Equal("FRANCE", o.ShipToCountry);
            Assert.Equal("ship@buyer.example.com", o.ShipToEmail);
            Assert.Equal("33100000000", o.ShipToPhone);

            Assert.Equal("EXEMPLE Comptabilite Fournisseurs", o.BillToName);
            Assert.Equal("Service Comptable", o.BillToDeliverTo);
            Assert.Equal("Place des Essais Nord", o.BillToStreet);
            Assert.Equal("VILLE-EXEMPLE", o.BillToCity);
            Assert.Equal("63000", o.BillToPostalCode);
            Assert.Equal("FRANCE", o.BillToCountry);
            Assert.Equal("compta@buyer.example.com", o.BillToEmail);
            Assert.Equal("33100000001", o.BillToPhone);
        }
    }
}
