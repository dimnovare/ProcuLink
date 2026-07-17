using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// A5 (P2) — the retry path must mirror <c>DeliverOrderJob</c>'s billing gate. A backoff retry can
/// fire LONG after the first attempt, by which time the org may have lapsed to
/// read_only / past_due / cancelled. Without the gate, that retry delivered anyway and metered the
/// €0.50 overage. The gate routes a lapsed org's retry to the explicit, auto-releasing
/// <c>delivery_held</c> state instead of delivering.
/// </summary>
public class DeliveryServiceBillingGateTests
{
    private const int MaxAttempts = 3;

    [Fact]
    public async Task Retry_OrgCannotProcess_DoesNotDispatch_HoldsForBilling_NoOverage()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(ids.OrgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false); // org lapsed (read_only / past_due / cancelled)

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));
        var service = CreateService(db, dispatcher, encryption, billing.Object);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        // Did NOT deliver — so nothing is metered as a billable delivered order.
        dispatcher.Calls.Should().Be(0, "a lapsed org's retry must not deliver");
        (await db.DeliveryAttempts.CountAsync(a => a.OrderId == ids.OrderId && a.Status == DeliveryAttempt.StatusSuccess))
            .Should().Be(0, "no successful (billable) delivery attempt is recorded");

        // Routed to the explicit, auto-releasing hold — never a silent strand.
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryHeld);
        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId && e.Action == "DeliveryHeldForBilling"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Retry_OrgInGoodStanding_DeliversNormally()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(ids.OrgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));
        var service = CreateService(db, dispatcher, encryption, billing.Object);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
        (await db.PurchaseOrders.CountAsync(p => p.Id == ids.OrderId && p.Status == OrderStatusConstants.DeliveryHeld))
            .Should().Be(0);
    }

    [Fact]
    public async Task HeldRetry_IsReleasedByReactivationFlow_NotStranded()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(ids.OrgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));
        var service = CreateService(db, dispatcher, encryption, billing.Object);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryHeld);

        // Org returns to good standing → the existing reactivation flow releases the hold.
        var released = await service.ReleaseBillingHeldOrdersAsync(ids.OrgId, default);

        released.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.ReadyToDeliver, "the held order is re-driven, never stranded");
    }

    // ── The park + a lapsed org (I1) ─────────────────────────────────────────
    // Only an operator "Send again" can bring a parked order here: RetryDeliveryAsync refuses
    // 'delivery_unconfirmed' before its own billing gate, so the automatic queue can never turn a
    // park into a hold. The human already accepted the duplicate risk the park exists to defer —
    // the hold just pauses that authorised send until the org can pay for it.

    [Fact]
    public async Task HoldForBilling_ParkedOrder_HoldsExplicitly_NotLeftParked()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryUnconfirmed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(ids.OrgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false); // org lapsed (read_only / past_due / trial_expired)

        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 202)), encryption, billing.Object);

        // Exactly what DeliverOrderJob's billing gate does when the operator clicks "Send again".
        var held = await service.HoldForBillingAsync(ids.OrgId, ids.OrderId, default);

        held.Should().BeTrue("a parked order is holdable — the operator's Send again must not be swallowed");
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryHeld, "the order moves to the explicit, auto-releasing hold");
        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId && e.Action == "DeliveryHeldForBilling"))
            .Should().Be(1, "the hold is on the record — the operator's click left a trace");
    }

    [Fact]
    public async Task HeldParkedOrder_IsReleasedByReactivationFlow_NotStrandedInThePark()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryUnconfirmed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(ids.OrgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 202)), encryption, billing.Object);

        await service.HoldForBillingAsync(ids.OrgId, ids.OrderId, default);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryHeld);

        // Org returns to good standing → the reactivation flow rescues it. An order left parked
        // would be invisible to this sweep (it only scans delivery_held) and never re-driven.
        var released = await service.ReleaseBillingHeldOrdersAsync(ids.OrgId, default);

        released.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.ReadyToDeliver, "the operator's Send again resumes once billing settles");
    }

    // ── The invariant that makes releasing a held park to ready_to_deliver SAFE ──────────────
    // ReleaseBillingHeldOrdersAsync restores every delivery_held order to ready_to_deliver and
    // re-drives it, without recording where it came from. That is correct ONLY while a park can
    // reach delivery_held by an operator's decision alone: releasing then COMPLETES the "Send
    // again" they already chose. It would become a real bug the moment the AUTOMATIC queue could
    // hold a park — the release would send with no human, which is the one thing the park exists
    // to prevent.
    //
    // That invariant rests entirely on RetryDeliveryAsync refusing a parked order BEFORE its
    // billing gate. Moving the gate above the non-retryable check — a plausible tidy-up — silently
    // converts the automatic queue into a park-holder and turns the unconditional release into a
    // duplicate PO. Pin it here rather than in a comment nobody runs.
    [Fact]
    public async Task Retry_OnParkedOrder_WithLapsedOrg_RefusesBeforeTheBillingGate_AndNeverHolds()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryUnconfirmed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(ids.OrgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false); // lapsed — the gate WOULD hold if the park check did not run first

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));
        var service = CreateService(db, dispatcher, encryption, billing.Object);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        dispatcher.Calls.Should().Be(0);

        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryUnconfirmed,
                "the automatic queue must leave a park parked — only a human moves it");
        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId && e.Action == "DeliveryHeldForBilling"))
            .Should().Be(0,
                "a park held WITHOUT a human decision would be released straight back into a send that nobody chose");

        billing.Verify(b => b.CanProcessOrdersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "the park check must short-circuit ahead of the billing gate — that ordering is the whole guarantee");
    }

    // Regression guard: the hold must not widen to statuses that are mid-dispatch or finished.
    [Theory]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter)]
    public async Task HoldForBilling_NonHoldableStatus_IsBenignNoOp(string status)
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, status);
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingService>();
        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 202)), encryption, billing.Object);

        var held = await service.HoldForBillingAsync(ids.OrgId, ids.OrderId, default);

        held.Should().BeFalse();
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status.Should().Be(status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db, string status)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-BILL-1",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId,
            OrderId = orderId,
            OrgId = orgId,
            Format = "csv",
            FileKey = "artifact.csv",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId, artifactId);
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService encryption, IBillingService billing) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            billing: billing);

    private static SupplierDeliveryConfig MakeConfig(Guid orgId, Guid supplierId, DeliveryEncryptionService encryption) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private sealed class CountingDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public int Calls { get; private set; }
        public string Protocol => "http";

        public CountingDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
