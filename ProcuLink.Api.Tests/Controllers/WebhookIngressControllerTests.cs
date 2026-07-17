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

    /// <summary>
    /// Seeds ONE DISPATCHED delivery attempt — the dispatch evidence the status guard requires.
    ///
    /// <para>The evidence is NOT the row's existence: four pre-dispatch gates write an order-linked
    /// terminal row with nothing sent (missing config, no dispatcher, undecryptable credentials,
    /// artifact-download failure), so a bare row proves only that delivery was ATTEMPTED. The
    /// evidence is a marker only the dispatch sequence writes — <c>IdempotencyKey</c> (stamped by
    /// <c>OpenDispatchAttemptAsync</c> on the row it commits before the wire send) or
    /// <c>ArtifactSha256</c> (the hash of the bytes actually dispatched). This helper stamps both,
    /// as a real dispatched row carries both. Use <see cref="SeedPreDispatchAttemptAsync"/> for the
    /// never-sent shape.</para>
    ///
    /// <para>That every pre-dispatch gate really does leave BOTH null is pinned in
    /// <c>DeliveryProvenanceTests.PreDispatchFailuresWriteNoDispatchMarker</c> — this helper's
    /// premise is a test, not a comment.</para>
    /// </summary>
    private static async Task SeedDeliveryAttemptAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId,
        string status = DeliveryAttempt.StatusSuccess)
    {
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id             = Guid.NewGuid(),
            OrgId          = orgId,
            OrderId        = orderId,
            Channel        = "http",
            Destination    = "https://supplier.example/orders",
            Status         = status,
            AttemptNumber  = 1,
            AttemptedAt    = DateTime.UtcNow,
            IdempotencyKey = $"{orderId:N}:{Guid.NewGuid():N}",
            ArtifactSha256 = "b5bb9d8014a0f9b1d61e21e796d78dccdf1352f23cd32812f4850b878ae4944c",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds an attempt row from a PRE-DISPATCH failure: a row exists, but nothing was ever sent, so
    /// it carries neither marker. This is the shape that made the row-existence guard unsound — an
    /// order can hold one of these and have reached a supplier zero times.
    /// </summary>
    private static async Task SeedPreDispatchAttemptAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId)
    {
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id             = Guid.NewGuid(),
            OrgId          = orgId,
            OrderId        = orderId,
            Channel        = "missing_config",
            Destination    = "supplier delivery config",
            Status         = DeliveryAttempt.StatusFailed,
            AttemptNumber  = 1,
            AttemptedAt    = DateTime.UtcNow,
            ErrorMessage   = "Supplier delivery config is missing.",
            IdempotencyKey = null,
            ArtifactSha256 = null,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Reads the <c>error</c> string off a 409 body.</summary>
    private static string ConflictError(IActionResult result)
    {
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        return (string)conflict.Value!.GetType().GetProperty("error")!.GetValue(conflict.Value)!;
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
        // MV-1: this order WAS dispatched — a mapping edit reset it to ready, the next Send
        // re-transformed it back to ready_to_deliver, and a late ACK for the ORIGINAL dispatch
        // lands here. The MARKER on that original dispatch's attempt row is what makes it reportable.
        await SeedDeliveryAttemptAsync(db, orgId, orderId);

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
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
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
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    [Theory]
    [InlineData(OrderStatusConstants.PendingParse,  "delivered")]
    [InlineData(OrderStatusConstants.Parsing,       "delivered")]
    [InlineData(OrderStatusConstants.Unrouted,      "delivered")]
    [InlineData(OrderStatusConstants.PendingReview, "delivered")]
    [InlineData(OrderStatusConstants.Ready,         "delivered")]
    [InlineData(OrderStatusConstants.Transforming,  "delivered")]
    [InlineData(OrderStatusConstants.PendingParse,  "rejected")]
    [InlineData(OrderStatusConstants.Ready,         "rejected")]
    public async Task Status_TerminalCallbackForNeverDispatchedOrder_Returns409_AndDoesNotMutate(
        string from, string reported)
    {
        // The order was never sent to a supplier, so a supplier cannot be reporting on it. Marking
        // it delivered would be a silent lost order: shipped in the UI, never actually sent.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(from, "a rejected callback must not mutate the order");
    }

    [Fact]
    public async Task Status_DeliveredCallbackForRejectedBySupplierOrder_Returns409_AndDoesNotMutate()
    {
        // rejected_by_supplier is terminal for webhooks: a supplier that rejected must not silently
        // flip the order to delivered -- a human has likely already acted on the rejection. A
        // genuine retraction is an operator re-drive, not an automatic write.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.RejectedBySupplier);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    [Fact]
    public async Task Status_DuplicateRejectedCallback_IsIdempotent200_NotConflict()
    {
        // Callback endpoints get retried. A supplier re-posting a rejection it already delivered
        // must not get a 409 for work that succeeded -- this short-circuit is what lets
        // rejected_by_supplier stay OUT of WebhookReportableFrom.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.RejectedBySupplier);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
        db.AuditEvents.Should().ContainSingle(e => e.Action == "webhook_status");
    }

    [Theory]
    [InlineData(OrderStatusConstants.ReadyToDeliver)]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter)]
    [InlineData(OrderStatusConstants.DeliveryHeld)]
    public async Task Status_DeliveredCallbackForDispatchedOrder_Returns200_AndMarksDelivered(string from)
    {
        // Every dispatched state accepts a late positive ACK. delivery_held is included because
        // delivery_failed -> delivery_held is real (A5): refusing a held order's ACK would make the
        // reactivation re-drive send it a SECOND time. "Dispatched" is proven by a dispatch MARKER on
        // an attempt row, not by the status alone (ready_to_deliver and delivery_held are BOTH also
        // reachable without any dispatch) and not by a bare row (the pre-dispatch gates write those
        // having sent nothing) -- which is what the sibling 409 tests below pin.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.Delivered);
    }

    [Theory]
    [InlineData(OrderStatusConstants.ReadyToDeliver, "delivered")]
    [InlineData(OrderStatusConstants.ReadyToDeliver, "rejected")]
    [InlineData(OrderStatusConstants.DeliveryHeld,   "delivered")]
    [InlineData(OrderStatusConstants.DeliveryHeld,   "rejected")]
    public async Task Status_TerminalCallbackForReportableStatusWithNoDeliveryAttempt_Returns409_AndDoesNotMutate(
        string from, string reported)
    {
        // C1 -- the status alone does NOT prove dispatch. BOTH of these states are reachable
        // PRE-dispatch:
        //   * ready_to_deliver is where EVERY transformed order rests before it is ever sent
        //     (AutoDeliver defaults false -> it waits for a human "Send"; StrandedReadyOrder-
        //     DetectionService exists solely because orders sit there un-sent).
        //   * delivery_held via the PRE-CLAIM billing gate (DeliveryService.cs:822-825), which moves
        //     the STILL-IDLE order there -- "never a delivery".
        // Marking either 'delivered' would also disable its own safety net: the stranded-ready sweep
        // matches Status == ready_to_deliver and the billing release matches Status == delivery_held,
        // so both predicates would stop matching -> permanently lost, displayed as shipped, billable.
        // The discriminator is dispatch EVIDENCE: an attempt row carrying a dispatch marker.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        // Deliberately NO delivery attempt: nothing was ever sent to a supplier.
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>(
            "no delivery attempt exists, so no supplier can be reporting an outcome for this order");
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(from, "the refused callback must leave the order (and its safety net) untouched");
        db.AuditEvents.Should().ContainSingle(e => e.Action == "webhook_status_rejected");
        db.AuditEvents.Should().NotContain(e => e.Action == "webhook_status");
    }

    [Theory]
    [InlineData(OrderStatusConstants.ReadyToDeliver, "delivered")]
    [InlineData(OrderStatusConstants.ReadyToDeliver, "rejected")]
    [InlineData(OrderStatusConstants.DeliveryHeld,   "delivered")]
    [InlineData(OrderStatusConstants.DeliveryFailed, "delivered")]
    public async Task Status_TerminalCallbackForOrderWhoseOnlyAttemptFailedBeforeDispatch_Returns409(
        string from, string reported)
    {
        // C2 -- an attempt ROW is not a SEND. Four pre-dispatch gates write an order-linked terminal
        // row having sent zero bytes (missing config, no dispatcher registered, undecryptable
        // credentials, artifact-download failure), so "a row exists" would wave through an order no
        // supplier has ever seen. Reachable end-to-end: ready_to_deliver -> Send -> missing config ->
        // delivery_failed + a row -> the org lapses -> the A5 gate holds it (delivery_held) -> a
        // callback marks it 'delivered' -> ReleaseBillingHeldOrders stops matching -> the PO is lost,
        // shown as shipped, and billed. The marker pair is what closes this; the null-ness of both
        // markers on every pre-dispatch path is pinned in
        // DeliveryProvenanceTests.PreDispatchFailuresWriteNoDispatchMarker.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        await SeedPreDispatchAttemptAsync(db, orgId, orderId);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>(
            "the order holds an attempt row, but nothing was ever dispatched to a supplier");
        ConflictError(result).Should().Contain("has not been sent to a supplier yet",
            "the refusal must say what is actually true: attempted, never sent");
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(from, "the refused callback must leave the order (and its safety net) untouched");
        db.AuditEvents.Should().ContainSingle(e => e.Action == "webhook_status_rejected");
        db.AuditEvents.Should().NotContain(e => e.Action == "webhook_status");
    }

    [Fact]
    public async Task Status_TerminalCallbackForOrderWithBothAPreDispatchRowAndARealSend_Returns200()
    {
        // The evidence is per-ROW, not per-order: a first Send that failed before dispatch (missing
        // config) leaves a marker-less row; fixing the config and re-sending adds a REAL dispatched
        // row. The order was genuinely sent, so its callback must land -- an over-strict guard that
        // demanded EVERY row carry a marker would refuse a real delivery.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivering);
        await SeedPreDispatchAttemptAsync(db, orgId, orderId);
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.Delivered);
    }

    [Fact]
    public async Task Status_DeliveredCallbackForAnotherOrgsDeliveryAttempt_Returns409()
    {
        // The attempt-row evidence is org-scoped like every other query here: another tenant's
        // attempt row must never satisfy this order's dispatch check.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.ReadyToDeliver);
        await SeedDeliveryAttemptAsync(db, Guid.NewGuid(), orderId);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.ReadyToDeliver);
    }

    // ── Exception reconcile + rejection reason (peer-review findings, PR #28) ─────
    //
    // These arrived on main while this branch was parked. The guard re-homes both into the ACCEPT
    // path of ApplyReportedStatusAsync -- a refused callback writes no status, so there is nothing to
    // reconcile and no reason to stamp. Their seeds now carry dispatch markers, because an order the
    // supplier is reporting on WAS dispatched; without a marker the guard would (correctly) refuse
    // and these would be testing the refusal path by accident.

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
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
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
    public async Task Status_RefusedCallback_DoesNotReconcile()
    {
        // A refusal writes no status, so reconciling would be reconciling against nothing -- and the
        // guard's whole point is that this order's state is untouched.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var exceptions = new RecordingExceptionService();
        var (ctrl, verifier, _) = Build(db, exceptions);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.ReadyToDeliver);
        // No dispatch evidence -> the claim refuses.
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        exceptions.Calls.Should().BeEmpty("a refused callback changed no status");
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
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
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
        // Both rows carry markers: a 504 means the payload WAS sent and the gateway timed out.
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
            AttemptNumber = 1, AttemptedAt = DateTime.UtcNow.AddMinutes(-10),
            ErrorMessage = "504 Gateway Timeout",
            IdempotencyKey = $"{orderId:N}:1",
        });
        var latest = new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusSuccess,
            AttemptNumber = 2, AttemptedAt = DateTime.UtcNow,
            IdempotencyKey = $"{orderId:N}:2",
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
            IdempotencyKey = $"{orderId:N}:1",
        };
        db.DeliveryAttempts.Add(attempt);
        await db.SaveChangesAsync();

        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\",\"reason\":\"ignore me\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        await ctrl.Status("status-slug", CancellationToken.None);

        var saved = await db.DeliveryAttempts.FindAsync(attempt.Id);
        saved!.RejectionReason.Should().BeNull();
    }

    // ── 409 copy: the sentence must be TRUE on every reachable refusal shape ──────

    [Fact]
    public async Task Status_RefusedForNeverDispatchedOrder_SaysItWasNeverSent()
    {
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.ReadyToDeliver);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var error = ConflictError(await ctrl.Status("status-slug", CancellationToken.None));

        error.Should().Contain("has not been sent to a supplier yet");
        error.Should().Contain(OrderStatusConstants.ReadyToDeliver);
    }

    [Fact]
    public async Task Status_RefusedForRejectedOrder_DoesNotClaimItWasNeverSent()
    {
        // The order WAS sent and the supplier rejected it, so "this order has not been sent to a
        // supplier yet" is false on both clauses, and "check that the orderId matches an order you
        // received" is a dead end -- it DOES match one.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.RejectedBySupplier);
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var error = ConflictError(await ctrl.Status("status-slug", CancellationToken.None));

        error.Should().NotContain("has not been sent to a supplier yet");
        error.Should().Contain("already recorded as rejected");
    }

    [Fact]
    public async Task Status_RefusedForDispatchedOrderNotAwaitingOutcome_DoesNotClaimItWasNeverSent()
    {
        // MV-1: a mapping edit on a DELIVERED order resets it to 'ready' for re-transform. It has
        // attempt rows (it was sent), but 'ready' is not a state awaiting a delivery outcome, so
        // neither the "never sent" sentence nor the "check your orderId" fix applies.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Ready);
        await SeedDeliveryAttemptAsync(db, orgId, orderId);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var error = ConflictError(await ctrl.Status("status-slug", CancellationToken.None));

        error.Should().NotContain("has not been sent to a supplier yet");
        error.Should().Contain(OrderStatusConstants.Ready);
    }

    [Fact]
    public async Task Status_RejectedCallback_WritesWebhookStatusRejectedAudit_WithActualStatus()
    {
        // A 409 nobody can see is a silent ignore with extra steps. The audit is what makes the
        // supplier's integration error actionable, so it carries the order's real status.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.PendingParse);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        await ctrl.Status("status-slug", CancellationToken.None);

        var audit = db.AuditEvents.Should()
            .ContainSingle(e => e.Action == "webhook_status_rejected" && e.EntityId == orderId)
            .Subject;
        audit.OrgId.Should().Be(orgId);
        var json = audit.Payload!.RootElement;
        json.GetProperty("ReportedStatus").GetString().Should().Be("delivered");
        json.GetProperty("OrderStatusAtReceipt").GetString().Should().Be(OrderStatusConstants.PendingParse);
        db.AuditEvents.Should().NotContain(e => e.Action == "webhook_status");
    }

    [Theory]
    [InlineData("received")]
    [InlineData("in_progress")]
    public async Task Status_NonMutatingCallback_FromAnyState_Returns200_AndDoesNotMutate(string reported)
    {
        // received/in_progress are pure telemetry -- they mutate nothing, so guarding them would
        // add noise without preventing harm. They stay 200 from any state.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.PendingParse);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.PendingParse);
        db.AuditEvents.Should().ContainSingle(e => e.Action == "webhook_status");
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

            // DeliveryAttempt is MAPPED here (not ignored): it carries the dispatch markers the
            // status guard requires, so the guard cannot be exercised without it.
            modelBuilder.Entity<DeliveryAttempt>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
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
