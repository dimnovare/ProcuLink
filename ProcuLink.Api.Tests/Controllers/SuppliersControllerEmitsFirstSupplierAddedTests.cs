using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Api.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

public class SuppliersControllerEmitsFirstSupplierAddedTests
{
    private static ProcuLinkDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SuppliersTestDbContext(opts);
    }

    private static (SuppliersController Controller, FakeAnalyticsService Analytics, Guid OrgId, ProcuLinkDbContext Db) BuildController()
    {
        var db = MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        billing
            .Setup(b => b.CheckSupplierLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LimitCheckResult(Allowed: true, PilotExpired: false, Plan: "growth", Limit: 5));

        var profileRepo  = new Mock<ISupplierProfileRepository>();
        var mapping      = new Mock<IItemMappingService>();
        var poMapping    = new Mock<IPoMappingService>();
        var deliveryCfg  = new Mock<IDeliveryConfigService>();
        var delivery     = new Mock<IDeliveryService>();
        var fileStorage  = new Mock<IFileStorageService>();
        var sourceCols   = new Mock<ProcuLink.Core.Services.Detection.ISourceColumnExtractor>();

        var analytics = new FakeAnalyticsService();

        var controller = new SuppliersController(
            profileRepo.Object,
            mapping.Object,
            db,
            tenant.Object,
            billing.Object,
            poMapping.Object,
            deliveryCfg.Object,
            delivery.Object,
            analytics,
            fileStorage.Object,
            sourceCols.Object);

        return (controller, analytics, orgId, db);
    }

    [Fact]
    public async Task CreateSupplier_FirstSupplierForOrg_EmitsFirstSupplierAdded()
    {
        // arrange: in-memory DB with no suppliers for the org
        var (controller, analytics, orgId, _) = BuildController();

        // act
        var result = await controller.CreateSupplier(new CreateSupplierRequest("Acme"), CancellationToken.None);

        // assert: 201 Created
        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.Value.Should().NotBeNull();
        var supplierId = (Guid)created.Value!.GetType().GetProperty("id")!.GetValue(created.Value)!;

        // exactly one event captured
        analytics.CapturedEvents.Should().HaveCount(1);
        var ev = analytics.CapturedEvents[0];
        ev.EventName.Should().Be("first_supplier_added");
        ev.OrgId.Should().Be(orgId);
        ev.UserId.Should().BeNull();
        ev.Properties.Should().ContainKey("supplier_id");
        ev.Properties["supplier_id"].Should().Be(supplierId);
    }

    [Fact]
    public async Task CreateSupplier_SecondSupplierForOrg_DoesNotEmitFirstSupplierAdded()
    {
        // arrange: in-memory DB with one existing supplier for the org
        var (controller, analytics, orgId, db) = BuildController();

        db.Suppliers.Add(new Supplier
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = "Existing",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // act
        var result = await controller.CreateSupplier(new CreateSupplierRequest("Beta"), CancellationToken.None);

        // assert: 201 Created
        result.Should().BeOfType<CreatedAtActionResult>();

        // zero captured events
        analytics.CapturedEvents.Should().BeEmpty();
    }

    /// <summary>
    /// Minimal in-memory DbContext. Ignores entities whose mappings rely on
    /// jsonb columns / FK relationships not needed for these controller tests,
    /// matching the pattern used by <c>ApiKeyTestDbContext</c>.
    /// </summary>
    private sealed class SuppliersTestDbContext : ProcuLinkDbContext
    {
        public SuppliersTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
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
            });
        }
    }
}
