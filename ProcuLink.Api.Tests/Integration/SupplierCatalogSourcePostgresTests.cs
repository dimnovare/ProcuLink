using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// BE-2 gate: the <c>AddSupplierCatalogSources</c> migration is proven on REAL Postgres —
/// not EF InMemory (memory: InMemory masks FK enforcement and migration issues entirely).
///
/// Verifies: (1) the full migration chain applies cleanly including the new table,
/// (2) the unique (org_id, supplier_id) index rejects a second source for the same
/// supplier, and (3) the supplier FK rejects an orphan source. Docker-gated so the suite
/// skips (not fails) where Docker is absent, mirroring <see cref="EndToEndPipelineTests"/>.
/// </summary>
[Collection("postgres-container")]
public sealed class SupplierCatalogSourcePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_catsrc_{Guid.NewGuid():N}")
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
        await migrateDb.Database.MigrateAsync(); // includes AddSupplierCatalogSources
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private static (Organisation Org, Supplier Supplier) NewOrgAndSupplier()
    {
        var orgId = Guid.NewGuid();
        var org = new Organisation
        {
            Id            = orgId,
            ClerkOrgId    = $"org_catsrc_{orgId:N}",
            Name          = "Catalog Source Org",
            Slug          = $"catalog-source-{orgId:N}",
            Plan          = "integration",
            AccountStatus = "active",
            CreatedAt     = DateTime.UtcNow,
        };
        var supplier = new Supplier
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = "Catalog Supplier",
            CreatedAt = DateTime.UtcNow,
        };
        return (org, supplier);
    }

    private static SupplierCatalogSource NewSource(Guid orgId, Guid supplierId) => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = orgId,
        SupplierId = supplierId,
        Protocol   = "sftp",
        Host       = "files.example.com",
        Port       = 22,
        Username   = "catalog",
        RemotePath = "/exports/catalog.csv",
        CreatedAt  = DateTime.UtcNow,
        UpdatedAt  = DateTime.UtcNow,
    };

    [DockerRequiredFact]
    public async Task Migration_Applies_AndRoundTripsARow_WithDbDefaults()
    {
        var (org, supplier) = NewOrgAndSupplier();
        Guid sourceId;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(org);
            db.Suppliers.Add(supplier);
            var source = NewSource(org.Id, supplier.Id);
            sourceId = source.Id;
            db.SupplierCatalogSources.Add(source);
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var row = await db.SupplierCatalogSources
                .AsNoTracking()
                .SingleAsync(s => s.Id == sourceId && s.OrgId == org.Id);

            Assert.Equal("sftp", row.Protocol);
            Assert.Equal("auto", row.FileFormat);       // DB default
            Assert.Equal(24, row.SyncIntervalHours);    // DB default
            Assert.False(row.IsEnabled);                // DB default
            Assert.Null(row.LastSyncStatus);
        }
    }

    [DockerRequiredFact]
    public async Task UniqueIndex_OneSourcePerOrgSupplier_IsEnforced()
    {
        var (org, supplier) = NewOrgAndSupplier();

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(org);
            db.Suppliers.Add(supplier);
            db.SupplierCatalogSources.Add(NewSource(org.Id, supplier.Id));
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.SupplierCatalogSources.Add(NewSource(org.Id, supplier.Id)); // same (org, supplier)
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [DockerRequiredFact]
    public async Task SupplierForeignKey_RejectsOrphanSource()
    {
        var (org, _) = NewOrgAndSupplier();

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(org);
        // No supplier row — the FK must reject the insert (EF InMemory would let this slide).
        db.SupplierCatalogSources.Add(NewSource(org.Id, Guid.NewGuid()));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
