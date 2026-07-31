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

        var orderId = (await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default)).OrderId;

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

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);
        await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);

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

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);

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

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);

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

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);
        await svc.CreateAndEnqueueAsync(orgId, "user_abc", deliverToEmail: null, default);

        var catalogCount = await db.SupplierProducts.CountAsync(p => p.OrgId == orgId);
        Assert.Equal(5, catalogCount);

        var mappingCount = await db.ItemMappings.CountAsync(m => m.OrgId == orgId);
        Assert.Equal(2, mappingCount);
    }

    // ── WP-27: the delivery setup that closes the practice loop ───────────────
    // Before WP-27 nothing seeded a SupplierDeliveryConfig, so every practice run ended at
    // "no delivery is set up" — the class docstring promised a parse → transform → DELIVER
    // loop the code did not close, and the dashboard checklist said so out loud.

    [Fact]
    public async Task CreateAndEnqueueAsync_SeedsEmailDeliveryConfig_WhenAddressSupplied()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", "maria@northgate.example", default);

        Assert.True(result.DeliveryConfigured);

        var supplier = await db.Suppliers.SingleAsync(s => s.OrgId == orgId && s.IsSample);
        var config = await db.SupplierDeliveryConfigs
            .SingleAsync(c => c.OrgId == orgId && c.SupplierId == supplier.Id);

        // `email` is the ONLY offered protocol that needs zero cooperation from a real supplier:
        // mail leaves from ProcuLink's own verified sender, so any address works on day one.
        Assert.Equal("email", config.Protocol);
        Assert.Contains("maria@northgate.example", config.ConfigJson);
        Assert.Equal("csv", config.OutputFormat);
        // The fixture leaves line 3 unmapped on purpose, so an auto-run would only 422 at
        // transform — and the explicit send press is the moment the practice run exists to teach.
        Assert.False(config.AutoDeliver);
        // The email API takes no per-supplier credentials, and no secret may enter ConfigJson.
        Assert.Equal(string.Empty, config.EncryptedCredentials);
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_SkipsDeliveryConfig_WhenDeploymentHasNoEmailProvider()
    {
        // A deployment with no email-API token cannot send at all. Seeding here would turn the
        // honest "delivery not set up" ending into a guaranteed delivery_failed — strictly worse.
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db, emailConfigured: false);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", "maria@northgate.example", default);

        Assert.False(result.DeliveryConfigured);
        Assert.Empty(await db.SupplierDeliveryConfigs.Where(c => c.OrgId == orgId).ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("one@x.example, two@y.example")] // a list is not a single practice recipient
    public async Task CreateAndEnqueueAsync_SkipsDeliveryConfig_WhenAddressUnusable(string? address)
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        var result = await svc.CreateAndEnqueueAsync(orgId, "user_abc", address, default);

        Assert.False(result.DeliveryConfigured);
        Assert.Empty(await db.SupplierDeliveryConfigs.Where(c => c.OrgId == orgId).ToListAsync());
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_RepointsExistingDeliveryConfig_RatherThanAddingASecond()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", "first@example.test", default);
        await svc.CreateAndEnqueueAsync(orgId, "user_abc", "second@example.test", default);

        var configs = await db.SupplierDeliveryConfigs.Where(c => c.OrgId == orgId).ToListAsync();
        var config = Assert.Single(configs);
        Assert.Contains("second@example.test", config.ConfigJson);
        Assert.DoesNotContain("first@example.test", config.ConfigJson);
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_EmailTemplatesUseSingleBracePlaceholders()
    {
        // EmailApiDeliveryDispatcher.BuildFromTemplate does a literal Replace of "{poNumber}" and
        // "{fileName}". A "{{poNumber}}" template would ship braces to the supplier verbatim.
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", "maria@northgate.example", default);

        var config = await db.SupplierDeliveryConfigs.SingleAsync(c => c.OrgId == orgId);
        Assert.Contains("{poNumber}", config.ConfigJson);
        Assert.Contains("{fileName}", config.ConfigJson);
        Assert.DoesNotContain("{{", config.ConfigJson);
    }
}
