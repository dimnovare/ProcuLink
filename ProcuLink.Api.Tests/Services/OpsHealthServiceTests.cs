using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class OpsHealthServiceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PurchaseOrderEntity Order(
        Guid orgId, Guid supplierId, string status,
        DateTime? updatedAt = null, bool slaBreached = false) =>
        new()
        {
            Id          = Guid.NewGuid(),
            OrgId       = orgId,
            SupplierId  = supplierId,
            PoNumber    = $"PO-{Guid.NewGuid():N}",
            Status      = status,
            OrderDate   = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency    = "EUR",
            SlaBreached = slaBreached,
            CreatedAt   = DateTime.UtcNow.AddHours(-2),
            UpdatedAt   = updatedAt ?? DateTime.UtcNow,
        };

    // ── GetHealthAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_CountsProblemStatuses_OrgScoped()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var otherOrg   = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var stale      = DateTime.UtcNow.AddHours(-1);   // older than 30-min threshold
        var fresh      = DateTime.UtcNow;                // within threshold

        db.PurchaseOrders.AddRange(
            Order(orgId, supplierId, OrderStatusConstants.Parsing,    stale),  // stuck parsing
            Order(orgId, supplierId, OrderStatusConstants.Parsing,    fresh),  // NOT stuck
            Order(orgId, supplierId, OrderStatusConstants.Delivering, stale),  // stuck delivering
            Order(orgId, supplierId, OrderStatusConstants.TransformFailed),
            Order(orgId, supplierId, OrderStatusConstants.DeliveryFailed),
            Order(orgId, supplierId, OrderStatusConstants.DeliveryDeadLetter),
            Order(orgId, supplierId, OrderStatusConstants.DeliveryDeadLetter),
            Order(orgId, supplierId, OrderStatusConstants.RejectedBySupplier),
            Order(orgId, supplierId, OrderStatusConstants.Failed),
            Order(orgId, supplierId, OrderStatusConstants.Delivered, fresh, slaBreached: false),
            Order(orgId, supplierId, OrderStatusConstants.DeliveryFailed, fresh, slaBreached: true),
            // other org — must never be counted
            Order(otherOrg, supplierId, OrderStatusConstants.DeliveryDeadLetter, stale)
        );

        db.OrderExceptions.AddRange(
            new OrderException { Id = Guid.NewGuid(), OrgId = orgId, OrderId = Guid.NewGuid(),
                Stage = "Deliver", Code = "dead_letter", State = "open", CreatedAt = DateTime.UtcNow },
            new OrderException { Id = Guid.NewGuid(), OrgId = orgId, OrderId = Guid.NewGuid(),
                Stage = "Deliver", Code = "delivery_failed", State = "resolved", CreatedAt = DateTime.UtcNow },
            new OrderException { Id = Guid.NewGuid(), OrgId = otherOrg, OrderId = Guid.NewGuid(),
                Stage = "Deliver", Code = "dead_letter", State = "open", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var svc = new OpsHealthService(db);
        var s   = await svc.GetHealthAsync(orgId, CancellationToken.None);

        s.ParsingStuck.Should().Be(1);
        s.DeliveringStuck.Should().Be(1);
        s.TransformFailed.Should().Be(1);
        s.DeliveryFailed.Should().Be(2);          // one stale + one fresh sla-breached
        s.DeliveryDeadLetter.Should().Be(2);
        s.RejectedBySupplier.Should().Be(1);
        s.Failed.Should().Be(1);
        s.SlaBreached.Should().Be(1);             // the non-delivered breached order
        s.OpenExceptions.Should().Be(1);          // only org-scoped open
        s.StuckThresholdMinutes.Should().Be(30);
        s.TotalProblemOrders.Should().Be(1 + 1 + 1 + 2 + 2 + 1 + 1);
    }

    [Fact]
    public async Task GetHealth_EmptyOrg_AllZero()
    {
        await using var db = NewDb();
        var svc = new OpsHealthService(db);
        var s   = await svc.GetHealthAsync(Guid.NewGuid(), CancellationToken.None);

        s.TotalProblemOrders.Should().Be(0);
        s.OpenExceptions.Should().Be(0);
        s.DeliveryDeadLetter.Should().Be(0);
    }

    // ── ListDeadLetterAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ListDeadLetter_ReturnsDeadLetterOrders_WithLatestAttemptError_OrgScoped()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var otherOrg   = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Acme Supplies", CreatedAt = DateTime.UtcNow,
        });

        var dl = Order(orgId, supplierId, OrderStatusConstants.DeliveryDeadLetter);
        db.PurchaseOrders.Add(dl);
        db.PurchaseOrders.Add(Order(orgId, supplierId, OrderStatusConstants.DeliveryFailed)); // excluded by default
        db.PurchaseOrders.Add(Order(otherOrg, supplierId, OrderStatusConstants.DeliveryDeadLetter)); // cross-org

        db.DeliveryAttempts.AddRange(
            new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = dl.Id, OrgId = orgId,
                Channel = "http", Destination = "https://x", Status = "failed",
                AttemptedAt = DateTime.UtcNow.AddMinutes(-10), AttemptNumber = 1,
                ErrorMessage = "older error",
            },
            new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = dl.Id, OrgId = orgId,
                Channel = "http", Destination = "https://x", Status = "failed",
                AttemptedAt = DateTime.UtcNow, AttemptNumber = 3,
                ErrorMessage = "latest error", ResponseCode = 503,
            });
        await db.SaveChangesAsync();

        var svc  = new OpsHealthService(db);
        var rows = await svc.ListDeadLetterAsync(orgId, includeFailed: false, CancellationToken.None);

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.OrderId.Should().Be(dl.Id);
        row.SupplierName.Should().Be("Acme Supplies");
        row.Status.Should().Be(OrderStatusConstants.DeliveryDeadLetter);
        row.DeliveryAttempts.Should().Be(2);
        row.LastError.Should().Be("latest error");
        row.LastResponseCode.Should().Be(503);
    }

    [Fact]
    public async Task ListDeadLetter_IncludeFailed_AlsoReturnsDeliveryFailed()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.PurchaseOrders.AddRange(
            Order(orgId, supplierId, OrderStatusConstants.DeliveryDeadLetter),
            Order(orgId, supplierId, OrderStatusConstants.DeliveryFailed));
        await db.SaveChangesAsync();

        var svc  = new OpsHealthService(db);
        var rows = await svc.ListDeadLetterAsync(orgId, includeFailed: true, CancellationToken.None);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Status).Should().Contain(new[]
        {
            OrderStatusConstants.DeliveryDeadLetter, OrderStatusConstants.DeliveryFailed,
        });
    }
}
