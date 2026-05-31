using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class OrderExceptionServiceTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (Guid orgId, Guid orderId) SeedOrder(ProcuLinkDbContext db, string status, bool unresolvedLine)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = Guid.NewGuid(),
            PoNumber = "PO-1", Status = status, Currency = "EUR",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                        BuyerItemCode = "B1", NeedsReview = unresolvedLine, Quantity = 1, UnitPrice = 1 }
            }
        });
        db.SaveChanges();
        return (orgId, orderId);
    }

    [Fact]
    public async Task Reconcile_PendingReview_OpensUnresolvedMappingException()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "pending_review", true);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        var ex = await db.OrderExceptions.SingleAsync();
        Assert.Equal("unresolved_mapping", ex.Code);
        Assert.Equal("open", ex.State);
    }

    [Fact]
    public async Task Reconcile_IsIdempotent_NoDuplicateOpenException()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "pending_review", true);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        Assert.Equal(1, await db.OrderExceptions.CountAsync(e => e.State == "open"));
    }

    [Fact]
    public async Task Reconcile_ProblemCleared_AutoResolvesOpenException()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "pending_review", true);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);

        var order = await db.PurchaseOrders.Include(o => o.Lines).SingleAsync();
        order.Status = "ready";
        order.Lines[0].NeedsReview = false;
        await db.SaveChangesAsync();

        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        Assert.Equal(0, await db.OrderExceptions.CountAsync(e => e.State == "open"));
        Assert.Equal(1, await db.OrderExceptions.CountAsync(e => e.State == "resolved"));
    }

    [Fact]
    public async Task Resolve_SetsStateResolved()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "delivery_failed", false);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        var ex = await db.OrderExceptions.SingleAsync();
        var ok = await svc.ResolveAsync(orgId, ex.Id, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal("resolved", (await db.OrderExceptions.SingleAsync()).State);
    }

    [Fact]
    public async Task Reconcile_TransformFailed_OpensTransformException()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "transform_failed", false);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        var ex = await db.OrderExceptions.SingleAsync();
        Assert.Equal("transform_failed", ex.Code);
        Assert.Equal("error", ex.Severity);
    }

    [Fact]
    public async Task Reconcile_DeadLetter_OpensCriticalException()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "delivery_dead_letter", false);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        var ex = await db.OrderExceptions.SingleAsync();
        Assert.Equal("dead_letter", ex.Code);
        Assert.Equal("critical", ex.Severity);
    }

    [Fact]
    public async Task Reconcile_ParseFailed_OpensParseFailedException()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "failed", false);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        var ex = await db.OrderExceptions.SingleAsync();
        Assert.Equal("parse_failed", ex.Code);
    }

    [Fact]
    public async Task Ignore_ExcludedFromAutoResolve()
    {
        var db = MakeDb();
        var (orgId, orderId) = SeedOrder(db, "pending_review", true);
        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        var ex = await db.OrderExceptions.SingleAsync();
        await svc.IgnoreAsync(orgId, ex.Id, CancellationToken.None);

        // Re-reconcile while problem still present — ignored row stays ignored, no new open row
        await svc.ReconcileAsync(orgId, orderId, CancellationToken.None);
        Assert.Equal(0, await db.OrderExceptions.CountAsync(e => e.State == "open"));
        Assert.Equal(1, await db.OrderExceptions.CountAsync(e => e.State == "ignored"));
    }
}
