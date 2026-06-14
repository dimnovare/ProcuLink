using System.Text;
using FluentAssertions;
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
using ProcuLink.Transform.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Endpoint contract tests for <c>GET /api/suppliers/{id}/mapping/source-columns</c>.
/// Wires the real <see cref="SourceColumnExtractor"/> to a stub file store so the
/// download → extract path is exercised end-to-end.
/// </summary>
public class SuppliersControllerSourceColumnsTests
{
    private static ProcuLinkDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SourceColumnsTestDbContext(opts);
    }

    private static (SuppliersController Controller, Guid OrgId, ProcuLinkDbContext Db)
        BuildController(IDictionary<string, byte[]> files)
    {
        var db = MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(f => f.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                files.TryGetValue(key, out var bytes)
                    ? new MemoryStream(bytes)
                    : throw new FileNotFoundException(key));

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
            fileStorage.Object,
            new SourceColumnExtractor(),  // real extractor
            new ProcuLink.Api.Services.StarterTemplates.StarterTemplateService(),
            new Mock<ISupplierCatalogService>().Object,
            new Mock<ProcuLink.Core.Services.ISupplierConnectionService>().Object);

        return (controller, orgId, db);
    }

    private static Supplier AddSupplier(ProcuLinkDbContext db, Guid orgId)
    {
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = "Acme",
            CreatedAt = DateTime.UtcNow,
        };
        db.Suppliers.Add(supplier);
        return supplier;
    }

    private static PurchaseOrderEntity AddOrder(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string? sourceFileKey, DateTime createdAt)
    {
        var order = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-1",
            OrderDate = DateOnly.FromDateTime(createdAt),
            Currency = "EUR",
            Status = "ready",
            SourceFileKey = sourceFileKey,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.PurchaseOrders.Add(order);
        return order;
    }

    private static SourceColumnsResponse Unwrap(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<SourceColumnsResponse>().Subject;
    }

    // ── 404 only for unknown / foreign supplier ─────────────────────────────

    [Fact]
    public async Task UnknownSupplier_Returns404()
    {
        var (controller, _, _) = BuildController(new Dictionary<string, byte[]>());

        var result = await controller.GetMappingSourceColumns(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SupplierFromAnotherOrg_Returns404()
    {
        var (controller, orgId, db) = BuildController(new Dictionary<string, byte[]>());
        // Supplier belongs to a DIFFERENT org → must not be visible.
        var foreign = AddSupplier(db, Guid.NewGuid());
        await db.SaveChangesAsync();

        var result = await controller.GetMappingSourceColumns(foreign.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ── Empty case → 200 + hint (NOT 404) ────────────────────────────────────

    [Fact]
    public async Task SupplierWithNoOrders_Returns200WithHintAndEmptyColumns()
    {
        var (controller, orgId, db) = BuildController(new Dictionary<string, byte[]>());
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var result = await controller.GetMappingSourceColumns(supplier.Id, CancellationToken.None);

        var body = Unwrap(result);
        body.Columns.Should().BeEmpty();
        body.SourceOrderId.Should().BeNull();
        body.Hint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SupplierWithOnlyApiExtractedOrders_NoSourceFile_Returns200WithHint()
    {
        var (controller, orgId, db) = BuildController(new Dictionary<string, byte[]>());
        var supplier = AddSupplier(db, orgId);
        // Order with a null SourceFileKey (e.g. created from an API/email-body payload).
        AddOrder(db, orgId, supplier.Id, sourceFileKey: null, createdAt: DateTime.UtcNow);
        await db.SaveChangesAsync();

        var result = await controller.GetMappingSourceColumns(supplier.Id, CancellationToken.None);

        var body = Unwrap(result);
        body.SourceOrderId.Should().BeNull();
        body.Columns.Should().BeEmpty();
        body.Hint.Should().NotBeNullOrWhiteSpace();
    }

    // ── Happy path: most-recent order's file is inspected ─────────────────────

    [Fact]
    public async Task SupplierWithCsvOrder_ReturnsDetectedHeaderColumns()
    {
        const string key = "org/order/sample.csv";
        var files = new Dictionary<string, byte[]>
        {
            [key] = Encoding.UTF8.GetBytes("PoNumber,BuyerItemCode,Quantity,UnitPrice\r\nPO-1,A1,3,9.99\r\n"),
        };

        var (controller, orgId, db) = BuildController(files);
        var supplier = AddSupplier(db, orgId);
        var order = AddOrder(db, orgId, supplier.Id, key, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var result = await controller.GetMappingSourceColumns(supplier.Id, CancellationToken.None);

        var body = Unwrap(result);
        body.Format.Should().Be("csv");
        body.Columns.Should().Equal("PoNumber", "BuyerItemCode", "Quantity", "UnitPrice");
        body.SourceOrderId.Should().Be(order.Id);
        body.Hint.Should().BeNull();
    }

    [Fact]
    public async Task PicksMostRecentOrderWithSourceFile()
    {
        const string oldKey = "org/old/old.csv";
        const string newKey = "org/new/new.csv";
        var files = new Dictionary<string, byte[]>
        {
            [oldKey] = Encoding.UTF8.GetBytes("old_a,old_b\r\n1,2\r\n"),
            [newKey] = Encoding.UTF8.GetBytes("new_x,new_y,new_z\r\n1,2,3\r\n"),
        };

        var (controller, orgId, db) = BuildController(files);
        var supplier = AddSupplier(db, orgId);
        AddOrder(db, orgId, supplier.Id, oldKey, DateTime.UtcNow.AddDays(-2));
        var newest = AddOrder(db, orgId, supplier.Id, newKey, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var result = await controller.GetMappingSourceColumns(supplier.Id, CancellationToken.None);

        var body = Unwrap(result);
        body.SourceOrderId.Should().Be(newest.Id);
        body.Columns.Should().Equal("new_x", "new_y", "new_z");
    }

    /// <summary>
    /// Minimal in-memory DbContext mirroring the Ignore pattern used by the other
    /// Suppliers controller tests — keeps Supplier + PurchaseOrder, drops the rest.
    /// </summary>
    private sealed class SourceColumnsTestDbContext : ProcuLinkDbContext
    {
        public SourceColumnsTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
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
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
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
            });

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.CanonicalJson);
            });
        }
    }
}
