using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Endpoint contract tests for the supplier product catalog:
/// <c>GET /api/suppliers/{id}/catalog</c>, <c>POST /api/suppliers/{id}/catalog/import</c>,
/// and <c>DELETE /api/suppliers/{id}/catalog</c>.
///
/// Wires the REAL <see cref="SupplierCatalogService"/> against the in-memory DB so the import
/// → persist → list round-trip is exercised end-to-end. Asserts org-scoping (cross-tenant 404)
/// and the JSON response shapes.
/// </summary>
public class SuppliersControllerCatalogTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new CatalogTestDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    private static (SuppliersController Controller, Guid OrgId, ProcuLinkDbContext Db) BuildController()
    {
        var db = MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

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
            new SupplierCatalogService(db), // real — persists to in-memory DB
            new Mock<ProcuLink.Core.Services.ISupplierConnectionService>().Object);

        return (controller, orgId, db);
    }

    private static Supplier AddSupplier(ProcuLinkDbContext db, Guid orgId)
    {
        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        return supplier;
    }

    private static IFormFile CsvFile(string content, string fileName = "catalog.csv")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/csv",
        };
    }

    // ── import ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportCatalog_Csv_PersistsRows()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var csv = "code,name,unit,price,barcode\n" +
                  "RES-220,Resistor 220 Ohm,PCS,0.04,4001234000010\n" +
                  "CAP-100,Capacitor 100uF,PCS,0.12,4001234000027\n";

        var result = await controller.ImportCatalog(supplier.Id, CsvFile(csv), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        int created = ((dynamic)ok.Value!).created;
        int updated = ((dynamic)ok.Value!).updated;
        created.Should().Be(2);
        updated.Should().Be(0);

        var rows = await db.SupplierProducts.Where(p => p.OrgId == orgId && p.SupplierId == supplier.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(p => p.Code == "RES-220" && p.Name == "Resistor 220 Ohm" && p.Price == 0.04m
                                         && p.Unit == "PCS" && p.Barcode == "4001234000010");
    }

    [Fact]
    public async Task ImportCatalog_RecognisesAliasColumns_AndIsIdempotent()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        // Aliases: sku→code, description→name, unit_price→price, gtin→barcode.
        var csv1 = "sku,description,unit_price,gtin\nSKU-1,First name,9.99,5012345678900\n";
        await controller.ImportCatalog(supplier.Id, CsvFile(csv1), CancellationToken.None);

        // Re-import same code with new data → updated, not duplicated.
        var csv2 = "sku,description,unit_price\nSKU-1,Updated name,11.50\n";
        var result = await controller.ImportCatalog(supplier.Id, CsvFile(csv2), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        int created = ((dynamic)ok.Value!).created;
        int updated = ((dynamic)ok.Value!).updated;
        created.Should().Be(0);
        updated.Should().Be(1);

        var rows = await db.SupplierProducts.Where(p => p.SupplierId == supplier.Id).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Updated name");
        rows[0].Price.Should().Be(11.50m);
    }

    [Fact]
    public async Task ImportCatalog_NoCodeColumn_Returns400()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var csv = "name,price\nSomething,1.00\n";
        var result = await controller.ImportCatalog(supplier.Id, CsvFile(csv), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.SupplierProducts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ImportCatalog_EmptyFile_Returns400()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var empty = new FormFile(new MemoryStream(), 0, 0, "file", "empty.csv")
        {
            Headers = new HeaderDictionary(),
        };

        var result = await controller.ImportCatalog(supplier.Id, empty, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ImportCatalog_ForeignSupplier_Returns404()
    {
        var (controller, _, db) = BuildController();
        var foreign = AddSupplier(db, Guid.NewGuid()); // different org
        await db.SaveChangesAsync();

        var csv = "code,name\nA,B\n";
        var result = await controller.ImportCatalog(foreign.Id, CsvFile(csv), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await db.SupplierProducts.CountAsync()).Should().Be(0);
    }

    // ── list ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCatalog_ReturnsItems_AndFiltersByQuery()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        await new SupplierCatalogService(db).UpsertManyAsync(orgId, supplier.Id, new[]
        {
            new SupplierProduct { Code = "RES-220", Name = "Resistor 220 Ohm" },
            new SupplierProduct { Code = "CAP-100", Name = "Capacitor 100uF" },
        }, CancellationToken.None);

        // No filter → both, with total.
        var all = await controller.GetCatalog(supplier.Id, q: null, take: null, CancellationToken.None);
        var allOk = all.Should().BeOfType<OkObjectResult>().Subject;
        int total = ((dynamic)allOk.Value!).total;
        total.Should().Be(2);

        // Filter by name fragment.
        var filtered = await controller.GetCatalog(supplier.Id, q: "resistor", take: null, CancellationToken.None);
        var filteredOk = filtered.Should().BeOfType<OkObjectResult>().Subject;
        var itemsEnumerable = (System.Collections.IEnumerable)((dynamic)filteredOk.Value!).items;
        var items = itemsEnumerable.Cast<object>().ToList();
        items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetCatalog_ForeignSupplier_Returns404()
    {
        var (controller, _, db) = BuildController();
        var foreign = AddSupplier(db, Guid.NewGuid());
        await db.SaveChangesAsync();

        var result = await controller.GetCatalog(foreign.Id, q: null, take: null, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    // ── clear ────────────────────────────────────────────────────────────────

    // The happy path (ClearCatalog_DeletesAll_ForThatSupplier) moved to
    // Integration/SupplierCatalogDeletePostgresTests.ClearCatalogEndpoint_DeletesAll_ForThatSupplier:
    // SupplierCatalogService.DeleteAsync is set-based (ExecuteDelete) so a 200,000-row catalog
    // clear costs no per-row memory, and the EF InMemory provider does not implement
    // ExecuteDelete — it throws. The 404 path below never reaches the service, so it stays here.

    [Fact]
    public async Task ClearCatalog_ForeignSupplier_Returns404()
    {
        var (controller, _, db) = BuildController();
        var foreign = AddSupplier(db, Guid.NewGuid());
        await db.SaveChangesAsync();

        var result = await controller.ClearCatalog(foreign.Id, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Minimal in-memory DbContext that keeps Supplier + SupplierProduct (the catalog path
    /// touches both) and drops everything else, mirroring the Ignore pattern used by the
    /// other Suppliers controller tests.
    /// </summary>
    private sealed class CatalogTestDbContext : ProcuLinkDbContext
    {
        public CatalogTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.SupplierProfiles);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.PoMappings);
                b.Ignore(x => x.DeliveryConfigs);
                // Keep the Products navigation (its target SupplierProduct is configured below).
            });

            modelBuilder.Entity<SupplierProduct>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.OrgId, x.SupplierId, x.Code }).IsUnique();
                b.Ignore(x => x.Organisation);
                b.HasOne(x => x.Supplier).WithMany(s => s.Products).HasForeignKey(x => x.SupplierId);
            });
        }
    }
}
