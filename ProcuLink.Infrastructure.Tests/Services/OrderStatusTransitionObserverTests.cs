using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// P2 hardening — the LOG-ONLY order state-machine observer. Three guarantees:
/// (1) every legitimate pipeline transition is SILENT (no warning — false positives
/// would train operators to ignore the signal); (2) an unexpected transition logs a
/// WARNING; (3) the observer NEVER blocks or throws — the save always proceeds, even
/// when the logger itself explodes.
/// </summary>
public class OrderStatusTransitionObserverTests
{
    // ── Pure transition-map checks ───────────────────────────────────────────

    [Theory]
    // Parse pipeline
    [InlineData(OrderStatusConstants.PendingParse,  OrderStatusConstants.Parsing)]
    [InlineData(OrderStatusConstants.Parsing,       OrderStatusConstants.PendingReview)]
    [InlineData(OrderStatusConstants.Parsing,       OrderStatusConstants.Ready)]
    [InlineData(OrderStatusConstants.Parsing,       OrderStatusConstants.Failed)]
    [InlineData(OrderStatusConstants.Parsing,       OrderStatusConstants.PendingParse)] // stuck requeue
    [InlineData(OrderStatusConstants.Failed,        OrderStatusConstants.Parsing)]      // re-parse retry
    // Review loop
    [InlineData(OrderStatusConstants.PendingReview, OrderStatusConstants.Ready)]
    [InlineData(OrderStatusConstants.Ready,         OrderStatusConstants.PendingReview)]
    // Transform
    [InlineData(OrderStatusConstants.Ready,         OrderStatusConstants.Transforming)]
    [InlineData(OrderStatusConstants.Transforming,  OrderStatusConstants.ReadyToDeliver)]
    [InlineData(OrderStatusConstants.Transforming,  OrderStatusConstants.Ready)]   // validation revert
    [InlineData(OrderStatusConstants.Transforming,  OrderStatusConstants.Failed)]  // stuck detection
    // Delivery
    [InlineData(OrderStatusConstants.ReadyToDeliver,     OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.Delivering,         OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.Delivering,         OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.Delivering,         OrderStatusConstants.RejectedBySupplier)]
    [InlineData(OrderStatusConstants.Delivering,         OrderStatusConstants.DeliveryDeadLetter)] // stuck delivery
    [InlineData(OrderStatusConstants.DeliveryFailed,     OrderStatusConstants.Delivering)]         // retry
    [InlineData(OrderStatusConstants.DeliveryFailed,     OrderStatusConstants.DeliveryHeld)]        // A5: retry billing hold
    [InlineData(OrderStatusConstants.DeliveryFailed,     OrderStatusConstants.DeliveryDeadLetter)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter, OrderStatusConstants.Delivering)]         // ops requeue
    [InlineData(OrderStatusConstants.Delivered,          OrderStatusConstants.RejectedBySupplier)] // late business NACK
    // Park (delivery_unconfirmed): unknown-outcome crash recovery on a non-idempotent channel
    [InlineData(OrderStatusConstants.Delivering,         OrderStatusConstants.DeliveryUnconfirmed)] // park: unknown-outcome crash recovery
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Delivering)] // resume after unconfirmed
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Delivered)] // confirmed after the fact
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Ready)] // MV-1 sibling: mapping edit after unconfirmed
    // Self-transition is always fine (idempotent re-write of the same status).
    [InlineData(OrderStatusConstants.Delivered, OrderStatusConstants.Delivered)]
    public void IsExpected_LegitimatePipelineTransitions_AreExpected(string from, string to)
        => Assert.True(OrderStatusTransitionObserver.IsExpected(from, to));

    [Theory]
    [InlineData(OrderStatusConstants.Delivered,  OrderStatusConstants.Parsing)]       // backwards into parse
    [InlineData(OrderStatusConstants.Delivered,  OrderStatusConstants.Transforming)]  // re-transform of a delivered order
    [InlineData(OrderStatusConstants.Ready,      OrderStatusConstants.Delivered)]     // skipped transform+delivery
    [InlineData(OrderStatusConstants.Parsing,    OrderStatusConstants.Delivering)]    // skipped half the pipeline
    [InlineData(OrderStatusConstants.PendingParse, OrderStatusConstants.Delivered)]
    [InlineData("definitely_not_a_status",       OrderStatusConstants.Ready)]         // unknown source
    [InlineData(OrderStatusConstants.Ready,      "definitely_not_a_status")]          // unknown target
    [InlineData(null,                            OrderStatusConstants.Ready)]
    [InlineData(OrderStatusConstants.Ready,      null)]
    [InlineData("",                              OrderStatusConstants.Ready)]
    public void IsExpected_UnexpectedOrUnknownTransitions_AreNotExpected(string? from, string? to)
        => Assert.False(OrderStatusTransitionObserver.IsExpected(from, to));

    // ── Interceptor behaviour through a real SaveChanges ─────────────────────

    [Fact]
    public async Task SaveChanges_LegitimateTransition_IsSilent_AndPersists()
    {
        var logger = new CapturingLogger();
        var (options, orderId) = await SeedAsync(logger, OrderStatusConstants.Ready);

        await using (var db = new ProcuLinkDbContext(options))
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            order.Status = OrderStatusConstants.Transforming;
            await db.SaveChangesAsync();
        }

        Assert.Empty(logger.Warnings);
        await using var verify = new ProcuLinkDbContext(options);
        Assert.Equal(OrderStatusConstants.Transforming,
            (await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
    }

    [Fact]
    public async Task SaveChanges_DeliveryFailedToDeliveryHeld_IsSilent_AndPersists()
    {
        // A5: RetryDeliveryAsync's billing gate holds a delivery_failed order (org lapsed at retry
        // time) via HoldForBillingAsync → delivery_held. This transition MUST be in the allowed map,
        // or the log-only observer would emit a spurious WARNING on every real billing hold.
        var logger = new CapturingLogger();
        var (options, orderId) = await SeedAsync(logger, OrderStatusConstants.DeliveryFailed);

        await using (var db = new ProcuLinkDbContext(options))
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            order.Status = OrderStatusConstants.DeliveryHeld;
            await db.SaveChangesAsync();
        }

        Assert.Empty(logger.Warnings);
        await using var verify = new ProcuLinkDbContext(options);
        Assert.Equal(OrderStatusConstants.DeliveryHeld,
            (await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
    }

    // The park (unknown-outcome crash recovery on a non-idempotent channel) moves
    // delivering → delivery_unconfirmed. Both transition maps must carry it, or the observer
    // logs a spurious "unexpected transition" for a move the system performs on purpose —
    // the exact map drift d4d6eac had to fix for A5.
    [Theory]
    [InlineData(OrderStatusConstants.Delivering, OrderStatusConstants.DeliveryUnconfirmed)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Ready)]
    public async Task ParkTransitions_AreRegisteredInBothMaps_AndObserverStaysSilent(string from, string to)
    {
        Assert.True(OrderStatusMachine.IsAllowed(from, to), $"{from} -> {to} is a real flow the park performs");

        // Observer silence via the same mechanism as the A5 sibling
        // (SaveChanges_DeliveryFailedToDeliveryHeld_IsSilent_AndPersists): a real SaveChanges
        // through the interceptor with a capturing logger, not just the pure IsExpected check —
        // this also catches a bug in the interceptor wiring itself, not only in the static maps.
        var logger = new CapturingLogger();
        var (options, orderId) = await SeedAsync(logger, from);

        await using (var db = new ProcuLinkDbContext(options))
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            order.Status = to;
            await db.SaveChangesAsync();
        }

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task SaveChanges_UnexpectedTransition_LogsWarning_ButNeverBlocks()
    {
        var logger = new CapturingLogger();
        var (options, orderId) = await SeedAsync(logger, OrderStatusConstants.Delivered);

        await using (var db = new ProcuLinkDbContext(options))
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            order.Status = OrderStatusConstants.Parsing; // nonsensical backwards move
            await db.SaveChangesAsync();                 // must NOT throw
        }

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("delivered", warning);
        Assert.Contains("parsing", warning);

        // LOG-ONLY: the weird write still persisted — observation, not enforcement.
        await using var verify = new ProcuLinkDbContext(options);
        Assert.Equal(OrderStatusConstants.Parsing,
            (await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
    }

    [Fact]
    public async Task SaveChanges_AddedOrder_AndNonStatusUpdate_AreSilent()
    {
        var logger = new CapturingLogger();
        // Seeding itself inserts an order (EntityState.Added) — must not warn.
        var (options, orderId) = await SeedAsync(logger, OrderStatusConstants.Ready);

        await using (var db = new ProcuLinkDbContext(options))
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            order.Currency  = "USD";            // non-status modification
            order.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task SaveChanges_WhenLoggerThrows_SaveStillSucceeds()
    {
        var logger = new CapturingLogger { ThrowOnLog = true };
        var (options, orderId) = await SeedAsync(logger, OrderStatusConstants.Delivered);

        await using (var db = new ProcuLinkDbContext(options))
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            order.Status = OrderStatusConstants.Parsing; // would log a warning — logger explodes
            await db.SaveChangesAsync();                 // must STILL not throw
        }

        await using var verify = new ProcuLinkDbContext(options);
        Assert.Equal(OrderStatusConstants.Parsing,
            (await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
    }

    [Fact]
    public void SaveChanges_Synchronous_UnexpectedTransition_LogsWarning()
    {
        var logger   = new CapturingLogger();
        var observer = new OrderStatusTransitionObserver(logger);
        var options  = BuildOptions(observer);

        var orderId = SeedSync(options, OrderStatusConstants.Delivered);

        using (var db = new ProcuLinkDbContext(options))
        {
            var order = db.PurchaseOrders.Single(o => o.Id == orderId);
            order.Status = OrderStatusConstants.PendingParse;
            db.SaveChanges(); // sync path uses the SavingChanges override
        }

        Assert.Single(logger.Warnings);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DbContextOptions<ProcuLinkDbContext> BuildOptions(OrderStatusTransitionObserver observer) =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(observer)
            .Options;

    private static async Task<(DbContextOptions<ProcuLinkDbContext> options, Guid orderId)> SeedAsync(
        CapturingLogger logger, string initialStatus)
    {
        var observer = new OrderStatusTransitionObserver(logger);
        var options  = BuildOptions(observer);
        var orderId  = SeedSync(options, initialStatus);
        await Task.CompletedTask;
        return (options, orderId);
    }

    private static Guid SeedSync(DbContextOptions<ProcuLinkDbContext> options, string initialStatus)
    {
        var orderId = Guid.NewGuid();
        using var db = new ProcuLinkDbContext(options);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-OBS-1",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = initialStatus,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        db.SaveChanges();
        return orderId;
    }

    /// <summary>Captures warning messages; optionally throws to prove the observer swallows logger failures.</summary>
    private sealed class CapturingLogger : ILogger<OrderStatusTransitionObserver>
    {
        public List<string> Warnings { get; } = new();
        public bool ThrowOnLog { get; init; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog) throw new InvalidOperationException("Logger exploded (test double).");
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
