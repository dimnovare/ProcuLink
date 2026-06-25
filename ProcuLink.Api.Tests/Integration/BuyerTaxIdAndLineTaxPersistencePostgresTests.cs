using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves on REAL Postgres (not EF InMemory) that the new nullable columns added by
/// <c>AddBuyerTaxIdAndLineTax</c> are REAL persisted schema: <c>purchase_orders.buyer_tax_id</c> and
/// <c>purchase_order_lines.tax_amount</c> survive an INSERT + fresh-context reload. An EF-Ignored
/// property or a forgotten migration column would silently vanish here (the repo's hard-won lesson:
/// InMemory masks Postgres, and an EF-Ignored / ExecuteUpdate field silently drops). Docker-gated.
/// </summary>
[Collection("postgres-container")]
public sealed class BuyerTaxIdAndLineTaxPersistencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_buyertax_{Guid.NewGuid():N}")
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
    public async Task BuyerTaxId_and_line_TaxAmount_survive_reload()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var lineId     = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_buyertax_{orgId:N}",
                Name          = "Buyer Tax Org",
                Slug          = $"buyertax-{orgId:N}",
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
                PoNumber   = "REDACTED-ITEM",
                Currency   = "NOK",
                Status     = "ready",
                OrderDate  = new DateOnly(2026, 6, 15),
                CreatedAt  = now,
                UpdatedAt  = now,
                // The buyer's own org number (NOT the supplier connection's configured From VatNr).
                BuyerTaxId = "REDACTED-TAXID",
                Lines =
                {
                    new PurchaseOrderLineEntity
                    {
                        Id            = lineId,
                        OrderId       = orderId,
                        LineNumber    = 1,
                        BuyerItemCode = "B-1",
                        Quantity      = 1m,
                        UnitPrice     = 3337.08m,
                        // ex-VAT unit price; line carries the printed 25% VAT amount.
                        TaxRate       = 25m,
                        TaxAmount     = 834.27m,
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var o = await db.PurchaseOrders.Include(x => x.Lines).AsNoTracking().SingleAsync(x => x.Id == orderId);

            Assert.Equal("REDACTED-TAXID", o.BuyerTaxId);

            var line = Assert.Single(o.Lines);
            Assert.Equal(25m, line.TaxRate);
            Assert.Equal(834.27m, line.TaxAmount);
        }
    }
}
