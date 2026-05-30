using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Group O reliability — SLA timer sweep. Verifies that overdue, unconfirmed deliveries are
/// flagged exactly once (idempotent), that terminal/confirmed orders are excluded, and that
/// flagging is org-scoped. Uses the full <see cref="ProcuLinkDbContext"/> so the audit-event
/// write path is exercised.
/// </summary>
public class DeliverySlaServiceTests
{
    [Fact]
    public async Task RunAsync_OverdueUnconfirmedOrder_IsFlaggedWithAuditEvent()
    {
        await using var db = CreateDb();
        var ids = await SeedOrderAsync(
            db, status: OrderStatusConstants.Delivering, dueAt: DateTime.UtcNow.AddMinutes(-5));

        var marked = await new DeliverySlaService(db, NullLogger<DeliverySlaService>.Instance)
            .RunAsync(default);

        marked.Should().Be(1);

        var order = await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId);
        order.SlaBreached.Should().BeTrue();

        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == ids.OrderId);
        audit.Action.Should().Be("DeliverySlaBreached");
        audit.OrgId.Should().Be(ids.OrgId);
        audit.EntityType.Should().Be("Order");
    }

    [Fact]
    public async Task RunAsync_NotYetDueOrder_IsNotFlagged()
    {
        await using var db = CreateDb();
        var ids = await SeedOrderAsync(
            db, status: OrderStatusConstants.Delivering, dueAt: DateTime.UtcNow.AddMinutes(+30));

        var marked = await new DeliverySlaService(db, NullLogger<DeliverySlaService>.Instance)
            .RunAsync(default);

        marked.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).SlaBreached.Should().BeFalse();
        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_NullDueAt_IsNotFlagged()
    {
        await using var db = CreateDb();
        var ids = await SeedOrderAsync(
            db, status: OrderStatusConstants.ReadyToDeliver, dueAt: null);

        var marked = await new DeliverySlaService(db, NullLogger<DeliverySlaService>.Instance)
            .RunAsync(default);

        marked.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).SlaBreached.Should().BeFalse();
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("delivery_dead_letter")]
    public async Task RunAsync_TerminalOrConfirmedStatus_IsExcludedEvenWhenDueAtElapsed(string status)
    {
        await using var db = CreateDb();
        // Defensive: even if a stale DueAt lingers, delivered / dead-letter must never be flagged.
        var ids = await SeedOrderAsync(db, status: status, dueAt: DateTime.UtcNow.AddMinutes(-60));

        var marked = await new DeliverySlaService(db, NullLogger<DeliverySlaService>.Instance)
            .RunAsync(default);

        marked.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).SlaBreached.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_AlreadyBreached_IsIdempotentNoSecondAudit()
    {
        await using var db = CreateDb();
        var ids = await SeedOrderAsync(
            db, status: OrderStatusConstants.Delivering, dueAt: DateTime.UtcNow.AddMinutes(-5));

        var svc = new DeliverySlaService(db, NullLogger<DeliverySlaService>.Instance);

        (await svc.RunAsync(default)).Should().Be(1);
        // Second sweep must be a no-op — order is already flagged.
        (await svc.RunAsync(default)).Should().Be(0);

        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId && e.Action == "DeliverySlaBreached"))
            .Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_FlagsAcrossOrganisations_PreservingOrgScope()
    {
        await using var db = CreateDb();
        var a = await SeedOrderAsync(db, OrderStatusConstants.Delivering, DateTime.UtcNow.AddMinutes(-5));
        var b = await SeedOrderAsync(db, OrderStatusConstants.Delivering, DateTime.UtcNow.AddMinutes(-5));

        var marked = await new DeliverySlaService(db, NullLogger<DeliverySlaService>.Instance)
            .RunAsync(default);

        marked.Should().Be(2);
        (await db.AuditEvents.SingleAsync(e => e.EntityId == a.OrderId)).OrgId.Should().Be(a.OrgId);
        (await db.AuditEvents.SingleAsync(e => e.EntityId == b.OrderId)).OrgId.Should().Be(b.OrgId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(Guid OrgId, Guid OrderId)> SeedOrderAsync(
        ProcuLinkDbContext db, string status, DateTime? dueAt)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-SLA-001",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = status,
            DeliveryDueAt = dueAt,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }
}
