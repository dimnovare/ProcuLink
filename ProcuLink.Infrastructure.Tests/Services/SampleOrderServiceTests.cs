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

    // ── catalog + 2-of-3 mapping seeding (onboarding overhaul, task 2) ────────
    // Pinned to ProcuLink.Api\Fixtures\sample-order.csv: lines 1-2 (ACME-WIDGET-A/B)
    // get seeded mappings; line 3 (ACME-BRACKET-S) is the deliberate gap — its
    // supplier code exists ONLY in the catalog, the user maps it manually.

    [Fact]
    public async Task CreateAndEnqueueAsync_SeedsCatalog_AndMappingsForLinesOneAndTwoOnly()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        var supplier = await db.Suppliers.SingleAsync(s => s.OrgId == orgId && s.IsSample);

        var catalog = await db.SupplierProducts
            .Where(p => p.OrgId == orgId && p.SupplierId == supplier.Id)
            .ToListAsync();
        Assert.Equal(5, catalog.Count);
        // Line 3's supplier code MUST be discoverable in the catalog (the manual rep).
        Assert.Contains(catalog, p => p.Code == "SMP-BRACKET-S");
        Assert.All(catalog, p => Assert.True(p.IsActive));

        var mappings = await db.ItemMappings
            .Where(m => m.OrgId == orgId && m.SupplierId == supplier.Id)
            .ToListAsync();
        Assert.Equal(2, mappings.Count);
        Assert.Contains(mappings, m => m.BuyerItemCode == "ACME-WIDGET-A" && m.SupplierItemCode == "SMP-WIDGET-A-10");
        Assert.Contains(mappings, m => m.BuyerItemCode == "ACME-WIDGET-B" && m.SupplierItemCode == "SMP-WIDGET-B-20");
        // The deliberate gap: fixture line 3 must NOT be pre-mapped.
        Assert.DoesNotContain(mappings, m => m.BuyerItemCode == "ACME-BRACKET-S");
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_SeedingIsIdempotent_RunTwiceProducesNoDuplicates()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);
        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        var catalogCount = await db.SupplierProducts.CountAsync(p => p.OrgId == orgId);
        Assert.Equal(5, catalogCount);

        var mappingCount = await db.ItemMappings.CountAsync(m => m.OrgId == orgId);
        Assert.Equal(2, mappingCount);
    }
}
