using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure.Tests.Support;

namespace ProcuLink.Infrastructure.Tests.Services;

public class SampleOrderServiceTests
{
    [Fact]
    public async Task CreateAndEnqueueAsync_CreatesSampleSupplier_IfMissing()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db, out var enqueuer);

        var orderId = await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        var samples = await db.Suppliers.Where(s => s.OrgId == orgId && s.IsSample).ToListAsync();
        Assert.Single(samples);
        Assert.Equal("__sample__", samples[0].Code);
        Assert.True(samples[0].IsSample);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.True(order.IsSample);
        Assert.Equal(samples[0].Id, order.SupplierId);
        Assert.Single(enqueuer.Enqueued);
        Assert.Equal(orderId, enqueuer.Enqueued[0].OrderId);
        Assert.Equal(orgId,   enqueuer.Enqueued[0].OrgId);
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_ReusesExistingSampleSupplier()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);
        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        var supplierCount = await db.Suppliers.CountAsync(s => s.OrgId == orgId && s.IsSample);
        Assert.Equal(1, supplierCount);

        var orderCount = await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId && o.IsSample);
        Assert.Equal(2, orderCount);
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_DoesNotIncrementOrdersThisMonth()
    {
        // Quota is computed by StripeBillingService.CountOrdersAsync which filters out
        // IsSample = true (committed in Phase 6.1). Verify the SampleOrderService produces
        // an order with that flag so the quota filter excludes it. Mirrors the query the
        // billing service performs against the monthly window.
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        var billableCount = await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId && !o.IsSample);
        Assert.Equal(0, billableCount);

        var totalCount = await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId);
        Assert.Equal(1, totalCount);
    }
}
