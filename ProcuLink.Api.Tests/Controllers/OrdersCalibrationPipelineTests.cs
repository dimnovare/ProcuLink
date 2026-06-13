using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// V9 — proves the GET /api/orders/{id} read path attaches the per-line confidence-calibration
/// overlay (calibratedConfidence / isCalibrated / calibrationBasis) to each AI suggestion WITHOUT
/// mutating the stored raw confidence, and that with no calibration service wired the response is
/// an honest raw passthrough. The calibration math itself is pinned in the Infrastructure tests;
/// here we pin the WIRING + the never-mutate-raw invariant (the FE contract).
/// </summary>
public class OrdersCalibrationPipelineTests
{
    [Fact]
    public async Task Get_AttachesCalibratedFields_AndDoesNotMutateStoredRawConfidence()
    {
        var orgId = Guid.NewGuid();
        var db = MakeDb();

        // Seed an over-confident [0.85,0.95) history: model said ~0.9 but accepted only half the
        // time (10 acc / 10 rej = 20 ≥ 8) → smoothed (10+1)/(20+2) = 0.5. So a raw 0.9 must
        // calibrate DOWN to ~0.5.
        await SeedDecisionsAsync(db, orgId, confidence: 0.90, accepted: 10, rejected: 10);

        // The order entity returned by the (mocked) order service carries a line with a raw 0.9
        // AI confidence — the very value we calibrate.
        var line = new PurchaseOrderLineEntity
        {
            Id                          = Guid.NewGuid(),
            OrderId                     = Guid.NewGuid(),
            LineNumber                  = 1,
            BuyerItemCode               = "B-1",
            SupplierItemCode            = null,
            Quantity                    = 1m,
            UnitPrice                   = 10m,
            Confidence                  = 0.90f,
            NeedsReview                 = true,
            AiSuggestedSupplierItemCode = "SUP-A1",
            AiSuggestionConfidence      = 0.90f,   // RAW model confidence — must survive untouched
            AiSuggestionReason          = "match",
            AiSuggestionProvenance      = "library",
        };
        var entity = MakeOrder(orgId, line);

        var controller = BuildController(db, orgId, entity, withCalibration: true);

        var result = await controller.Get(entity.Id, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrderDto>().Subject;

        var suggestion = dto.Lines.Single().AiSuggestion;
        suggestion.Should().NotBeNull();

        // Raw confidence is surfaced unchanged.
        suggestion!.Confidence.Should().BeApproximately(0.90f, 1e-4f);
        // Calibrated overlay reflects the empirical 0.5 accept rate, and is marked calibrated.
        suggestion.IsCalibrated.Should().BeTrue();
        suggestion.CalibratedConfidence.Should().BeApproximately(0.5f, 1e-4f,
            "the over-confident 0.9 bucket accepted half the time must pull the displayed value to ~0.5");
        suggestion.CalibrationBasis.Should().Be(ConfidenceCalibration.CalibratedBasis(20));

        // CRITICAL: the stored line's RAW confidence is never mutated by the display layer.
        line.AiSuggestionConfidence.Should().BeApproximately(0.90f, 1e-6f,
            "calibration is a display/derived layer — the persisted raw confidence stays sound");
    }

    [Fact]
    public async Task Get_NoCalibrationServiceWired_FallsBackToRawPassthrough()
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

        var controller = BuildController(db, orgId, entity, withCalibration: false);

        var result = await controller.Get(entity.Id, CancellationToken.None);
        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrderDto>().Subject;

        var suggestion = dto.Lines.Single().AiSuggestion!;
        suggestion.Confidence.Should().BeApproximately(0.77f, 1e-4f);
        suggestion.IsCalibrated.Should().BeFalse("no calibration service → honest raw passthrough");
        suggestion.CalibratedConfidence.Should().BeApproximately(0.77f, 1e-4f);
        suggestion.CalibrationBasis.Should().Be(ConfidenceCalibration.UncalibratedBasis);
    }

    [Fact]
    public async Task Get_ColdStartOrg_AttachesUncalibratedRawPassthrough()
    {
        var orgId = Guid.NewGuid();
        var db = MakeDb(); // no decision history at all

        var line = new PurchaseOrderLineEntity
        {
            Id                          = Guid.NewGuid(),
            OrderId                     = Guid.NewGuid(),
            LineNumber                  = 1,
            BuyerItemCode               = "B-1",
            Quantity                    = 1m,
            UnitPrice                   = 10m,
            Confidence                  = 0.88f,
            NeedsReview                 = true,
            AiSuggestedSupplierItemCode = "SUP-A1",
            AiSuggestionConfidence      = 0.88f,
            AiSuggestionReason          = "match",
            AiSuggestionProvenance      = "library",
        };
        var entity = MakeOrder(orgId, line);

        var controller = BuildController(db, orgId, entity, withCalibration: true);

        var result = await controller.Get(entity.Id, CancellationToken.None);
        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrderDto>().Subject;

        var suggestion = dto.Lines.Single().AiSuggestion!;
        suggestion.IsCalibrated.Should().BeFalse("cold-start org has no history to calibrate from");
        suggestion.CalibratedConfidence.Should().BeApproximately(0.88f, 1e-4f);
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
        ProcuLinkDbContext db, Guid orgId, PurchaseOrderEntity entity, bool withCalibration)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders = new Mock<IOrderService>();
        orders
            .Setup(s => s.GetByIdAsync(orgId, entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(entity));

        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<OrdersController>.Instance;

        IConfidenceCalibrationService? calibration = withCalibration
            ? new ConfidenceCalibrationService(db, new MemoryCache(new MemoryCacheOptions()))
            : null;

        return new OrdersController(
            orders.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            logger,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>(),
            aiDecisions: null,
            conformance: null,
            poMappings: null,
            effectiveConfig: null,
            calibration: calibration);
    }

    private static CalibrationTestDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task SeedDecisionsAsync(
        ProcuLinkDbContext db, Guid orgId, double confidence, int accepted, int rejected)
    {
        var rows = new List<AiSuggestionDecision>();
        for (var i = 0; i < accepted; i++)
            rows.Add(MakeDecision(orgId, i, confidence, AiSuggestionDecisionKind.Accepted));
        for (var i = 0; i < rejected; i++)
            rows.Add(MakeDecision(orgId, 100_000 + i, confidence, AiSuggestionDecisionKind.Rejected));
        db.AiSuggestionDecisions.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static AiSuggestionDecision MakeDecision(Guid orgId, int lineNumber, double confidence, string decision) => new()
    {
        Id                        = Guid.NewGuid(),
        OrgId                     = orgId,
        OrderId                   = Guid.NewGuid(),
        LineNumber                = lineNumber,
        SuggestedSupplierItemCode = "SUP-A",
        ChosenSupplierItemCode    = decision == AiSuggestionDecisionKind.Accepted ? "SUP-A" : "SUP-B",
        Confidence                = confidence,
        ModelVersion              = "gpt-5-mini",
        Decision                  = decision,
        DecidedBy                 = "user",
        DecidedAt                 = DateTime.UtcNow,
    };

    /// <summary>
    /// In-memory context that keeps <see cref="AiSuggestionDecision"/> (the calibration source) and
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
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();

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

            modelBuilder.Entity<AiSuggestionDecision>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
