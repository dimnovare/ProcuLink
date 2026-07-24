using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Detection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// BE-2 follow-up gate: <see cref="SupplierCatalogService.DeleteAsync"/> clears a supplier catalog
/// SET-BASED (one <c>DELETE … WHERE org_id = … AND supplier_id = …</c>) instead of loading
/// every row into the change tracker first.
///
/// Why this lives on real Postgres rather than the EF InMemory provider used by
/// <c>SupplierCatalogServiceTests</c>: InMemory does not implement <c>ExecuteDelete</c> and
/// throws on it, so the fixed code path is untestable there. The (org, supplier) scoping
/// assertion that used to live in <c>SupplierCatalogServiceTests</c> moved here verbatim and
/// is strengthened with a cross-ORG case.
///
/// Docker-gated so the suite skips (not fails) where Docker is absent, mirroring
/// <see cref="SupplierCatalogSourcePostgresTests"/>.
/// </summary>
[Collection("postgres-container")]
public sealed class SupplierCatalogDeletePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_catdel_{Guid.NewGuid():N}")
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

    // ── seeding helpers ───────────────────────────────────────────────────────

    private const string SupplierName = "Catalog Supplier";

    private static Organisation NewOrg() => new()
    {
        Id            = Guid.NewGuid(),
        ClerkOrgId    = $"org_catdel_{Guid.NewGuid():N}",
        Name          = "Catalog Delete Org",
        Slug          = $"catalog-delete-{Guid.NewGuid():N}",
        Plan          = "integration",
        AccountStatus = "active",
        CreatedAt     = DateTime.UtcNow,
    };

    private static Supplier NewSupplier(Guid orgId) => new()
    {
        Id        = Guid.NewGuid(),
        OrgId     = orgId,
        Name      = SupplierName,
        CreatedAt = DateTime.UtcNow,
    };

    private static SupplierProduct NewProduct(Guid orgId, Guid supplierId, string code, bool isActive = true) => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = orgId,
        SupplierId = supplierId,
        Code       = code,
        Name       = $"Product {code}",
        IsActive   = isActive,
        CreatedAt  = DateTime.UtcNow,
        UpdatedAt  = DateTime.UtcNow,
    };

    private async Task SeedAsync(params object[] entities)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    // ── scoping (moved from SupplierCatalogServiceTests, InMemory) ─────────────

    [DockerRequiredFact]
    public async Task DeleteAsync_RemovesOnlyTheScopedSupplier()
    {
        var org = NewOrg();
        var supplier1 = NewSupplier(org.Id);
        var supplier2 = NewSupplier(org.Id);

        await SeedAsync(
            org, supplier1, supplier2,
            NewProduct(org.Id, supplier1.Id, "X"),
            NewProduct(org.Id, supplier1.Id, "Y"),
            NewProduct(org.Id, supplier2.Id, "Z"));

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new SupplierCatalogService(db);

        var deleted = await svc.DeleteAsync(org.Id, supplier1.Id, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(0, await svc.CountAsync(org.Id, supplier1.Id, CancellationToken.None));
        Assert.Equal(1, await svc.CountAsync(org.Id, supplier2.Id, CancellationToken.None)); // untouched
    }

    [DockerRequiredFact]
    public async Task DeleteAsync_DoesNotCrossTheOrgBoundary()
    {
        // Tenancy: supplier_id alone must never be the delete key. A supplier row belongs to
        // exactly one org (FK), so the cross-org shape is a MISMATCHED pair — org A's id with
        // org B's supplier — which must match nothing; and org A clearing its own catalog must
        // leave org B's identical code untouched.
        var orgA = NewOrg();
        var orgB = NewOrg();
        var supplierA = NewSupplier(orgA.Id);
        var supplierB = NewSupplier(orgB.Id);

        await SeedAsync(
            orgA, orgB, supplierA, supplierB,
            NewProduct(orgA.Id, supplierA.Id, "SHARED"),
            NewProduct(orgB.Id, supplierB.Id, "SHARED"));

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new SupplierCatalogService(db);

        // Org A's id paired with org B's supplier matches nothing — no cross-tenant delete.
        Assert.Equal(0, await svc.DeleteAsync(orgA.Id, supplierB.Id, CancellationToken.None));

        Assert.Equal(1, await svc.DeleteAsync(orgA.Id, supplierA.Id, CancellationToken.None));
        Assert.Equal(0, await svc.CountAsync(orgA.Id, supplierA.Id, CancellationToken.None));
        Assert.Equal(1, await svc.CountAsync(orgB.Id, supplierB.Id, CancellationToken.None)); // untouched
    }

    [DockerRequiredFact]
    public async Task DeleteAsync_AlsoRemovesDiscontinuedRows()
    {
        // "Clear the catalog" means every row for the (org, supplier) — inactive rows kept
        // for audit included. CountAsync only counts active ones, so assert on the table.
        var org = NewOrg();
        var supplier = NewSupplier(org.Id);

        await SeedAsync(
            org, supplier,
            NewProduct(org.Id, supplier.Id, "LIVE"),
            NewProduct(org.Id, supplier.Id, "DISCONTINUED", isActive: false));

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = new SupplierCatalogService(db);
            Assert.Equal(2, await svc.DeleteAsync(org.Id, supplier.Id, CancellationToken.None));
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            Assert.Equal(0, await verify.SupplierProducts
                .AsNoTracking()
                .CountAsync(p => p.OrgId == org.Id && p.SupplierId == supplier.Id));
        }
    }

    [DockerRequiredFact]
    public async Task DeleteAsync_ReturnsZero_WhenTheCatalogIsAlreadyEmpty()
    {
        var org = NewOrg();
        var supplier = NewSupplier(org.Id);
        await SeedAsync(org, supplier);

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new SupplierCatalogService(db);

        Assert.Equal(0, await svc.DeleteAsync(org.Id, supplier.Id, CancellationToken.None));
    }

    // ── the set-based fix itself ──────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task DeleteAsync_IsSetBased_AndMaterialisesNoRows()
    {
        // The whole point of the fix: clearing a catalog must not scale its memory cost with
        // the row count. The catalog cap is 200,000 rows (SupplierCatalogFileParser
        // .MaxCatalogRows) at ~976 B per tracked SupplierProduct — a ToList + RemoveRange
        // clear is a ~200 MB spike. ChangeTracker.Tracked fires once per entity a query
        // materialises, so zero events is the observable proof the DELETE stayed in the DB.
        var org = NewOrg();
        var supplier = NewSupplier(org.Id);

        await SeedAsync(
            org, supplier,
            NewProduct(org.Id, supplier.Id, "A"),
            NewProduct(org.Id, supplier.Id, "B"),
            NewProduct(org.Id, supplier.Id, "C"));

        await using var db = new ProcuLinkDbContext(_options!);
        var materialised = 0;
        db.ChangeTracker.Tracked += (_, e) =>
        {
            if (e.Entry.Entity is SupplierProduct) materialised++;
        };

        var svc = new SupplierCatalogService(db);
        var deleted = await svc.DeleteAsync(org.Id, supplier.Id, CancellationToken.None);

        Assert.Equal(3, deleted);
        Assert.Equal(0, materialised);
    }

    // ── endpoint contract (moved from SuppliersControllerCatalogTests, InMemory) ──

    [DockerRequiredFact]
    public async Task ClearCatalogEndpoint_DeletesAll_ForThatSupplier()
    {
        // DELETE /api/suppliers/{id}/catalog end-to-end through the controller. Moved off the
        // InMemory provider for the same reason as the service tests. The 404 half of the
        // endpoint contract (ClearCatalog_ForeignSupplier_Returns404) stays on InMemory — it
        // returns before reaching the service.
        var org = NewOrg();
        var supplier = NewSupplier(org.Id);

        await SeedAsync(
            org, supplier,
            NewProduct(org.Id, supplier.Id, "A"),
            NewProduct(org.Id, supplier.Id, "B"));

        await using var db = new ProcuLinkDbContext(_options!);

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(org.Id);

        var controller = new SuppliersController(
            new Mock<ISupplierProfileRepository>().Object,
            new Mock<IItemMappingService>().Object,
            db,
            tenant.Object,
            new Mock<IBillingService>().Object,
            new Mock<IPoMappingService>().Object,
            new Mock<IDeliveryConfigService>().Object,
            new Mock<IDeliveryService>().Object,
            new TestDoubles.FakeAnalyticsService(),
            new Mock<IFileStorageService>().Object,
            new SourceColumnExtractor(),
            new ProcuLink.Api.Services.StarterTemplates.StarterTemplateService(),
            new SupplierCatalogService(db), // real — persists to the container
            new Mock<ISupplierConnectionService>().Object);

        var result = await controller.ClearCatalog(supplier.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        int deleted = ((dynamic)ok.Value!).deleted;
        Assert.Equal(2, deleted);

        await using var verify = new ProcuLinkDbContext(_options!);
        Assert.Equal(0, await verify.SupplierProducts.AsNoTracking().CountAsync());
    }

    [DockerRequiredFact]
    public async Task DeleteAsync_DoesNotFlushTheCallersUnrelatedPendingChanges()
    {
        // ExecuteDelete commits immediately and OUTSIDE the context's pending changes — the
        // known trap. The half-apply hazard runs the other way for the old ToList +
        // SaveChanges shape: SaveChanges flushed whatever else the caller happened to have
        // tracked, committing edits the caller had not asked to persist yet.
        //
        // The only production caller (SuppliersController.ClearCatalog) tracks nothing across
        // the call — SupplierExistsAsync is a read-only AnyAsync and CurrentTenantService
        // never touches the DbContext — so neither shape can half-apply its intent today.
        // This test pins the contract so a future caller cannot be surprised silently.
        var org = NewOrg();
        var supplier = NewSupplier(org.Id);

        await SeedAsync(org, supplier, NewProduct(org.Id, supplier.Id, "A"));

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var tracked = await db.Suppliers.SingleAsync(s => s.Id == supplier.Id);
            tracked.Name = "EDITED BUT NOT SAVED";

            var svc = new SupplierCatalogService(db);
            Assert.Equal(1, await svc.DeleteAsync(org.Id, supplier.Id, CancellationToken.None));

            // The caller's edit is still pending, not committed.
            Assert.Equal(EntityState.Modified, db.Entry(tracked).State);
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var row = await verify.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplier.Id);
            Assert.Equal(SupplierName, row.Name);

            // …while the delete itself did commit.
            Assert.Equal(0, await verify.SupplierProducts
                .AsNoTracking()
                .CountAsync(p => p.OrgId == org.Id && p.SupplierId == supplier.Id));
        }
    }
}
