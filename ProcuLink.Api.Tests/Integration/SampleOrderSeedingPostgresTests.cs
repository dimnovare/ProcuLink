using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Sample-order catalog/mapping seeding proven on REAL Postgres — not EF InMemory.
///
/// <para>Why: per project memory, EF InMemory does not enforce FKs or insert ordering — a
/// seed that adds <see cref="SupplierProduct"/>/<see cref="ItemMapping"/> rows referencing a
/// brand-new (unsaved) supplier passes InMemory but would throw on Postgres if EF mis-ordered
/// the inserts. This test runs <see cref="SampleOrderService.CreateAndEnqueueAsync"/> twice
/// against a throwaway Postgres (fresh DbContext per run, so the second run's idempotency
/// existence-checks hit the database, not the change tracker) and round-trips the seeded
/// rows. Docker-gated (mirrors <see cref="EndToEndPipelineTests"/>) so it skips where Docker
/// is absent instead of failing the suite.</para>
/// </summary>
public sealed class SampleOrderSeedingPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_sample_{Guid.NewGuid():N}")
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
    public async Task Seeding_RoundTrips_AndStaysIdempotent_OnRealPostgres()
    {
        var orgId   = Guid.NewGuid();
        var now     = DateTime.UtcNow;
        var storage = new InMemoryFileStorage();

        // FK target: the org row must exist before supplier/order/catalog inserts
        // (Postgres enforces this; InMemory would silently let it slide).
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_sample_{orgId:N}",
                Name          = "Sample Seeding Org",
                Slug          = $"sample-seed-{orgId:N}",
                Plan          = "pilot",
                AccountStatus = "trialing",
                CreatedAt     = now,
            });
            await db.SaveChangesAsync();
        }

        // Run 1 — brand-new supplier + seeds in ONE SaveChanges: proves EF orders the
        // supplier insert before the catalog/mapping rows that reference it.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = new SampleOrderService(db, new NoOpParseJobEnqueuer(), storage, new FakeAnalyticsService());
            await svc.CreateAndEnqueueAsync(orgId, "user_pg", CancellationToken.None);
        }

        // Run 2 — fresh context: idempotency must come from the DATABASE state.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = new SampleOrderService(db, new NoOpParseJobEnqueuer(), storage, new FakeAnalyticsService());
            await svc.CreateAndEnqueueAsync(orgId, "user_pg", CancellationToken.None);
        }

        // Round-trip verification on a third fresh context.
        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var supplier = await verify.Suppliers.SingleAsync(s => s.OrgId == orgId && s.IsSample);
            Assert.Equal("__sample__", supplier.Code);

            var catalog = await verify.SupplierProducts
                .Where(p => p.OrgId == orgId && p.SupplierId == supplier.Id)
                .OrderBy(p => p.Code)
                .ToListAsync();
            Assert.Equal(5, catalog.Count);

            // Fixture line 3's code is catalog-only (the deliberate manual-rep gap)
            // and its values survive the Postgres round-trip.
            var bracket = Assert.Single(catalog, p => p.Code == "SMP-BRACKET-S");
            Assert.Equal("Bracket short", bracket.Name);
            Assert.Equal(1.95m, bracket.Price);
            Assert.Equal("EUR", bracket.Currency);
            Assert.True(bracket.IsActive);

            var mappings = await verify.ItemMappings
                .Where(m => m.OrgId == orgId && m.SupplierId == supplier.Id)
                .ToListAsync();
            Assert.Equal(2, mappings.Count);
            Assert.Contains(mappings, m => m.BuyerItemCode == "ACME-WIDGET-A" && m.SupplierItemCode == "SMP-WIDGET-A-10");
            Assert.Contains(mappings, m => m.BuyerItemCode == "ACME-WIDGET-B" && m.SupplierItemCode == "SMP-WIDGET-B-20");
            Assert.DoesNotContain(mappings, m => m.BuyerItemCode == "ACME-BRACKET-S");

            // Two runs → two sample orders (orders are per-run), but no seed duplicates.
            var orderCount = await verify.PurchaseOrders.CountAsync(o => o.OrgId == orgId && o.IsSample);
            Assert.Equal(2, orderCount);
        }
    }

    private sealed class NoOpParseJobEnqueuer : IParseJobEnqueuer
    {
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
