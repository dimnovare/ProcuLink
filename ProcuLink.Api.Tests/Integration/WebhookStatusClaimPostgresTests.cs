using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Webhooks;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves the supplier status callback's ATOMIC CLAIM on REAL Postgres, where
/// <c>ExecuteUpdateAsync</c> actually runs. The EF InMemory provider cannot translate it, so
/// WebhookIngressControllerTests only ever exercises the read-check-write fallback branch — the
/// relational claim (the guard that ships to production) would otherwise be entirely untested.
///
/// <para>What only this test can prove:</para>
/// <list type="bullet">
///   <item>the from-status set + the correlated <c>EXISTS</c> over delivery_attempts (including the
///     <c>IdempotencyKey</c>/<c>ArtifactSha256</c> marker test) TRANSLATE into one
///     <c>UPDATE … WHERE</c> — an untranslatable predicate throws at runtime, not compile-time;</item>
///   <item>a <c>ready_to_deliver</c> order with NO attempt row is refused BY THE CLAIM (0 rows);</item>
///   <item>an order whose only row came from a PRE-DISPATCH gate — a row exists, nothing was sent —
///     is refused BY THE CLAIM;</item>
///   <item>the tracked entity is re-synced after the tracker-bypassing update, so the 200 body
///     reports the NEW status rather than the stale one;</item>
///   <item>the status write and its audit row commit together in ONE transaction — proven by
///     fault-injecting the audit write and requiring the auto-committed
///     <c>ExecuteUpdateAsync</c> to roll back with it, NOT by counting audit rows on the happy
///     path (which passes with no transaction at all).</item>
/// </list>
///
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class WebhookStatusClaimPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_whclaim_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    // ── seeding ──────────────────────────────────────────────────────────────

    private async Task<(Guid OrgId, Guid OrderId)> SeedOrderAsync(
        string status, DateTime? deliveryDueAt = null, bool slaBreached = false)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_wh_{orgId:N}", Name = "Webhook Org",
            Slug = $"wh-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Webhook Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-WH-CLAIM-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
            Currency = "EUR", Status = status, CreatedAt = now, UpdatedAt = now,
            DeliveryDueAt = deliveryDueAt, SlaBreached = slaBreached,
        });
        await db.SaveChangesAsync();

        return (orgId, orderId);
    }

    /// <summary>
    /// A DISPATCHED attempt row — the evidence the claim requires. The evidence is not the row's
    /// existence (four pre-dispatch gates write rows having sent nothing) but the markers only the
    /// dispatch sequence writes: <c>IdempotencyKey</c>, committed before the wire send, and
    /// <c>ArtifactSha256</c>, the hash of the bytes actually dispatched.
    /// </summary>
    private async Task SeedDeliveryAttemptAsync(Guid orgId, Guid orderId)
    {
        await using var db = NewContext();
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId,
            Channel = "http", Destination = "https://supplier.example/orders",
            Status = DeliveryAttempt.StatusSuccess, AttemptNumber = 1, AttemptedAt = DateTime.UtcNow,
            IdempotencyKey = $"{orderId:N}:{Guid.NewGuid():N}",
            ArtifactSha256 = "b5bb9d8014a0f9b1d61e21e796d78dccdf1352f23cd32812f4850b878ae4944c",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The exact row ParkUnconfirmedAsync leaves behind: terminal 'unconfirmed', carrying the
    /// pre-send <c>IdempotencyKey</c> and NO <c>ArtifactSha256</c> (the sha lands only after
    /// <c>DispatchAsync</c> returns — the park exists because it never did). Key-only is the
    /// marker shape the claim's correlated EXISTS must accept.
    /// </summary>
    private async Task<Guid> SeedParkedAttemptAsync(Guid orgId, Guid orderId)
    {
        await using var db = NewContext();
        var attempt = new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId,
            Channel = "erp_erply", Destination = "https://erp.example/api",
            Status = DeliveryAttempt.StatusUnconfirmed, AttemptNumber = 1, AttemptedAt = DateTime.UtcNow,
            ErrorMessage = "Delivery unconfirmed.",
            IdempotencyKey = $"{orderId:N}:{Guid.NewGuid():N}", ArtifactSha256 = null,
        };
        db.DeliveryAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt.Id;
    }

    /// <summary>
    /// A PRE-DISPATCH failure row: it exists, but nothing was ever sent, so it carries neither
    /// marker. The shape that made a bare row-existence check unsound.
    /// </summary>
    private async Task SeedPreDispatchAttemptAsync(Guid orgId, Guid orderId)
    {
        await using var db = NewContext();
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId,
            Channel = "missing_config", Destination = "supplier delivery config",
            Status = DeliveryAttempt.StatusFailed, AttemptNumber = 1, AttemptedAt = DateTime.UtcNow,
            ErrorMessage = "Supplier delivery config is missing.",
            IdempotencyKey = null, ArtifactSha256 = null,
        });
        await db.SaveChangesAsync();
    }

    private static WebhookIngressController BuildController(ProcuLinkDbContext db, Guid orgId, string body)
    {
        var verifier = new Mock<IHmacWebhookVerifier>();
        verifier
            .Setup(v => v.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(true, null, orgId));

        // The real OrderExceptionService: this suite runs on a fully migrated Postgres schema, so
        // reconcile can do its actual work rather than being stubbed out.
        var ctrl = new WebhookIngressController(
            verifier.Object, db, new OrderExceptionService(db),
            NullLogger<WebhookIngressController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-ProcuLink-Timestamp"] = "2026-07-16T00:00:00Z";
        httpContext.Request.Headers["X-ProcuLink-Nonce"]     = Guid.NewGuid().ToString();
        httpContext.Request.Headers["X-ProcuLink-Signature"] = "sig";
        var bytes = Encoding.UTF8.GetBytes(body);
        httpContext.Request.Body          = new MemoryStream(bytes);
        httpContext.Request.ContentLength = bytes.Length;
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return ctrl;
    }

    private static string? OkStatus(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return (string?)ok.Value!.GetType().GetProperty("status")!.GetValue(ok.Value);
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task Status_ReadyToDeliverWithNoDeliveryAttempt_IsRefusedByTheClaim()
    {
        // C1. ready_to_deliver is where every transformed order RESTS before it is ever sent, so the
        // status alone cannot authorise a 'delivered' write. With no attempt row the atomic claim's
        // EXISTS predicate matches 0 rows and the callback is refused -- leaving the order in the
        // state StrandedReadyOrderDetectionService still sweeps.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.ReadyToDeliver);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();

        await using var verify = NewContext();
        var order = await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.ReadyToDeliver,
            "the claim must not write a status onto an order that was never dispatched");
        (await verify.AuditEvents.CountAsync(e => e.EntityId == orderId && e.Action == "webhook_status"))
            .Should().Be(0);
        (await verify.AuditEvents.CountAsync(e => e.EntityId == orderId && e.Action == "webhook_status_rejected"))
            .Should().Be(1, "a 409 nobody can see is a silent ignore with extra steps");
    }

    [DockerRequiredFact]
    public async Task Status_DeliveryHeldByThePreSendBillingGate_IsRefusedByTheClaim()
    {
        // The pre-claim billing gate moves a STILL-IDLE ready_to_deliver order to delivery_held
        // (DeliveryService.cs:822-825, "never a delivery") with no attempt row. Marking it delivered
        // would strand it: ReleaseBillingHeldOrdersAsync matches Status == delivery_held.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.DeliveryHeld);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();

        await using var verify = NewContext();
        var order = await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryHeld,
            "the order must stay releasable by ReleaseBillingHeldOrdersAsync");
    }

    [DockerRequiredFact]
    public async Task Status_DispatchedOrder_IsClaimed_AndTheResponseReportsTheNewStatus()
    {
        // The happy path THROUGH the relational claim: the from-status set and the correlated EXISTS
        // must translate into one UPDATE, and the tracked entity must be re-synced afterwards --
        // ExecuteUpdateAsync bypasses the change tracker, so an un-synced `order` would make the 200
        // body report the OLD status.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.Delivering);
        await SeedDeliveryAttemptAsync(orgId, orderId);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        OkStatus(result).Should().Be(OrderStatusConstants.Delivered,
            "the 200 body must report the status the claim just wrote, not the stale tracked value");

        await using var verify = NewContext();
        var order = await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Delivered);

        // Exactly one audit row on the happy path. (This does NOT prove the transaction spans the
        // claim and the audit -- it is equally true with no transaction; that is what
        // Status_WhenTheAuditWriteFails_TheClaimedStatusIsRolledBackToo proves.)
        (await verify.AuditEvents.CountAsync(e => e.EntityId == orderId && e.Action == "webhook_status"))
            .Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Status_ParkedOrder_IsClaimed_AndTheSlaWindowCloses()
    {
        // The park (delivery_unconfirmed) waits on exactly one question — did the PO arrive? — and
        // the supplier's own callback answers it. This proves, on the real claim:
        //   * delivery_unconfirmed is admitted by the from-status set;
        //   * a KEY-ONLY marker row (the park's shape: IdempotencyKey set, ArtifactSha256 null)
        //     satisfies the correlated EXISTS;
        //   * the added SLA SetPropertys translate — the park deliberately leaves a live
        //     DeliveryDueAt (the nag runs until a human acts), and the callback IS the act, so the
        //     claim must close the window in the same atomic UPDATE.
        var (orgId, orderId) = await SeedOrderAsync(
            OrderStatusConstants.DeliveryUnconfirmed,
            deliveryDueAt: DateTime.UtcNow.AddHours(2), slaBreached: false);
        var attemptId = await SeedParkedAttemptAsync(orgId, orderId);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        OkStatus(result).Should().Be(OrderStatusConstants.Delivered);

        await using var verify = NewContext();
        var order = await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Delivered);
        order.DeliveryDueAt.Should().BeNull("a resolved park must stop nagging");
        order.SlaBreached.Should().BeFalse();

        // The ORDER moves; the attempt row keeps recording what the channel observed — nothing.
        var row = await verify.DeliveryAttempts.SingleAsync(a => a.Id == attemptId);
        row.Status.Should().Be(DeliveryAttempt.StatusUnconfirmed,
            "the webhook never rewrites an attempt row to success");
    }

    [DockerRequiredFact]
    public async Task Status_ReadyToDeliverWithAnAttemptRow_IsClaimed()
    {
        // MV-1: a DISPATCHED order reset by a mapping edit and re-transformed rests in
        // ready_to_deliver WITH attempt rows. A late ACK for the original dispatch must still land --
        // this is why the fix adds the attempt check rather than dropping ready_to_deliver from the set.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.ReadyToDeliver);
        await SeedDeliveryAttemptAsync(orgId, orderId);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        OkStatus(result).Should().Be(OrderStatusConstants.Delivered);

        await using var verify = NewContext();
        (await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    [DockerRequiredFact]
    public async Task Status_DeliveryHeldAfterAFailedAttempt_IsClaimed()
    {
        // A5: delivery_failed -> delivery_held is a hold AFTER an attempt. It has attempt rows, so
        // its late ACK is still accepted -- refusing it would make the reactivation re-drive send the
        // PO a SECOND time.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.DeliveryHeld);
        await SeedDeliveryAttemptAsync(orgId, orderId);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        OkStatus(result).Should().Be(OrderStatusConstants.Delivered);
    }

    [DockerRequiredFact]
    public async Task Status_OrderWhoseOnlyAttemptFailedBeforeDispatch_IsRefusedByTheClaim()
    {
        // C2 -- a row is not a send. The missing-config gate writes an order-linked terminal row
        // having dispatched nothing, so a bare EXISTS would wave through an order no supplier has
        // ever seen. The claim's predicate demands a dispatch MARKER on the row; this row has
        // neither, so it matches 0 rows.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.DeliveryHeld);
        await SeedPreDispatchAttemptAsync(orgId, orderId);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();

        await using var verify = NewContext();
        (await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryHeld,
                "the order must stay releasable by ReleaseBillingHeldOrdersAsync -- it was never sent");
    }

    [DockerRequiredFact]
    public async Task Status_OrderWithBothAPreDispatchRowAndARealSend_IsClaimed()
    {
        // The evidence is per-ROW: a failed-before-dispatch first Send plus a later REAL send means
        // the order WAS dispatched. An over-strict guard (every row must carry a marker) would
        // refuse a genuine delivery -- proving the EXISTS is quantified correctly.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.Delivering);
        await SeedPreDispatchAttemptAsync(orgId, orderId);
        await SeedDeliveryAttemptAsync(orgId, orderId);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        OkStatus(await ctrl.Status("wh-slug", CancellationToken.None))
            .Should().Be(OrderStatusConstants.Delivered);
    }

    [DockerRequiredFact]
    public async Task Status_WhenTheAuditWriteFails_TheClaimedStatusIsRolledBackToo()
    {
        // Trap 1, PROVEN rather than asserted: ExecuteUpdateAsync AUTO-COMMITS. Without the explicit
        // transaction spanning the claim and the audit SaveChanges, the status write would already be
        // durable when the audit failed -- an order flipped to 'delivered' with nothing recording why.
        //
        // Fault-inject a failure into the audit SaveChanges and require the status write to vanish
        // with it. This is the assertion the earlier "commit in ONE transaction" doc line could not
        // make: counting audit rows on the happy path is equally true with no transaction at all
        // (delete the BeginTransaction/Commit and every other test here stays green).
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.Delivering);
        await SeedDeliveryAttemptAsync(orgId, orderId);

        await using var db = new AuditFailingDbContext(_options!);
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var act = async () => await ctrl.Status("wh-slug", CancellationToken.None);
        await act.Should().ThrowAsync<DbUpdateException>("the injected audit failure must not be swallowed");

        await using var verify = NewContext();
        (await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivering,
                "the claim and the audit share one transaction, so a failed audit rolls the status back");
        (await verify.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    /// <summary>
    /// Fails the ACCEPTED-callback audit write, and ONLY that one.
    ///
    /// <para>Throwing on every SaveChanges would make the test vacuous: had the claim REFUSED the
    /// callback, the refusal path's own SaveChanges would throw the same exception and leave the
    /// status equally unchanged — passing for the opposite of the reason under test. Faulting only
    /// the <c>webhook_status</c> audit means the test can pass ONLY if the claim actually matched
    /// and wrote; a refusal saves its <c>webhook_status_rejected</c> row normally, no exception is
    /// thrown, and the test fails on its ThrowAsync.</para>
    /// </summary>
    private sealed class AuditFailingDbContext : ProcuLinkDbContext
    {
        public AuditFailingDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            var isAcceptedCallbackAudit = ChangeTracker.Entries<AuditEvent>()
                .Any(e => e.State == EntityState.Added && e.Entity.Action == "webhook_status");

            return isAcceptedCallbackAudit
                ? throw new DbUpdateException("injected: the accepted-callback audit write failed")
                : base.SaveChangesAsync(ct);
        }
    }

    [DockerRequiredFact]
    public async Task Status_AttemptRowForAnotherOrg_DoesNotSatisfyTheClaim()
    {
        // Tenant isolation inside the claim predicate: another org's attempt row is not this order's
        // dispatch evidence.
        var (orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.ReadyToDeliver);
        var (otherOrgId, _)  = await SeedOrderAsync(OrderStatusConstants.ReadyToDeliver);
        await SeedDeliveryAttemptAsync(otherOrgId, orderId);

        await using var db = NewContext();
        var ctrl = BuildController(db, orgId, $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");

        var result = await ctrl.Status("wh-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();

        await using var verify = NewContext();
        (await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.ReadyToDeliver);
    }
}
