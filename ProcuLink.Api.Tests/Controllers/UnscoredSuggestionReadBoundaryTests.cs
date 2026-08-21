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
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// The GET /api/orders/{id} read boundary must carry "nothing scored this" all the way to the UI.
///
/// <para><b>The defect this pins.</b> <c>MapAiSuggestion</c> read the stored confidence as
/// <c>line.AiSuggestionConfidence ?? 0f</c> into a NON-NULLABLE
/// <c>AiMappingSuggestionDto.Confidence</c>. So the moment the deterministic producers stopped
/// fabricating 0.95, every catalog and source-document suggestion would have arrived at the mapper
/// as a hard <b>0%</b> — painted on the same AI-violet confidence ramp, at the bottom of it. That is
/// the identical "an unknown renders as a number" failure, just at the other end of the scale, and
/// it would have made the fix look like a regression to a reviewer.</para>
///
/// <para>An absent measurement is not a measurement of zero. Both <c>Confidence</c> and
/// <c>CalibratedConfidence</c> are nullable and stay null, and <c>Basis</c> names the evidence so
/// the UI can say what the suggestion IS instead of scoring it.</para>
/// </summary>
public class UnscoredSuggestionReadBoundaryTests
{
    [Fact]
    public async Task CatalogSuggestion_ReachesTheUiWithNoConfidence_NotZero()
    {
        var suggestion = await ReadSuggestionAsync(
            storedConfidence: null,
            provenance: "catalog: manufacturer part number");

        suggestion.Confidence.Should().BeNull(
            "an exact catalog lookup was never scored — it must not arrive as 0%, which the mapper "
            + "would paint on the AI confidence ramp exactly like a real model score of zero");
        suggestion.CalibratedConfidence.Should().BeNull(
            "calibrating an absent score would manufacture one");
        suggestion.IsCalibrated.Should().BeFalse();
        suggestion.Basis.Should().Be(AiMappingSuggestionBasis.Catalog);
        suggestion.Basis.Should().Be("catalog", "the frontend switches its label on this literal");
    }

    [Fact]
    public async Task SourceDocumentSuggestion_ReachesTheUiWithNoConfidence_AndSaysWhatItIs()
    {
        var suggestion = await ReadSuggestionAsync(
            storedConfidence: null,
            provenance: "source document: manufacturer part number");

        suggestion.Confidence.Should().BeNull();
        suggestion.Basis.Should().Be(AiMappingSuggestionBasis.SourceDocument);
        suggestion.Basis.Should().Be("source_document");
    }

    [Fact]
    public async Task ScoredSuggestion_KeepsItsNumber_AndIsLabelledModel()
    {
        // The control. A genuine model score must survive this boundary untouched, and must be the
        // ONLY thing that gets labelled "model".
        var suggestion = await ReadSuggestionAsync(
            storedConfidence: 0.82f,
            provenance: "OpenAI structured output");

        suggestion.Confidence.Should().BeApproximately(0.82f, 1e-4f);
        suggestion.CalibratedConfidence.Should().BeApproximately(0.82f, 1e-4f,
            "no calibration service is wired here → honest raw passthrough");
        suggestion.Basis.Should().Be(AiMappingSuggestionBasis.Model);
        suggestion.Basis.Should().Be("model");
    }

    [Fact]
    public async Task AStoredConfidenceIsTheOnlyThingThatEarnsTheModelLabel()
    {
        // Provenance text is model-authored on the AI path, so it can say anything at all. The basis
        // decision therefore rests on whether a number was recorded — a fact about the row — and NOT
        // on parsing prose. Here a suggestion carrying catalog-shaped provenance still reads as
        // "model" because a scorer really did produce a number for it.
        var suggestion = await ReadSuggestionAsync(
            storedConfidence: 0.71f,
            provenance: "catalog: manufacturer part number");

        suggestion.Basis.Should().Be(AiMappingSuggestionBasis.Model,
            "a recorded score can only have come from a scorer");
        suggestion.Confidence.Should().BeApproximately(0.71f, 1e-4f);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static async Task<AiMappingSuggestionDto> ReadSuggestionAsync(
        float? storedConfidence, string provenance)
    {
        var orgId = Guid.NewGuid();
        var db = MakeDb();

        var line = new PurchaseOrderLineEntity
        {
            Id                          = Guid.NewGuid(),
            OrderId                     = Guid.NewGuid(),
            LineNumber                  = 1,
            BuyerItemCode               = "29954596",
            SupplierItemCode            = null,
            Quantity                    = 1m,
            UnitPrice                   = 10m,
            Confidence                  = null,
            NeedsReview                 = true,
            AiSuggestedSupplierItemCode = "FAB-SCAN-77120",
            AiSuggestionConfidence      = storedConfidence,
            AiSuggestionReason          = "reason",
            AiSuggestionProvenance      = provenance,
        };
        var entity = MakeOrder(orgId, line);

        var controller = BuildController(db, orgId, entity);
        var result = await controller.Get(entity.Id, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<OrderDto>().Subject;

        var suggestion = dto.Lines.Single().AiSuggestion;
        suggestion.Should().NotBeNull("the suggestion itself is never withheld — only its number");
        return suggestion!;
    }

    private static PurchaseOrderEntity MakeOrder(Guid orgId, PurchaseOrderLineEntity line)
    {
        var orderId = Guid.NewGuid();
        line.OrderId = orderId;
        return new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-UNSCORED",
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

        var orders = new Mock<IOrderService>();
        orders
            .Setup(s => s.GetByIdAsync(orgId, entity.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(entity));

        return new OrdersController(
            orders.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrdersController>.Instance,
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
            calibration: null);
    }

    private static ReadBoundaryTestDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// In-memory context keeping the order/line entities while ignoring jsonb / complex-FK entities
    /// the provider cannot map. Mirrors the shape used by <c>OrdersCalibrationPipelineTests</c>.
    /// </summary>
    private sealed class ReadBoundaryTestDbContext : ProcuLinkDbContext
    {
        public ReadBoundaryTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

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

            modelBuilder.Entity<AiSuggestionDecision>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
