using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Hangfire;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for GET /api/orders/{id}/mapping-preview.
/// Uses an in-memory EF context; no mocked OrderService required because the
/// endpoint queries the DbContext directly.
/// </summary>
public class OrdersMappingPreviewTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static MappingPreviewTestDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MappingPreviewTestDbContext(opts);
    }

    private static (OrdersController Controller, Guid OrgId, ProcuLinkDbContext Db) BuildController(
        ProcuLinkDbContext? db = null)
    {
        db ??= MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders      = new Mock<IOrderService>();
        var billing     = new Mock<IBillingService>();
        var idempotency = new Mock<IIdempotencyService>();
        var jobs        = new Mock<IBackgroundJobClient>();
        var logger      = Microsoft.Extensions.Logging.Abstractions
                              .NullLogger<OrdersController>.Instance;

        var controller = new OrdersController(
            orders.Object,
            tenant.Object,
            jobs.Object,
            db,
            logger,
            billing.Object,
            idempotency.Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        return (controller, orgId, db);
    }

    // ── Test 1: returns preview with AI suggestions and correct field values ───

    [Fact]
    public async Task GetMappingPreview_OrderWithAiSuggestions_ReturnsPreviewDto()
    {
        var (controller, orgId, db) = BuildController();

        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-001",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = "pending_review",
            SourceFileKey = "uploads/test-order.csv",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });

        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id          = Guid.NewGuid(),
            OrderId     = orderId,
            LineNumber  = 1,
            BuyerItemCode   = "BUYER-SKU-001",
            SupplierItemCode = null,              // not yet resolved
            Description = "Widget A",
            Quantity    = 10m,
            Unit        = "EA",
            UnitPrice   = 5.50m,
            Confidence  = 0.85f,
            NeedsReview = true,
            AiSuggestedSupplierItemCode = "SUPP-A1",
            AiSuggestionConfidence      = 0.92f,
            AiSuggestionProvenance      = "mapping_library",
            AiSuggestionReason          = "Exact buyer code match in library.",
        });

        await db.SaveChangesAsync();

        // act
        var result = await controller.GetMappingPreview(orderId, CancellationToken.None);

        // assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<MappingPreviewDto>().Subject;

        dto.OrderId.Should().Be(orderId.ToString());
        dto.OrderStatus.Should().Be("pending_review");
        dto.SourceFormat.Should().Be("csv");
        dto.DetectedConfidence.Should().BeApproximately(0.85, 0.001);
        dto.Lines.Should().HaveCount(1);

        var line = dto.Lines[0];
        line.LineNumber.Should().Be(1);
        line.CanonicalField.Should().Be("supplierItemCode");
        line.BuyerItemCode.Should().Be("BUYER-SKU-001");
        line.ResolvedSupplierCode.Should().BeNull();
        line.AiSuggestedSupplierCode.Should().Be("SUPP-A1");
        line.Confidence.Should().BeApproximately(0.92, 0.001);
        line.Provenance.Should().Be("mapping_library");
        line.Reason.Should().Be("Exact buyer code match in library.");
        line.Status.Should().Be("suggested");

        line.SourceFields.Should().ContainKey("buyerItemCode")
            .WhoseValue.Should().Be("BUYER-SKU-001");
        line.SourceFields.Should().ContainKey("description")
            .WhoseValue.Should().Be("Widget A");
        line.SourceFields.Should().ContainKey("quantity")
            .WhoseValue.Should().Be("10");
        line.SourceFields.Should().ContainKey("unit")
            .WhoseValue.Should().Be("EA");
        line.SourceFields.Should().ContainKey("unitPrice")
            .WhoseValue.Should().Be("5.50");
    }

    // ── Test 2: resolved / suggested / unresolved status logic ────────────────

    [Fact]
    public async Task GetMappingPreview_MixedLines_CorrectStatusPerLine()
    {
        var (controller, orgId, db) = BuildController();

        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-002",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = "pending_review",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });

        // Line 1: already resolved (supplierItemCode set)
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id               = Guid.NewGuid(),
            OrderId          = orderId,
            LineNumber       = 1,
            BuyerItemCode    = "B-001",
            SupplierItemCode = "S-001",
            Quantity         = 1m,
            UnitPrice        = 10m,
            Confidence       = 1.0f,
            NeedsReview      = false,
        });

        // Line 2: AI suggestion exists but not yet accepted
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id                          = Guid.NewGuid(),
            OrderId                     = orderId,
            LineNumber                  = 2,
            BuyerItemCode               = "B-002",
            SupplierItemCode            = null,
            Quantity                    = 2m,
            UnitPrice                   = 20m,
            Confidence                  = 0.75f,
            NeedsReview                 = true,
            AiSuggestedSupplierItemCode = "S-002-AI",
            AiSuggestionConfidence      = 0.80f,
            AiSuggestionProvenance      = "openai",
            AiSuggestionReason          = "Fuzzy match.",
        });

        // Line 3: no suggestion at all
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id               = Guid.NewGuid(),
            OrderId          = orderId,
            LineNumber       = 3,
            BuyerItemCode    = "B-003",
            SupplierItemCode = null,
            Quantity         = 3m,
            UnitPrice        = 30m,
            Confidence       = 0.50f,
            NeedsReview      = true,
        });

        await db.SaveChangesAsync();

        var result = await controller.GetMappingPreview(orderId, CancellationToken.None);

        var ok  = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<MappingPreviewDto>().Subject;

        dto.Lines.Should().HaveCount(3);

        dto.Lines[0].Status.Should().Be("resolved");
        dto.Lines[0].ResolvedSupplierCode.Should().Be("S-001");
        dto.Lines[0].AiSuggestedSupplierCode.Should().BeNull();
        dto.Lines[0].Confidence.Should().BeNull();

        dto.Lines[1].Status.Should().Be("suggested");
        dto.Lines[1].AiSuggestedSupplierCode.Should().Be("S-002-AI");
        dto.Lines[1].Confidence.Should().BeApproximately(0.80, 0.001);

        dto.Lines[2].Status.Should().Be("unresolved");
        dto.Lines[2].AiSuggestedSupplierCode.Should().BeNull();
        dto.Lines[2].Confidence.Should().BeNull();

        // overall detected confidence = average of per-line Confidence (1.0 + 0.75 + 0.50) / 3
        dto.DetectedConfidence.Should().BeApproximately(0.75, 0.001);
    }

    // ── Test 3: 404 for unknown order / cross-tenant isolation ────────────────

    [Fact]
    public async Task GetMappingPreview_UnknownOrCrossTenantOrder_Returns404()
    {
        var db = MakeDb();

        // Seed an order belonging to a DIFFERENT org
        var otherOrgId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = otherOrgId,   // different tenant
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-OTHER",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = "pending_review",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Build controller for a DIFFERENT org
        var (controller, _, _) = BuildController(db);

        // act — controller's org doesn't own this order
        var result = await controller.GetMappingPreview(orderId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();

        // Also verify a completely unknown id returns 404
        var result2 = await controller.GetMappingPreview(Guid.NewGuid(), CancellationToken.None);
        result2.Should().BeOfType<NotFoundResult>();
    }

    // ── Minimal in-memory DbContext ───────────────────────────────────────────

    /// <summary>
    /// Strips out entities whose column mappings aren't compatible with the
    /// in-memory provider (jsonb columns, complex FK chains).
    /// Follows the same pattern as SuppliersTestDbContext.
    /// </summary>
    private sealed class MappingPreviewTestDbContext : ProcuLinkDbContext
    {
        public MappingPreviewTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

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
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<OrderParty>();
            // SourceCapture is MAPPED here (not Ignored): the mapping-override preview now
            // Includes it to re-derive SourceMap rules from the persisted token universe.
            modelBuilder.Ignore<CanonicalFieldDef>();

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                // CanonicalJson is a jsonb column — not supported by in-memory; ignore it
                b.Ignore(x => x.CanonicalJson);
            });

            modelBuilder.Entity<PurchaseOrderLineEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Property(x => x.OrderId).IsRequired();
            });

            // SourceCapture is a valid Include target for the preview seam, but its TokensJson is a
            // jsonb/JsonDocument column the in-memory provider can't store — ignore it like CanonicalJson.
            modelBuilder.Entity<SourceCapture>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.TokensJson);
                b.Ignore(x => x.Order);
            });
        }
    }
}
