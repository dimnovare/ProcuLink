using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Reliability/maintenance — data-retention sweep. Verifies the load-bearing selection logic:
/// only rows strictly OLDER than each configured window are pruned, recent rows are always kept,
/// delivery_attempts are pruned conservatively (terminal orders + test-fire only), the sweep is a
/// no-op when disabled, and it is idempotent.
/// <para>
/// The EF InMemory provider does not support <c>ExecuteDeleteAsync</c>, so these tests use a
/// subclass that overrides the (protected virtual) delete hook with a tracked delete. The
/// production selection predicate — the part that decides WHICH rows are deleted — is exercised
/// exactly as in production; only the physical delete mechanism is swapped.
/// </para>
/// </summary>
public class DataRetentionServiceTests
{
    // ── audit_events ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PrunesOldAuditEvents_KeepsRecent()
    {
        await using var db = CreateDb();
        var oldId    = await SeedAuditEventAsync(db, ageDays: 200);  // older than 180d window
        var recentId = await SeedAuditEventAsync(db, ageDays: 10);   // inside the window

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.AuditEvents.Should().Be(1);
        (await db.AuditEvents.AnyAsync(e => e.Id == oldId)).Should().BeFalse("the 200-day-old row is past the window");
        (await db.AuditEvents.AnyAsync(e => e.Id == recentId)).Should().BeTrue("the 10-day-old row is recent and must be kept");
    }

    [Fact]
    public async Task RunAsync_AuditEventExactlyAtWindowBoundary_IsKept()
    {
        await using var db = CreateDb();
        // Just INSIDE the window (1 hour newer than the 180-day cutoff) → must be kept.
        var justInsideId = await SeedAuditEventAsync(db, age: TimeSpan.FromDays(180) - TimeSpan.FromHours(1));

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.AuditEvents.Should().Be(0);
        (await db.AuditEvents.AnyAsync(e => e.Id == justInsideId)).Should().BeTrue();
    }

    // ── po_passport_events ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PrunesOldPassportEvents_KeepsRecent()
    {
        await using var db = CreateDb();
        var oldId    = await SeedPassportEventAsync(db, ageDays: 365);
        var recentId = await SeedPassportEventAsync(db, ageDays: 30);

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.PassportEvents.Should().Be(1);
        (await db.PoPassportEvents.AnyAsync(e => e.Id == oldId)).Should().BeFalse();
        (await db.PoPassportEvents.AnyAsync(e => e.Id == recentId)).Should().BeTrue();
    }

    // ── idempotency_keys (hours window) ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PrunesOldIdempotencyKeys_KeepsRecent()
    {
        await using var db = CreateDb();
        var oldKey    = await SeedIdempotencyKeyAsync(db, age: TimeSpan.FromHours(72));  // past 48h
        var recentKey = await SeedIdempotencyKeyAsync(db, age: TimeSpan.FromHours(1));   // inside 48h

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.IdempotencyKeys.Should().Be(1);
        (await db.IdempotencyKeys.AnyAsync(k => k.Key == oldKey)).Should().BeFalse();
        (await db.IdempotencyKeys.AnyAsync(k => k.Key == recentKey)).Should().BeTrue();
    }

    // ── delivery_attempts (conservative: terminal orders + test-fire only) ───────

    [Fact]
    public async Task RunAsync_PrunesOldAttemptsForTerminalOrders_AndOrphanTestFires_KeepsActiveAndRecent()
    {
        await using var db = CreateDb();

        var terminalOrder = await SeedOrderAsync(db, OrderStatusConstants.Delivered);
        var activeOrder   = await SeedOrderAsync(db, OrderStatusConstants.Delivering);

        // Old attempt on a TERMINAL order → pruned.
        var oldTerminalAttempt = await SeedDeliveryAttemptAsync(db, terminalOrder, ageDays: 200);
        // Old attempt on a STILL-ACTIVE order → KEPT (never drop an in-flight audit trail).
        var oldActiveAttempt   = await SeedDeliveryAttemptAsync(db, activeOrder, ageDays: 200);
        // Old test-fire attempt with no order → pruned.
        var oldTestFire        = await SeedDeliveryAttemptAsync(db, orderId: null, ageDays: 200);
        // Recent attempt on a terminal order → KEPT (inside the window).
        var recentTerminal     = await SeedDeliveryAttemptAsync(db, terminalOrder, ageDays: 5);

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.DeliveryAttempts.Should().Be(2);
        (await db.DeliveryAttempts.AnyAsync(a => a.Id == oldTerminalAttempt)).Should().BeFalse();
        (await db.DeliveryAttempts.AnyAsync(a => a.Id == oldTestFire)).Should().BeFalse();
        (await db.DeliveryAttempts.AnyAsync(a => a.Id == oldActiveAttempt)).Should().BeTrue("the order is still active");
        (await db.DeliveryAttempts.AnyAsync(a => a.Id == recentTerminal)).Should().BeTrue("inside the window");
    }

