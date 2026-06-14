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
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for the TEMPLATE-AWARE branch of POST /api/orders/{id}/mapping-override/preview
/// (branch auto/be-template-preview). The action renders a DRAFT whole-document Scriban template
/// without saving / transforming:
///   • a draft template (the founder's Ingram-Micro-style example) renders to <c>{ ok:true, output, contentType }</c>;
///   • a draft with no explicit content type defaults to <c>application/json</c>;
///   • a custom <c>OutputTemplateContentType</c> is echoed back;
///   • a bad template returns HTTP 200 with <c>{ ok:false, error }</c> (NOT 400/500) so the editor shows it inline;
///   • when NO body is supplied, the STORED override's template is rendered (draft fallback to stored);
///   • a draft body wins over the stored override;
///   • when no template is present the EXISTING field-mapping (format=...) preview is unchanged;
///   • org-scoping: a cross-tenant / unknown order id returns 404.
/// </summary>
public class OrdersTemplatePreviewTests
{
    // The exact Ingram-Micro-style example the founder gave (mirrors the Transform unit test).
    private const string IngramTemplate =
        "{\"customerOrderNumber\":\"{{OrderNr}}\"," +
        "\"lines\":[{{ for Line in Lines }}{\"customerLineNumber\":\"{{Line.LineNr}}\"," +
        "\"ingramPartNumber\":\"{{Line.DistributorPid}}\"," +
        "\"quantity\":{{Line.Qty}}," +
        "\"unitPrice\":{{Line.OrderedPrice}}}{{ if !for.last }},{{ end }}{{ end }}]}";

    // ── helpers ────────────────────────────────────────────────────────────────

