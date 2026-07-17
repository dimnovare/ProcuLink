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
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
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
        Build(ProcuLinkDbContext? db = null, IOrderExceptionService? exceptions = null)
    {
        db ??= MakeDb();
        var verifier = new Mock<IHmacWebhookVerifier>();
        var ctrl     = new WebhookIngressController(
            verifier.Object,
            db,
            exceptions ?? new RecordingExceptionService(),
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

    /// <summary>Seeds one routed order in <paramref name="status"/> for <paramref name="orgId"/>.</summary>
    private static async Task SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, string status)
    {
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-GUARD-001",
            Status     = status,
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Stubs the HMAC verifier to accept, resolving to <paramref name="orgId"/>.</summary>
    private static void StubVerifier(Mock<IHmacWebhookVerifier> verifier, string slug, Guid orgId)
        => verifier
            .Setup(v => v.VerifyAsync(
                slug, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(true, null, orgId));

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

    [Theory]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.ReadyToDeliver)]
    public async Task Status_RejectedCallback_WritesRejectedBySupplier_NotDeliveryFailed(string from)
    {
        // A supplier business rejection is NOT a transport failure. Writing delivery_failed lets
        // StrandedFailedDeliveryDetectionService sweep the order after its aged threshold and
        // re-drive it (RetryDeliveryAsync retries from delivery_failed) -- re-sending a PO the
        // supplier explicitly rejected. That sweeper's own comment (:46) justifies its predicate
        // on the premise that "a supplier rejection lands in rejected_by_supplier".
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    [Fact]
    public async Task Status_RejectedCallbackForDeliveredOrder_WritesRejectedBySupplier()
    {
        // HTTP 200 is not supplier business acceptance. The prior `order.Status != "delivered"`
        // condition answered a post-delivery business rejection with 200 OK and dropped it.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivered);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    // -- Exception reconcile + rejection reason (peer-review findings) --------

    /// <summary>Records ReconcileAsync calls; the real service needs entities this test ctx ignores.</summary>
    private sealed class RecordingExceptionService : IOrderExceptionService
    {
        public List<(Guid OrgId, Guid OrderId)> Calls { get; } = new();

        public Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct)
        {
            Calls.Add((orgId, orderId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OrderException>> ListAsync(Guid orgId, string? state, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<OrderException>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> ResolveAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> IgnoreAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Throws, to prove a reconcile failure never fails the supplier callback.</summary>
    private sealed class ThrowingExceptionService : IOrderExceptionService
    {
        public Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct)
            => throw new InvalidOperationException("reconcile blew up");

        public Task<IReadOnlyList<OrderException>> ListAsync(Guid orgId, string? state, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<OrderException>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> ResolveAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> IgnoreAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("delivered")]
    public async Task Status_MutatingCallback_ReconcilesOrderExceptions(string reported)
    {
        // Mirrors OrderResolutionService.MarkRejectedAsync, the canonical rejection path, which
        // reconciles after the status write. Without this, a stale delivery_failed exception from an
        // earlier 503 keeps reading "Delivery to the supplier failed." on an order the supplier has
        // since REJECTED -- and nothing else re-reconciles it. On the delivered->rejected path it
        // inverts: a rejected order carrying ZERO open exceptions.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var exceptions = new RecordingExceptionService();
        var (ctrl, verifier, _) = Build(db, exceptions);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivering);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        await ctrl.Status("status-slug", CancellationToken.None);

        exceptions.Calls.Should().ContainSingle().Which.Should().Be((orgId, orderId));
    }

    [Theory]
    [InlineData("received")]
    [InlineData("in_progress")]
    public async Task Status_NonMutatingCallback_DoesNotReconcile(string reported)
    {
        // Telemetry mutates nothing, so there is no status change to reconcile against.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var exceptions = new RecordingExceptionService();
        var (ctrl, verifier, _) = Build(db, exceptions);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivering);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        await ctrl.Status("status-slug", CancellationToken.None);

        exceptions.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Status_WhenReconcileThrows_CallbackStillSucceedsAndStatusIsWritten()
    {
        // The supplier callback must not fail because our bookkeeping did. Mirrors
        // OrderServiceShared.SafeReconcileExceptionsAsync, which logs and swallows.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db, new ThrowingExceptionService());

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivering);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("a reconcile failure is non-fatal");
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier,
            "the status write is committed before the reconcile is attempted");
    }

    [Fact]
    public async Task Status_RejectedCallback_WritesReasonOntoLatestDeliveryAttempt()
    {
        // The reason reached ONLY an AuditEvent with EntityType="PurchaseOrder", which
        // OrdersController.Get never reads (it filters EntityType=="Order") -- so the UI fell back to
        // the latest DeliveryAttempt and showed the supplier rejecting the PO because of a GATEWAY
        // TIMEOUT, while the real reason was unreachable. MarkRejectedAsync stamps it on the latest
        // attempt (OrderResolutionService.cs:268); the webhook must do the same.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivering);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
            AttemptNumber = 1, AttemptedAt = DateTime.UtcNow.AddMinutes(-10),
            ErrorMessage = "504 Gateway Timeout",
        });
        var latest = new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusSuccess,
            AttemptNumber = 2, AttemptedAt = DateTime.UtcNow,
        };
        db.DeliveryAttempts.Add(latest);
        await db.SaveChangesAsync();

        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\",\"reason\":\"SKU discontinued\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        await ctrl.Status("status-slug", CancellationToken.None);

        var saved = await db.DeliveryAttempts.FindAsync(latest.Id);
        saved!.RejectionReason.Should().Be("SKU discontinued",
            "the LATEST attempt carries the reason the UI surfaces");
    }

    [Fact]
    public async Task Status_DeliveredCallback_DoesNotWriteARejectionReason()
    {
        // A positive ACK is not a rejection; nothing should be stamped as one.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivering);
        var attempt = new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusSuccess,
            AttemptNumber = 1, AttemptedAt = DateTime.UtcNow,
        };
        db.DeliveryAttempts.Add(attempt);
        await db.SaveChangesAsync();

        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\",\"reason\":\"ignore me\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        await ctrl.Status("status-slug", CancellationToken.None);

        var saved = await db.DeliveryAttempts.FindAsync(attempt.Id);
        saved!.RejectionReason.Should().BeNull();
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
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
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

            // DeliveryAttempt is MAPPED (not ignored): the rejection reason is written onto the
            // latest attempt, which is where OrdersController.Get surfaces it from.
            modelBuilder.Entity<DeliveryAttempt>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

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