    // ── order_exceptions ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PrunesOldResolvedAndIgnoredExceptions_KeepsOpenAndRecent()
    {
        await using var db = CreateDb();

        // Old resolved → pruned
        var oldResolvedId  = await SeedOrderExceptionAsync(db, state: "resolved", ageDays: 200);
        // Old ignored → pruned
        var oldIgnoredId   = await SeedOrderExceptionAsync(db, state: "ignored",  ageDays: 200);
        // Old OPEN → NEVER pruned (operator still needs to action it)
        var oldOpenId      = await SeedOrderExceptionAsync(db, state: "open",     ageDays: 200);
        // Recent resolved → kept (inside the window)
        var recentId       = await SeedOrderExceptionAsync(db, state: "resolved", ageDays: 10);

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.OrderExceptions.Should().Be(2);
        (await db.OrderExceptions.AnyAsync(e => e.Id == oldResolvedId)).Should().BeFalse("old resolved is past the window");
        (await db.OrderExceptions.AnyAsync(e => e.Id == oldIgnoredId)).Should().BeFalse("old ignored is past the window");
        (await db.OrderExceptions.AnyAsync(e => e.Id == oldOpenId)).Should().BeTrue("open exceptions are NEVER pruned");
        (await db.OrderExceptions.AnyAsync(e => e.Id == recentId)).Should().BeTrue("recent resolved is inside the window");
    }

    [Fact]
    public async Task RunAsync_OpenExceptions_AreNeverPruned_RegardlessOfAge()
    {
        await using var db = CreateDb();

        // Very old open exception — must survive no matter how old it is.
        var veryOldOpenId = await SeedOrderExceptionAsync(db, state: "open", ageDays: 999);

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.OrderExceptions.Should().Be(0);
        (await db.OrderExceptions.AnyAsync(e => e.Id == veryOldOpenId)).Should().BeTrue("open exceptions must never be deleted");
    }

