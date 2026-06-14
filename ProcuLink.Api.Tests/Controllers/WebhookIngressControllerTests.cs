using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Webhooks;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for POST /api/webhook-ingress/{slug}/ping, /acknowledge, and /status.
/// HMAC verification is mocked; the controller's body-read and header-read
/// paths are exercised via DefaultHttpContext.
/// </summary>
public class WebhookIngressControllerTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static WebhookIngressTestDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WebhookIngressTestDbContext(opts);
    }

    private static (WebhookIngressController Controller, Mock<IHmacWebhookVerifier> Verifier, ProcuLinkDbContext Db)
        Build(ProcuLinkDbContext? db = null)
    {
        db ??= MakeDb();
        var verifier = new Mock<IHmacWebhookVerifier>();
        var ctrl     = new WebhookIngressController(
            verifier.Object,
            db,
            NullLogger<WebhookIngressController>.Instance);
        return (ctrl, verifier, db);
    }

    /// <summary>
    /// Attaches a DefaultHttpContext with HMAC headers and a JSON body to the controller.
    /// The body stream must support EnableBuffering (MemoryStream does).
    /// </summary>
    private static void SetHttpContext(
        WebhookIngressController ctrl,
        string body        = "{}",
        string slug        = "test-slug",
        string ts          = "2026-05-31T00:00:00Z",
        string nonce       = "nonce-abc",
        string signature   = "sig-placeholder")
    {
        var httpContext = new DefaultHttpContext();

        httpContext.Request.Headers["X-ProcuLink-Timestamp"] = ts;
        httpContext.Request.Headers["X-ProcuLink-Nonce"]     = nonce;
        httpContext.Request.Headers["X-ProcuLink-Signature"] = signature;

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        httpContext.Request.Body          = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    // ── Ping ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_WhenHmacVerificationFails_Returns401()
    {
        var (ctrl, verifier, _) = Build();
        SetHttpContext(ctrl, slug: "bad-slug");

        verifier
            .Setup(v => v.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(
                Valid: false,
                ErrorMessage: "Signature verification failed",
                OrganisationId: null));

        var result = await ctrl.Ping("bad-slug", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>(
            "a failing HMAC verification must return 401");
    }

    [Fact]
    public async Task Ping_WhenHmacVerificationSucceeds_Returns200()
    {
        var (ctrl, verifier, _) = Build();
        const string slug = "my-erp-slug";
        SetHttpContext(ctrl, slug: slug);

        verifier
            .Setup(v => v.VerifyAsync(
                slug, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(
                Valid: true,
                ErrorMessage: null,
                OrganisationId: Guid.NewGuid()));

        var result = await ctrl.Ping(slug, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();
        // Response must include ok=true and the slug
        var okProp = ok.Value!.GetType().GetProperty("ok");
        okProp.Should().NotBeNull();
        okProp!.GetValue(ok.Value).Should().Be(true);
    }

    // ── Acknowledge ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Acknowledge_WhenHmacVerificationFails_Returns401()
    {
        var (ctrl, verifier, _) = Build();
        SetHttpContext(ctrl);

        verifier
            .Setup(v => v.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(false, "Signature verification failed", null));

        var result = await ctrl.Acknowledge("any-slug", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Acknowledge_WhenOrderExistsAndHmacSucceeds_Returns200AndWritesAuditEvent()
    {
        var db         = MakeDb();
        var orgId      = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        // Seed a matching order
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id        = orderId,
            OrgId     = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber  = "PO-ACK-001",
            Status    = "delivered",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency  = "EUR",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var body = $"{{\"orderId\":\"{orderId}\",\"supplierReference\":\"SUP-REF-001\"}}";
        SetHttpContext(ctrl, body: body);

        verifier
            .Setup(v => v.VerifyAsync(
                "ack-slug", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(true, null, orgId));

        var result = await ctrl.Acknowledge("ack-slug", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();

        // One AuditEvent must have been written to the DB
        var events = db.AuditEvents.ToList();
        events.Should().ContainSingle(e =>
            e.Action == "webhook_acknowledge" && e.EntityId == orderId);
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_WhenHmacVerificationFails_Returns401()
    {
        var (ctrl, verifier, _) = Build();
        SetHttpContext(ctrl);

        verifier
            .Setup(v => v.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(false, "Signature verification failed", null));

        var result = await ctrl.Status("any-slug", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Status_WhenSupplierReportsDelivered_OrderStatusUpdatedAndAuditWritten()
    {
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id        = orderId,
            OrgId     = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber  = "PO-STATUS-001",
            Status    = "ready_to_deliver",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency  = "EUR",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var body = $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}";
        SetHttpContext(ctrl, body: body);

        verifier
            .Setup(v => v.VerifyAsync(
                "status-slug", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(true, null, orgId));

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();

        // Order must now have status=delivered
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be("delivered");

        // Audit event must have been appended
        var events = db.AuditEvents.ToList();
        events.Should().ContainSingle(e =>
            e.Action == "webhook_status" && e.EntityId == orderId);
    }

    // ── Minimal in-memory DbContext ──────────────────────────────────────────

    private sealed class WebhookIngressTestDbContext : ProcuLinkDbContext
    {
        public WebhookIngressTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
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
            modelBuilder.Ignore<SchemaFingerprint>();

            // Value converters let the in-memory provider store JsonDocument as a string.
            // We use static method references to avoid expression-tree restrictions on
            // optional-argument calls (CS0854) and local-function references (CS8110).
            var jsonDocNullableConverter = new ValueConverter<JsonDocument?, string?>(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => JsonDocHelpers.ParseNullable(v));

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Property(x => x.CanonicalJson).HasConversion(jsonDocNullableConverter);
            });

            modelBuilder.Entity<AuditEvent>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Property(x => x.Payload).HasConversion(jsonDocNullableConverter);
            });
        }
    }
}

/// <summary>
/// Static helpers used by value-converter lambdas.
/// Expression trees cannot call methods with optional parameters (JsonDocument.Parse)
/// or reference local functions, so we delegate through static methods instead.
/// </summary>
internal static class JsonDocHelpers
{
    public static JsonDocument? ParseNullable(string? s) =>
        s is null ? null : JsonDocument.Parse(s);
}