    private static TemplatePreviewTestDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TemplatePreviewTestDbContext(opts);
    }

    private static (OrdersController Controller, Guid OrgId, ProcuLinkDbContext Db, Mock<IOrderMappingOverrideService> Overrides)
        BuildController(ProcuLinkDbContext? db = null, Guid? orgId = null)
    {
        db ??= MakeDb();
        var org = orgId ?? Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(org);

        var overrides = new Mock<IOrderMappingOverrideService>();

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
            Array.Empty<ITransformService>());

        return (controller, org, db, overrides);
    }

    private static Guid SeedResolvedOrder(ProcuLinkDbContext db, Guid orgId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            // Guid.Empty → the supplier-load branch is skipped (Supplier is Ignore'd in the test ctx);
            // the template tests don't need a supplier name.
            SupplierId = Guid.Empty,
            PoNumber   = "PO-9001",
            BuyerName  = "Acme Buyer Ltd",
            OrderDate  = new DateOnly(2026, 5, 1),
            Currency   = "EUR",
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });

        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
            Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
            BuyerItemCode = "B-2", SupplierItemCode = "SUP-2", Description = "Gadget",
            Quantity = 2m, Unit = "EA", UnitPrice = 5.5m, NeedsReview = false, Confidence = 1.0f,
        });

        db.SaveChanges();
        return orderId;
    }

    /// <summary>Reads a named property off the anonymous response object returned by the action.</summary>
    private static object? Prop(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value);

    // ── Test 1: draft Ingram template renders to { ok:true, output, contentType } ──

    [Fact]
    public async Task Preview_DraftIngramTemplate_RendersOkTrueWithOutput()
    {
        var (controller, orgId, db, _) = BuildController();
        var orderId = SeedResolvedOrder(db, orgId);

        var draft = new OrderMappingOverride { OutputTemplate = IngramTemplate };

        var result = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);

        var ok    = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;

        Prop(value, "ok").Should().Be(true);
        Prop(value, "contentType").Should().Be("application/json"); // default when no explicit type
        var output = Prop(value, "output").Should().BeOfType<string>().Subject;

        // The rendered output must be valid JSON with the Lines loop + for.last comma handling.
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("customerOrderNumber").GetString().Should().Be("PO-9001");
        var lines = root.GetProperty("lines");
        lines.GetArrayLength().Should().Be(2);
        lines[0].GetProperty("ingramPartNumber").GetString().Should().Be("SUP-1");
        lines[0].GetProperty("quantity").GetDecimal().Should().Be(3m);
        lines[1].GetProperty("ingramPartNumber").GetString().Should().Be("SUP-2");
    }

    // ── Test 2: explicit content type is echoed back ──────────────────────────────

    [Fact]
    public async Task Preview_DraftTemplateWithContentType_EchoesIt()
    {
        var (controller, orgId, db, _) = BuildController();
        var orderId = SeedResolvedOrder(db, orgId);

        var draft = new OrderMappingOverride
        {
            OutputTemplate            = "PO,{{ OrderNr }}",
            OutputTemplateContentType = "text/csv",
        };

        var result = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(value, "ok").Should().Be(true);
        Prop(value, "contentType").Should().Be("text/csv");
        Prop(value, "output").Should().Be("PO,PO-9001");
    }

    // ── Test 3: a bad template returns 200 with { ok:false, error } (never 500) ────

    [Fact]
    public async Task Preview_BadDraftTemplate_ReturnsOkFalseNot500()
    {
        var (controller, orgId, db, _) = BuildController();
        var orderId = SeedResolvedOrder(db, orgId);

        // Unclosed `for` loop — a compile error (mirrors the Transform unit test's invalid template).
        var draft = new OrderMappingOverride { OutputTemplate = "{{ for x in Lines }}oops" };

        var result = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);

        // HTTP 200 (OkObjectResult) — NOT a 400/500 — so the editor shows the error inline.
        var ok    = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;

        Prop(value, "ok").Should().Be(false);
        Prop(value, "error").Should().BeOfType<string>().Which.Should().NotBeNullOrWhiteSpace();
    }

    // ── Test 4: template renders even while lines still need review (no guard) ─────

    [Fact]
    public async Task Preview_DraftTemplate_RendersEvenWhenLinesNeedReview()
    {
        var (controller, orgId, db, _) = BuildController();

        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = Guid.Empty,
            PoNumber = "PO-DRAFT", OrderDate = new DateOnly(2026, 5, 1), Currency = "EUR",
            Status = "pending_review", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "B-1", SupplierItemCode = null,   // unresolved — would block a real transform
            Quantity = 1m, UnitPrice = 2m, NeedsReview = true, Confidence = 0.5f,
        });
        db.SaveChanges();

        var draft = new OrderMappingOverride { OutputTemplate = "{{ OrderNr }}" };

        var result = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(value, "ok").Should().Be(true);
        Prop(value, "output").Should().Be("PO-DRAFT");
    }

    // ── Test 5: no body → render the STORED override's template ────────────────────

    [Fact]
    public async Task Preview_NoBody_RendersStoredTemplate()
    {
        var (controller, orgId, db, overrides) = BuildController();
        var orderId = SeedResolvedOrder(db, orgId);

        overrides
            .Setup(o => o.GetAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMappingOverride { OutputTemplate = "stored:{{ OrderNr }}" });

        var result = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(value, "ok").Should().Be(true);
        Prop(value, "output").Should().Be("stored:PO-9001");
    }

    // ── Test 6: a draft body wins over the stored override ─────────────────────────

    [Fact]
    public async Task Preview_DraftBody_WinsOverStoredOverride()
    {
        var (controller, orgId, db, overrides) = BuildController();
        var orderId = SeedResolvedOrder(db, orgId);

        overrides
            .Setup(o => o.GetAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMappingOverride { OutputTemplate = "stored:{{ OrderNr }}" });

        var draft = new OrderMappingOverride { OutputTemplate = "draft:{{ OrderNr }}" };

        var result = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(value, "ok").Should().Be(true);
        Prop(value, "output").Should().Be("draft:PO-9001");
        // The stored override must NOT have been consulted when a draft is supplied.
        overrides.Verify(o => o.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test 7: no template present → existing field-mapping preview is unchanged ──

    [Fact]
    public async Task Preview_NoTemplate_FieldMappingPreviewUnchanged()
    {
        var (controller, orgId, db, _) = BuildController();
        var orderId = SeedResolvedOrder(db, orgId);

        // A field-by-field CSV override (no template) — exercises the existing path.
        var draft = new OrderMappingOverride
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

        var result = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        // The legacy shape — { format, contentType, content } — must be preserved (no ok/output keys).
        Prop(value, "format").Should().Be(OutputFormat.Csv.ToString());
        var content = Prop(value, "content").Should().BeOfType<string>().Subject;
        content.Should().Contain("PurchaseOrder");
        content.Should().Contain("PO-9001");
        content.Should().Contain("SUP-1");
        // It must NOT be in template mode.
        Prop(value, "ok").Should().BeNull();
        Prop(value, "output").Should().BeNull();
    }

    // ── Test 8: unknown format (field-mapping mode) still returns 400 ──────────────

    [Fact]
    public async Task Preview_NoTemplate_UnknownFormat_Returns400()
    {
        var (controller, orgId, db, _) = BuildController();
        var orderId = SeedResolvedOrder(db, orgId);

        var draft = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = new() { ["po"] = new OutputFieldRule { OutputPath = "PO", CanonicalField = "PoNumber" } },
            },
        };

        var result = await controller.PreviewMappingOverride(orderId, draft, "edifact", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Test 9: cross-tenant / unknown order id returns 404 (template mode) ────────

    [Fact]
    public async Task Preview_CrossTenantOrder_Returns404()
    {
        var db = MakeDb();
        var otherOrg = Guid.NewGuid();
        var orderId = SeedResolvedOrder(db, otherOrg);

        var (controller, _, _, _) = BuildController(db); // different org

        var draft = new OrderMappingOverride { OutputTemplate = "{{ OrderNr }}" };

        var result = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();

        var result2 = await controller.PreviewMappingOverride(Guid.NewGuid(), draft, "csv", CancellationToken.None);
        result2.Should().BeOfType<NotFoundResult>();
    }

    // ── Minimal in-memory DbContext (mirrors OrdersMappingPreviewTests) ───────────

    private sealed class TemplatePreviewTestDbContext : ProcuLinkDbContext
    {
        public TemplatePreviewTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
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
            modelBuilder.Ignore<SourceCapture>();

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
                // Wire the Order ↔ Lines relationship so the controller's
                // Include(o => o.Lines) materialises the seeded lines under the in-memory provider.
                b.HasOne(x => x.Order)
                 .WithMany(o => o.Lines)
                 .HasForeignKey(x => x.OrderId);
            });
        }
    }
}