    // ── disabled / idempotent ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Disabled_DeletesNothing()
    {
        await using var db = CreateDb();
        await SeedAuditEventAsync(db, ageDays: 999);
        await SeedPassportEventAsync(db, ageDays: 999);
        await SeedIdempotencyKeyAsync(db, age: TimeSpan.FromDays(999));

        var disabled = new DataRetentionOptions { Enabled = false };
        var result = await CreateService(db, disabled).RunAsync(default);

        result.Total.Should().Be(0);
        (await db.AuditEvents.CountAsync()).Should().Be(1);
        (await db.PoPassportEvents.CountAsync()).Should().Be(1);
        (await db.IdempotencyKeys.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_SecondRunDeletesNothingMore()
    {
        await using var db = CreateDb();
        await SeedAuditEventAsync(db, ageDays: 200);
        await SeedAuditEventAsync(db, ageDays: 5);   // recent, survives both runs
        var service = CreateService(db, EnabledOptions());

        (await service.RunAsync(default)).AuditEvents.Should().Be(1);
        // Second run: the old row is gone, the recent row is still inside the window.
        (await service.RunAsync(default)).AuditEvents.Should().Be(0);
        (await db.AuditEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NothingPastWindow_IsNoOp()
    {
        await using var db = CreateDb();
        await SeedAuditEventAsync(db, ageDays: 1);
        await SeedIdempotencyKeyAsync(db, age: TimeSpan.FromHours(1));

        var result = await CreateService(db, EnabledOptions()).RunAsync(default);

        result.Total.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_RespectsBatchSize_LeavesRemainderForNextRun()
    {
        await using var db = CreateDb();
        for (var i = 0; i < 5; i++)
            await SeedAuditEventAsync(db, ageDays: 300); // all past the window

        var options = EnabledOptions();
        options.BatchSize = 2; // bound each run to 2 deletes
        var service = CreateService(db, options);

        (await service.RunAsync(default)).AuditEvents.Should().Be(2);
        (await db.AuditEvents.CountAsync()).Should().Be(3);
        (await service.RunAsync(default)).AuditEvents.Should().Be(2);
        (await service.RunAsync(default)).AuditEvents.Should().Be(1);
        (await db.AuditEvents.CountAsync()).Should().Be(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DataRetentionOptions EnabledOptions() => new() { Enabled = true };

    private static TestableDataRetentionService CreateService(ProcuLinkDbContext db, DataRetentionOptions options) =>
        new(db, options, NullLogger<DataRetentionService>.Instance);

    private static async Task<Guid> SeedAuditEventAsync(ProcuLinkDbContext db, int ageDays) =>
        await SeedAuditEventAsync(db, TimeSpan.FromDays(ageDays));

    private static async Task<Guid> SeedAuditEventAsync(ProcuLinkDbContext db, TimeSpan age)
    {
        var id = Guid.NewGuid();
        db.AuditEvents.Add(new AuditEvent
        {
            Id = id,
            OrgId = Guid.NewGuid(),
            EntityType = "Order",
            EntityId = Guid.NewGuid(),
            Action = "Test",
            Payload = JsonDocument.Parse("{}"),
            CreatedAt = DateTime.UtcNow - age,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedPassportEventAsync(ProcuLinkDbContext db, int ageDays)
    {
        var id = Guid.NewGuid();
        db.PoPassportEvents.Add(new PoPassportEvent
        {
            Id = id,
            OrgId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            Stage = "Deliver",
            EventType = "Succeeded",
            ActorType = "system",
            OccurredAt = DateTime.UtcNow.AddDays(-ageDays),
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<string> SeedIdempotencyKeyAsync(ProcuLinkDbContext db, TimeSpan age)
    {
        var key = Guid.NewGuid().ToString();
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key,
            OrgId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow - age,
        });
        await db.SaveChangesAsync();
        return key;
    }

    private static async Task<Guid> SeedOrderAsync(ProcuLinkDbContext db, string status)
    {
        var id = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = id,
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-RET",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedDeliveryAttemptAsync(
        ProcuLinkDbContext db, Guid? orderId, int ageDays)
    {
        var id = Guid.NewGuid();
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = id,
            OrderId = orderId,
            OrgId = Guid.NewGuid(),
            Channel = "http",
            Destination = "https://example.test",
            Status = "delivered",
            AttemptedAt = DateTime.UtcNow.AddDays(-ageDays),
            AttemptNumber = 1,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedOrderExceptionAsync(
        ProcuLinkDbContext db, string state, int ageDays)
    {
        var id = Guid.NewGuid();
        db.OrderExceptions.Add(new OrderException
        {
            Id        = id,
            OrgId     = Guid.NewGuid(),
            OrderId   = Guid.NewGuid(),
            Stage     = "Deliver",
            Code      = "delivery_failed",
            Severity  = "error",
            State     = state,
            Message   = "test",
            CreatedAt = DateTime.UtcNow.AddDays(-ageDays),
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// Test subclass: swaps the bulk <c>ExecuteDeleteAsync</c> (unsupported on InMemory) for a
    /// tracked delete that applies the SAME <c>Take(batchSize)</c> bound, so the production
    /// selection predicate + batch limit are exercised faithfully.
    /// </summary>
    private sealed class TestableDataRetentionService : DataRetentionService
    {
        private readonly ProcuLinkDbContext _db;

        public TestableDataRetentionService(
            ProcuLinkDbContext db, DataRetentionOptions options, Microsoft.Extensions.Logging.ILogger<DataRetentionService> logger)
            : base(db, options, logger)
        {
            _db = db;
        }

        protected override async Task<int> DeleteOldestBatchAsync<T>(
            IQueryable<T> query, int batchSize, CancellationToken ct)
        {
            var rows = await query.Take(batchSize).ToListAsync(ct);
            if (rows.Count == 0)
                return 0;
            _db.Set<T>().RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }
}
