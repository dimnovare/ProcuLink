using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services.StarterTemplates;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Detection;
using ProcuLink.Transform.Mapping;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Endpoint contract tests for <c>POST /api/suppliers/{id}/po-mapping/apply-template</c>.
///
/// Wires the REAL <see cref="StarterTemplateService"/> (loads embedded fixtures) and the
/// REAL <see cref="PoMappingService"/> (persists to the in-memory DB) so the apply path is
/// exercised end-to-end: starter fixture → persisted supplier PO mapping → round-trip through
/// <see cref="PoMappingEngine.Apply"/>.
/// </summary>
public class SuppliersControllerApplyTemplateTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new ApplyTemplateTestDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    private static (SuppliersController Controller, Guid OrgId, ProcuLinkDbContext Db)
        BuildController()
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
            new PoMappingService(db),               // real — persists
            new Mock<IDeliveryConfigService>().Object,
            new Mock<IDeliveryService>().Object,
            new TestDoubles.FakeAnalyticsService(),
            new Mock<IFileStorageService>().Object,
            new SourceColumnExtractor(),
            new StarterTemplateService(),           // real — loads embedded fixtures
            new Mock<ISupplierCatalogService>().Object);

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

    private static PoMappingConfig UnwrapConfig(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<PoMappingConfig>().Subject;
    }

    // ── Validation / 404 paths ───────────────────────────────────────────────

    [Fact]
    public async Task UnknownSupplier_Returns404()
    {
        var (controller, _, _) = BuildController();

        var result = await controller.ApplyPoMappingTemplate(
            Guid.NewGuid(), new ApplyPoMappingTemplateRequest("erply"), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SupplierFromAnotherOrg_Returns404()
    {
        var (controller, _, db) = BuildController();
        var foreign = AddSupplier(db, Guid.NewGuid()); // different org
        await db.SaveChangesAsync();

        var result = await controller.ApplyPoMappingTemplate(
            foreign.Id, new ApplyPoMappingTemplateRequest("erply"), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UnknownTemplate_Returns404()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var result = await controller.ApplyPoMappingTemplate(
            supplier.Id, new ApplyPoMappingTemplateRequest("does-not-exist"), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task BlankTemplateId_Returns400()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var result = await controller.ApplyPoMappingTemplate(
            supplier.Id, new ApplyPoMappingTemplateRequest("  "), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Happy path: erply ────────────────────────────────────────────────────

    [Theory]
    [InlineData("erply")]
    [InlineData("ERPLY")] // case-insensitive
    public async Task ApplyErply_PersistsConfigAndReturnsIt(string templateId)
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var result = await controller.ApplyPoMappingTemplate(
            supplier.Id, new ApplyPoMappingTemplateRequest(templateId), CancellationToken.None);

        var config = UnwrapConfig(result);
        config.Header["PoNumber"].ExternalField.Should().Be("number");
        config.Lines["BuyerItemCode"].ExternalField.Should().Be("code");

        // It was actually persisted via PoMappingService.
        var persisted = await new PoMappingService(db).GetAsync(orgId, supplier.Id, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Header["BuyerName"].ExternalField.Should().Be("clientName");
        persisted.Lines["UnitPrice"].ExternalField.Should().Be("price");
    }

    [Fact]
    public async Task ApplyDirecto_PersistedConfigRoundTripsThroughMappingEngine()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        await controller.ApplyPoMappingTemplate(
            supplier.Id, new ApplyPoMappingTemplateRequest("directo"), CancellationToken.None);

        var config = await new PoMappingService(db).GetAsync(orgId, supplier.Id, CancellationToken.None);
        config.Should().NotBeNull();

        IReadOnlyDictionary<string, string> headerRow = new Dictionary<string, string>
        {
            ["number"]        = "DO-2026-042",
            ["date"]          = "2026-03-10",
            ["customer_name"] = "Baltic Trade AS",
            ["currency"]      = "EUR",
        };
        IReadOnlyDictionary<string, string> lineRow = new Dictionary<string, string>
        {
            ["row_item"]        = "DRC-001",
            ["row_description"] = "Bearing 6205",
            ["row_quantity"]    = "50",
            ["unit"]            = "PCS",
            ["row_price"]       = "8.75",
        };

        var mapped = PoMappingEngine.Apply(headerRow, [lineRow], config!);

        mapped.PoNumber.Should().Be("DO-2026-042");
        mapped.BuyerName.Should().Be("Baltic Trade AS");
        mapped.Lines.Should().ContainSingle();
        mapped.Lines[0].BuyerItemCode.Should().Be("DRC-001");
        mapped.Lines[0].UnitPrice.Should().Be("8.75");
    }

    [Fact]
    public async Task ApplyTemplate_OverwritesExistingConfig()
    {
        var (controller, orgId, db) = BuildController();
        var supplier = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        // Pre-seed an unrelated config.
        await new PoMappingService(db).UpsertAsync(orgId, supplier.Id, new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "old_po" } },
        }, CancellationToken.None);

        await controller.ApplyPoMappingTemplate(
            supplier.Id, new ApplyPoMappingTemplateRequest("erply"), CancellationToken.None);

        var config = await new PoMappingService(db).GetAsync(orgId, supplier.Id, CancellationToken.None);
        config!.Header["PoNumber"].ExternalField.Should().Be("number"); // replaced, not "old_po"
    }

    /// <summary>
    /// Minimal in-memory DbContext that keeps Supplier + SupplierPoMapping (the apply path
    /// touches both) and drops everything else, mirroring the Ignore pattern used by the
    /// other Suppliers controller tests.
    /// </summary>
    private sealed class ApplyTemplateTestDbContext : ProcuLinkDbContext
    {
        public ApplyTemplateTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
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

            modelBuilder.Entity<SupplierPoMapping>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
            });
        }
    }
}
