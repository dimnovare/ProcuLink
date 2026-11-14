using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// GET /api/orders/{id} on the read-only archive returns each AI suggestion as an honest raw
/// passthrough: calibratedConfidence equals the stored raw confidence, isCalibrated is false and the
/// basis is <see cref="ConfidenceCalibration.UncalibratedBasis"/>. The calibration overlay retired
/// with the order API (plan 13b); the FE contract fields stay, so this pins what they now carry.
/// </summary>
public class OrdersCalibrationPipelineTests
{
    [Fact]
    public async Task Get_AiSuggestion_IsAnHonestRawPassthrough()
    {
        var orgId = Guid.NewGuid();
        var db = MakeDb();

        var line = new PurchaseOrderLineEntity
        {
            Id                          = Guid.NewGuid(),
            OrderId                     = Guid.NewGuid(),
            LineNumber                  = 1,
            BuyerItemCode               = "B-1",
            Quantity                    = 1m,
            UnitPrice                   = 10m,
            Confidence                  = 0.77f,
            NeedsReview                 = true,
            AiSuggestedSupplierItemCode = "SUP-A1",
            AiSuggestionConfidence      = 0.77f,
            AiSuggestionReason          = "match",
            AiSuggestionProvenance      = "library",
        };
        var entity = MakeOrder(orgId, line);

        var controller = BuildController(db, orgId, entity);

        var result = await controller.Get(entity.Id, CancellationToken.None);
        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrderDto>().Subject;

        var suggestion = dto.Lines.Single().AiSuggestion!;
        suggestion.Confidence.Should().BeApproximately(0.77f, 1e-4f);
        suggestion.IsCalibrated.Should().BeFalse("the archive never calibrates → honest raw passthrough");
        suggestion.CalibratedConfidence.Should().BeApproximately(0.77f, 1e-4f);
        suggestion.CalibrationBasis.Should().Be(ConfidenceCalibration.UncalibratedBasis);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PurchaseOrderEntity MakeOrder(Guid orgId, PurchaseOrderLineEntity line)
    {
        var orderId = Guid.NewGuid();
        line.OrderId = orderId;
        return new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-CAL",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = "pending_review",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines      = new List<PurchaseOrderLineEntity> { line },
            OutboundArtifacts = new List<OutboundArtifact>(),
        };
    }

    private static OrdersController BuildController(
        ProcuLinkDbContext db, Guid orgId, PurchaseOrderEntity entity)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders = new Mock<IOrderQueryService>();
        orders
            .Setup(s => s.GetByIdAsync(orgId, entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(entity));

        return new OrdersController(orders.Object, tenant.Object, db);
    }

    private static CalibrationTestDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// In-memory context that keeps
    /// the order/line entities, while ignoring jsonb / complex-FK entities the provider can't map.
    /// </summary>
    private sealed class CalibrationTestDbContext : ProcuLinkDbContext
    {
        public CalibrationTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<Supplier>();
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
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<WorkerHealthAlertCooldown>();

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.CanonicalJson);
            });

            modelBuilder.Entity<PurchaseOrderLineEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Property(x => x.OrderId).IsRequired();
            });
        }
    }
}
