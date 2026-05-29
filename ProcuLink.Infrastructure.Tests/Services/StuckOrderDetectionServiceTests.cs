using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// P0 reliability — stuck-order detection. Orders left in a transient pipeline
/// status ('parsing' / 'transforming') past the timeout are failed with a
/// 'StuckTimeout' audit event. Uses the full ProcuLinkDbContext on InMemory.
/// </summary>
public class StuckOrderDetectionServiceTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task RunAsync_ParsingOrderOlderThanThreshold_MarksFailedWithAuditEvent()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);

        var marked = await CreateService(db).RunAsync(Threshold, default);

        marked.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Failed);
        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckTimeout");
        audit.EntityType.Should().Be("Order");
    }

    [Fact]
    public async Task RunAsync_TransformingOrderOlderThanThreshold_MarksFailed()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Transforming, updatedMinutesAgo: 60);

        var marked = await CreateService(db).RunAsync(Threshold, default);

        marked.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Failed);
    }

    [Fact]
    public async Task RunAsync_RecentTransientOrder_IsUntouched()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 5);

        var marked = await CreateService(db).RunAsync(Threshold, default);

        marked.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Parsing);
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_OldOrderInNonTransientStatus_IsUntouched()
    {
        await using var db = CreateDb();
        // A delivered order updated long ago must NOT be touched — only
        // parsing/transforming count as "stuck".
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Delivered, updatedMinutesAgo: 120);

        var marked = await CreateService(db).RunAsync(Threshold, default);

        marked.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_SecondRunMarksNothing()
    {
        await using var db = CreateDb();
        await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);
        var service = CreateService(db);

        (await service.RunAsync(Threshold, default)).Should().Be(1);
        (await service.RunAsync(Threshold, default)).Should().Be(0);

        // Exactly one StuckTimeout event — not duplicated across runs.
        (await db.AuditEvents.CountAsync(e => e.Action == "StuckTimeout")).Should().Be(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StuckOrderDetectionService CreateService(ProcuLinkDbContext db) =>
        new(db, NullLogger<StuckOrderDetectionService>.Instance);

    private static async Task<Guid> SeedOrderAsync(ProcuLinkDbContext db, string status, int updatedMinutesAgo)
    {
        var orderId = Guid.NewGuid();
        var updated = DateTime.UtcNow.AddMinutes(-updatedMinutesAgo);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-STUCK",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR",
            Status = status,
            CreatedAt = updated,
            UpdatedAt = updated,
        });
        await db.SaveChangesAsync();
        return orderId;
    }
}
