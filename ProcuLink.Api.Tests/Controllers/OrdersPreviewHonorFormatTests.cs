using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for the EXPLORATORY <c>honorFormat</c> query flag on
/// POST /api/orders/{id}/mapping-override/preview.
///
/// <para>Background: a revision-pinned order normally has the preview SWAP the requested format to the
/// pinned revision's snapshotted output format so "preview == delivered bytes" (read-parity). The
/// founder asked the live preview to also support a "what would this order look like as X" view. The
/// new opt-in <c>honorFormat=true</c> flag SKIPS that swap and renders the requested format from the
/// canonical/effective order. Delivery is unaffected (still governed by the pinned revision); only this
/// read-only preview changes.</para>
///
/// <para>This covers:
///   • <c>honorFormat=true&amp;format=csv</c> on a JSON-pinned order renders <c>format:"Csv"</c>;
///   • the DEFAULT (no flag) on the same JSON-pinned order still swaps to <c>format:"Json"</c>;
///   • <c>honorFormat=true</c> on an UNPINNED order is a no-op (the requested format already stands).</para>
/// </summary>
public class OrdersPreviewHonorFormatTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static HonorFormatTestDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new HonorFormatTestDbContext(opts);
    }

    /// <summary>
    /// Builds the controller with REAL fixed transformers (so CSV/JSON actually render) and an
    /// optional <see cref="IEffectiveConnectionConfigResolver"/> mock. When <paramref name="pinnedFormat"/>
    /// is non-null the resolver returns a revision bundle snapshotting that output format (the
    /// pinned-revision authority case); when null the resolver returns the LIVE marker (unpinned).
    /// </summary>
    private static (OrdersController Controller, Guid OrgId, ProcuLinkDbContext Db) BuildController(
        ProcuLinkDbContext db, Guid orgId, string? pinnedFormat)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var overrides = new Mock<IOrderMappingOverrideService>();

        var resolver = new Mock<IEffectiveConnectionConfigResolver>();
        resolver
            .Setup(r => r.ResolveAsync(orgId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pinnedFormat is null
                ? EffectiveConnectionConfig.Live
                : new EffectiveConnectionConfig { RevisionId = Guid.NewGuid(), OutputFormat = pinnedFormat });

        // Real transformers covering the entity-based formats the preview can render.
        var transformers = new ITransformService[]
        {
            new CsvTransformService(),
            new JsonTransformService(),
            new XmlTransformService(),
        };

        var controller = new OrdersController(
            new Mock<IOrderService>().Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            overrides.Object,
            new Mock<IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            transformers,
            effectiveConfig: resolver.Object);

        return (controller, orgId, db);
    }

    private static Guid SeedResolvedOrder(ProcuLinkDbContext db, Guid orgId, Guid? revisionId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id                   = orderId,
            OrgId                = orgId,
            SupplierId           = Guid.Empty,
            ConnectionRevisionId = revisionId,
            PoNumber             = "PO-9001",
            BuyerName            = "Acme Buyer Ltd",
            OrderDate            = new DateOnly(2026, 5, 1),
            Currency             = "EUR",
            Status               = "ready",
            CreatedAt            = DateTime.UtcNow,
            UpdatedAt            = DateTime.UtcNow,
        });

        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
            Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
        });

        db.SaveChanges();
        return orderId;
    }

    /// <summary>A field-by-field override (no template) so the preview renders a real document.</summary>
    private static OrderMappingOverride FieldOverride() => new()
    {
        Output = new OutputMappingConfig
        {
            Header = new()
            {
                ["po"] = new OutputFieldRule { OutputPath = "PurchaseOrder", CanonicalField = "PoNumber" },
            },
            Lines = new()
            {
                ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" },
                ["qty"]  = new OutputFieldRule { OutputPath = "Qty",      CanonicalField = "Quantity" },
            },
        },
    };

    private static object? Prop(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value);

    // ── Test 1: honorFormat=true on a JSON-pinned order renders the REQUESTED CSV ──

    [Fact]
    public async Task Preview_HonorFormat_RendersRequestedFormat_NotPinned()
    {
        var orgId   = Guid.NewGuid();
        var db      = MakeDb();
        var (controller, _, _) = BuildController(db, orgId, pinnedFormat: "json");
        var orderId = SeedResolvedOrder(db, orgId, revisionId: Guid.NewGuid());

        var result = await controller.PreviewMappingOverride(
            orderId, FieldOverride(), format: "csv", honorFormat: true, CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        // The swap is SKIPPED — the requested CSV format stands, even though the revision is JSON.
        Prop(value, "format").Should().Be(OutputFormat.Csv.ToString());
        var content = Prop(value, "content").Should().BeOfType<string>().Subject;
        content.Should().Contain("PO-9001");
        content.Should().Contain("SUP-1");
    }

    // ── Test 2: DEFAULT (no flag) on the SAME JSON-pinned order still swaps to JSON ──

    [Fact]
    public async Task Preview_Default_SwapsToPinnedFormat_Unchanged()
    {
        var orgId   = Guid.NewGuid();
        var db      = MakeDb();
        var (controller, _, _) = BuildController(db, orgId, pinnedFormat: "json");
        var orderId = SeedResolvedOrder(db, orgId, revisionId: Guid.NewGuid());

        // honorFormat defaults to false — the requested CSV must be swapped to the pinned JSON.
        var result = await controller.PreviewMappingOverride(
            orderId, FieldOverride(), format: "csv", honorFormat: false, CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(value, "format").Should().Be(OutputFormat.Json.ToString());
        var content = Prop(value, "content").Should().BeOfType<string>().Subject;
        content.Should().Contain("PO-9001");
        content.Should().Contain("SUP-1");
    }

    // ── Test 3: honorFormat=true on an UNPINNED order is a no-op (CSV already stands) ──

    [Fact]
    public async Task Preview_HonorFormat_UnpinnedOrder_NoOp()
    {
        var orgId   = Guid.NewGuid();
        var db      = MakeDb();
        var (controller, _, _) = BuildController(db, orgId, pinnedFormat: null); // LIVE marker
        var orderId = SeedResolvedOrder(db, orgId, revisionId: null);

        var result = await controller.PreviewMappingOverride(
            orderId, FieldOverride(), format: "json", honorFormat: true, CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        // No pin → nothing to swap; the requested JSON renders either way.
        Prop(value, "format").Should().Be(OutputFormat.Json.ToString());
    }

    // ── Minimal in-memory DbContext (mirrors OrdersTemplatePreviewTests) ───────────

    private sealed class HonorFormatTestDbContext : ProcuLinkDbContext
    {
        public HonorFormatTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
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
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<OrderParty>();
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
                b.Property(x => x.OrderId).IsRequired();
                b.HasOne(x => x.Order)
                 .WithMany(o => o.Lines)
                 .HasForeignKey(x => x.OrderId);
            });

            modelBuilder.Entity<SourceCapture>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.TokensJson);
                b.Ignore(x => x.Order);
            });
        }
    }
}
