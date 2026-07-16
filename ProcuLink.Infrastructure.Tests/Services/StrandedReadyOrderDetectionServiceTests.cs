using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// B1 (lost-order) safety sweep — recovers orders stranded in <c>ready_to_deliver</c> whose delivery
/// was never enqueued (crash between the transform commit and the delivery enqueue). Nothing else
/// covers <c>ready_to_deliver</c>. The sweep recovers ONLY orders configured to auto-deliver (a manual
/// order legitimately rests here awaiting a manual send) that have NO delivery attempt yet, re-driving
/// them through the normal first-delivery job. Idempotent — bumps <c>UpdatedAt</c> out of the aged
/// window; <c>DeliverOrderJob</c>'s own claim prevents any double-send. Uses the full
/// ProcuLinkDbContext on InMemory (the atomic claim it relies on is covered by the Postgres suite).
/// </summary>
public class StrandedReadyOrderDetectionServiceTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task RunAsync_AgedReadyToDeliver_AutoDeliver_NoAttempt_EnqueuesDelivery()
    {
        await using var db = CreateDb();
        var (orgId, orderId, artifactId) = await SeedStrandedAsync(db, updatedMinutesAgo: 45, autoDeliver: true);
        var enqueuer = new RecordingDispatchEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        // Delivery was re-enqueued with the exact order + artifact.
        enqueuer.Calls.Should().ContainSingle()
            .Which.Should().Be((orderId, orgId, artifactId));

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Still ready_to_deliver (DeliverOrderJob owns the next transition) but bumped out of the aged window.
        order.Status.Should().Be(OrderStatusConstants.ReadyToDeliver);
        order.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-30));

        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StrandedReadyDeliveryRecovered");
        audit.EntityType.Should().Be("Order");
    }

    [Fact]
    public async Task RunAsync_ManualOrder_AutoDeliverFalse_IsUntouched()
    {
        // A manual (AutoDeliver=false) order legitimately rests in ready_to_deliver awaiting a manual
        // send — the sweep must NEVER force it out, and must not even bump its UpdatedAt.
        await using var db = CreateDb();
        var (_, orderId, _) = await SeedStrandedAsync(db, updatedMinutesAgo: 120, autoDeliver: false);
        var enqueuer = new RecordingDispatchEnqueuer();
        var before = (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).UpdatedAt;

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.UpdatedAt.Should().Be(before); // untouched
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ReadyToDeliver_WithPriorDeliveryAttempt_IsUntouched()
    {
        // A prior delivery attempt means delivery WAS dispatched — never re-drive (double-send guard).
        await using var db = CreateDb();
        var (orgId, orderId, _) = await SeedStrandedAsync(db, updatedMinutesAgo: 45, autoDeliver: true);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "d", Status = "failed", AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var enqueuer = new RecordingDispatchEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_FreshReadyToDeliver_IsUntouched()
    {
        // Within the aged window: the normal fast path (transform enqueues delivery immediately) may
        // still be in flight — do not race it.
        await using var db = CreateDb();
        var (_, orderId, _) = await SeedStrandedAsync(db, updatedMinutesAgo: 2, autoDeliver: true);
        var enqueuer = new RecordingDispatchEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_NoDeliveryConfig_IsUntouched()
    {
        // No delivery config at all → not known to auto-deliver → skip (never guess a manual order into a send).
        await using var db = CreateDb();
        var (_, orderId, _) = await SeedStrandedAsync(db, updatedMinutesAgo: 45, autoDeliver: true, withConfig: false);
        var enqueuer = new RecordingDispatchEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter)]
    [InlineData(OrderStatusConstants.Transforming)]
    [InlineData(OrderStatusConstants.DeliveryHeld)]
    public async Task RunAsync_OtherStatuses_Untouched(string status)
    {
        // Only ready_to_deliver is this sweep's concern — every other status is owned elsewhere
        // (or terminal), so touching it would create double-ownership across sweeps.
        await using var db = CreateDb();
        var (_, orderId, _) = await SeedStrandedAsync(db, updatedMinutesAgo: 120, autoDeliver: true, status: status);

        var acted = await CreateService(db, new RecordingDispatchEnqueuer()).RunAsync(Threshold, default);

        acted.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(status);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_SecondRunDoesNothing()
    {
        await using var db = CreateDb();
        var (_, orderId, _) = await SeedStrandedAsync(db, updatedMinutesAgo: 45, autoDeliver: true);
        var service = CreateService(db, new RecordingDispatchEnqueuer());

        (await service.RunAsync(Threshold, default)).Should().Be(1);
        // UpdatedAt was bumped to now, so the order left the aged window — a second run doesn't re-act.
        (await service.RunAsync(Threshold, default)).Should().Be(0);

        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NoEnqueuerRegistered_StillBumpedAndCounted()
    {
        // A process without the dispatch enqueuer must still bump the order out of the aged window and
        // count it (never silently pinning it where the next sweep re-audits it every run).
        await using var db = CreateDb();
        var (_, orderId, _) = await SeedStrandedAsync(db, updatedMinutesAgo: 45, autoDeliver: true);

        var acted = await new StrandedReadyOrderDetectionService(
            db, NullLogger<StrandedReadyOrderDetectionService>.Instance, enqueuer: null)
            .RunAsync(Threshold, default);

        acted.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).UpdatedAt
            .Should().BeAfter(DateTime.UtcNow.AddMinutes(-30));
        (await db.AuditEvents.SingleAsync(e => e.EntityId == orderId)).Action
            .Should().Be("StrandedReadyDeliveryRecovered");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StrandedReadyOrderDetectionService CreateService(
        ProcuLinkDbContext db, IDeliveryDispatchEnqueuer? enqueuer = null) =>
        new(db, NullLogger<StrandedReadyOrderDetectionService>.Instance, enqueuer);

    private static async Task<(Guid OrgId, Guid OrderId, Guid ArtifactId)> SeedStrandedAsync(
        ProcuLinkDbContext db,
        int updatedMinutesAgo,
        bool autoDeliver,
        bool withConfig = true,
        string status = OrderStatusConstants.ReadyToDeliver)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var updated = DateTime.UtcNow.AddMinutes(-updatedMinutesAgo);

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-STRAND",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR",
            Status = status,
            CreatedAt = updated,
            UpdatedAt = updated,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "artifact.csv", CreatedAt = updated,
        });
        if (withConfig)
            db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                Protocol = "http", AutoDeliver = autoDeliver,
                ConfigJson = "{}", EncryptedCredentials = "",
                CreatedAt = updated, UpdatedAt = updated,
            });
        await db.SaveChangesAsync();
        return (orgId, orderId, artifactId);
    }

    private sealed class RecordingDispatchEnqueuer : IDeliveryDispatchEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId, Guid ArtifactId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, Guid artifactId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId, artifactId));
            return Task.CompletedTask;
        }
    }
}
