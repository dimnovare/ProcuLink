using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// B5 (silent-lost-retry) safety sweep — recovers orders stranded in <c>delivery_failed</c> whose
/// automatic next-retry was lost (a crash / lost enqueue between the failed-attempt write and the
/// backoff schedule). Nothing else covers <c>delivery_failed</c>. The sweep re-drives ONLY aged
/// orders with attempts remaining and no in-flight attempt, through the normal RetryDeliveryJob.
/// Idempotent — bumps <c>UpdatedAt</c> out of the aged window; RetryDeliveryAsync's atomic claim +
/// attempt-cap prevent any double-send. Uses the full ProcuLinkDbContext on InMemory.
/// </summary>
public class StrandedFailedDeliveryDetectionServiceTests
{
    // Aged threshold well past the max retry backoff (BackoffMinutes {30,60,120}) so a legitimately
    // scheduled retry is never raced — mirrors the recurring job's 3-hour setting.
    private static readonly TimeSpan Threshold = TimeSpan.FromHours(3);
    private const int MaxAttempts = 3; // DeliveryReliabilityOptions default

    [Fact]
    public async Task RunAsync_AgedDeliveryFailed_AttemptsRemaining_NoInFlight_ReEnqueuesRetry()
    {
        await using var db = CreateDb();
        var (orgId, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: 1);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        // The retry job was re-enqueued through the seam with THIS order's own ids.
        enqueuer.Calls.Should().ContainSingle().Which.Should().Be((orderId, orgId));

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Still delivery_failed (RetryDeliveryAsync owns the next transition) but bumped out of the aged window.
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);
        order.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));

        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StrandedFailedDeliveryRecovered");
        audit.EntityType.Should().Be("Order");
    }

    [Fact]
    public async Task RunAsync_RecentDeliveryFailed_WithinAgedWindow_IsUntouched()
    {
        // Within the aged window a legitimately-scheduled backoff retry may still be pending — do not race it.
        await using var db = CreateDb();
        var (_, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 20, terminalAttempts: 1);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DeliveryFailed_AtAttemptCap_IsUntouched()
    {
        // An order that already used every attempt should be dead-lettered, never re-driven — the
        // sweep must not touch it (re-driving would only dead-letter it, but that is RetryDelivery's
        // job at the cap, not this sweep's concern; the "attempts remaining" filter excludes it).
        await using var db = CreateDb();
        var (_, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: MaxAttempts);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DeliveryFailed_WithInFlightDispatchingAttempt_IsUntouched()
    {
        // A 'dispatching' row means a send is mid-flight (crash backstop) — never re-drive on top of it.
        await using var db = CreateDb();
        var (orgId, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: 1);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 2, AttemptedAt = DateTime.UtcNow, IdempotencyKey = "k",
        });
        await db.SaveChangesAsync();
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_UnroutedOrder_NullSupplier_IsUntouched()
    {
        // A null-supplier order has no delivery config to retry against — skip it.
        await using var db = CreateDb();
        var (_, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: 1, routed: false);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Theory]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.ReadyToDeliver)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter)]
    [InlineData(OrderStatusConstants.RejectedBySupplier)]
    [InlineData(OrderStatusConstants.DeliveryHeld)]
    public async Task RunAsync_OtherStatuses_Untouched(string status)
    {
        // Only delivery_failed is this sweep's concern. rejected_by_supplier in particular is a 4xx
        // supplier refusal that must NEVER be auto-retried (retrying the same bytes won't help).
        await using var db = CreateDb();
        var (_, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: 1, status: status);

        var acted = await CreateService(db, new RecordingRetryEnqueuer()).RunAsync(Threshold, default);

        acted.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(status);
    }

    [Fact]
    public async Task RunAsync_MultipleOrgs_RecoversEach_WithItsOwnOrgId()
    {
        await using var db = CreateDb();
        var (orgA, orderA) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: 1);
        var (orgB, orderB) = await SeedFailedAsync(db, updatedMinutesAgo: 300, terminalAttempts: 2);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(2);
        enqueuer.Calls.Should().BeEquivalentTo(new[] { (orderA, orgA), (orderB, orgB) });
    }

    [Fact]
    public async Task RunAsync_MoreStrandsThanBatchCap_ActsOnlyUpToCap_OldestFirst()
    {
        await using var db = CreateDb();
        var (_, oldest) = await SeedFailedAsync(db, updatedMinutesAgo: 300, terminalAttempts: 1);
        var (_, middle) = await SeedFailedAsync(db, updatedMinutesAgo: 260, terminalAttempts: 1);
        var (_, newest) = await SeedFailedAsync(db, updatedMinutesAgo: 220, terminalAttempts: 1);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default, maxBatch: 2);

        acted.Should().Be(2);
        enqueuer.Calls.Select(c => c.OrderId).Should().BeEquivalentTo(new[] { oldest, middle });
        enqueuer.Calls.Select(c => c.OrderId).Should().NotContain(newest);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_SecondRunDoesNothing()
    {
        await using var db = CreateDb();
        var (_, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: 1);
        var service = CreateService(db, new RecordingRetryEnqueuer());

        (await service.RunAsync(Threshold, default)).Should().Be(1);
        // UpdatedAt bumped to now, so the order left the aged window — a second run doesn't re-act.
        (await service.RunAsync(Threshold, default)).Should().Be(0);

        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NoEnqueuerRegistered_StillBumpedAndAudited()
    {
        // A process without the retry enqueuer must still bump the order out of the aged window and
        // audit it (never silently pinning it where the next sweep re-audits it every run).
        await using var db = CreateDb();
        var (_, orderId) = await SeedFailedAsync(db, updatedMinutesAgo: 240, terminalAttempts: 1);

        var acted = await new StrandedFailedDeliveryDetectionService(
            db, NullLogger<StrandedFailedDeliveryDetectionService>.Instance,
            reliability: null, retryEnqueuer: null)
            .RunAsync(Threshold, default);

        acted.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).UpdatedAt
            .Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        (await db.AuditEvents.SingleAsync(e => e.EntityId == orderId)).Action
            .Should().Be("StrandedFailedDeliveryRecovered");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StrandedFailedDeliveryDetectionService CreateService(
        ProcuLinkDbContext db, IRetryDeliveryEnqueuer? enqueuer = null) =>
        new(db, NullLogger<StrandedFailedDeliveryDetectionService>.Instance, reliability: null, enqueuer);

    private static async Task<(Guid OrgId, Guid OrderId)> SeedFailedAsync(
        ProcuLinkDbContext db,
        int updatedMinutesAgo,
        int terminalAttempts,
        bool routed = true,
        string status = OrderStatusConstants.DeliveryFailed)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var updated = DateTime.UtcNow.AddMinutes(-updatedMinutesAgo);

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = routed ? supplierId : null,
            PoNumber = "PO-FAILED",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR",
            Status = status,
            CreatedAt = updated,
            UpdatedAt = updated,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "artifact.csv", CreatedAt = updated,
        });
        for (var i = 1; i <= terminalAttempts; i++)
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
                Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
                AttemptNumber = i, AttemptedAt = updated,
            });
        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private sealed class RecordingRetryEnqueuer : IRetryDeliveryEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }
}
