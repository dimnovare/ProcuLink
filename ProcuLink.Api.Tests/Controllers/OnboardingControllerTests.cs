using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// GET /api/onboarding/status — the extended server-truth checklist signals
/// (design: docs/superpowers/plans/2026-06-12-onboarding-overhaul-design.md, task 1).
/// The load-bearing invariant: every flag/count EXCLUDES IsSample data, so a
/// sample-only org intentionally reports all-false / zero counts.
/// </summary>
public class OnboardingControllerTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OnboardingController Ctrl, Guid OrgId, ProcuLinkDbContext Db) Build()
    {
        var db     = MakeDb();
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return (new OnboardingController(db, tenant.Object), orgId, db);
    }

    private static async Task<OnboardingStatusDto> GetStatusAsync(OnboardingController ctrl)
    {
        var result = await ctrl.GetStatus(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<OnboardingStatusDto>().Subject;
    }

    // ── sample exclusion: the headline behaviour ─────────────────────────────

    [Fact]
    public async Task SampleOnlyOrg_ReportsAllFalse_ButStillPointsAtTheSample()
    {
        var (ctrl, orgId, db) = Build();

        // A sample-only org: the __sample__ supplier fully pre-wired (catalog, mappings,
        // delivery config would never exist for it, but seed one anyway to prove the
        // exclusion is supplier-driven), plus a fully delivered sample order with a
        // resolved line. None of it may certify a milestone.
        var supplier = AddSupplier(db, orgId, isSample: true, code: "__sample__");
        var order    = AddOrder(db, orgId, supplier.Id, "delivered", isSample: true);
        AddResolvedLine(db, order.Id);
        AddCatalogRow(db, orgId, supplier.Id, "SMP-BRACKET-S");
        AddItemMapping(db, orgId, supplier.Id, "ACME-WIDGET-A", "SMP-WIDGET-A-10");
        AddDeliveryConfig(db, orgId, supplier.Id);
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.HasSupplier.Should().BeFalse();
        dto.HasUpload.Should().BeFalse();
        dto.HasResolvedMapping.Should().BeFalse();
        dto.HasDelivery.Should().BeFalse();
        dto.HasCatalog.Should().BeFalse();
        dto.HasItemMappings.Should().BeFalse();
        dto.HasDeliveryConfig.Should().BeFalse();
        dto.HasTestFired.Should().BeFalse();
        dto.SupplierCount.Should().Be(0);
        dto.OrderCount.Should().Be(0);
        dto.DeliveredCount.Should().Be(0);
        dto.FirstSupplierId.Should().BeNull();
        dto.FirstActionableOrderId.Should().BeNull();

        // The one place the sample IS surfaced: the resume pointer.
        dto.HasSampleOrder.Should().BeTrue();
        dto.SampleOrderId.Should().Be(order.Id);
    }

    [Fact]
    public async Task EmptyOrg_ReportsAllFalse_AndNoSamplePointer()
    {
        var (ctrl, _, _) = Build();

        var dto = await GetStatusAsync(ctrl);

        dto.HasSupplier.Should().BeFalse();
        dto.HasSampleOrder.Should().BeFalse();
        dto.SampleOrderId.Should().BeNull();
        dto.SupplierCount.Should().Be(0);
        dto.OrderCount.Should().Be(0);
    }

    // ── every flag true for a fully set-up real org ──────────────────────────

    [Fact]
    public async Task FullyConfiguredRealOrg_ReportsEverythingTrue_WithCounts()
    {
        var (ctrl, orgId, db) = Build();

        var supplier = AddSupplier(db, orgId);
        var pending  = AddOrder(db, orgId, supplier.Id, "pending_review");
        var done     = AddOrder(db, orgId, supplier.Id, "delivered");
        AddResolvedLine(db, done.Id);
        AddCatalogRow(db, orgId, supplier.Id, "SUP-001");
        AddItemMapping(db, orgId, supplier.Id, "BUY-001", "SUP-001");
        AddDeliveryConfig(db, orgId, supplier.Id);
        AddTestFire(db, orgId, status: "success");
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.HasSupplier.Should().BeTrue();
        dto.HasUpload.Should().BeTrue();
        dto.HasResolvedMapping.Should().BeTrue();
        dto.HasDelivery.Should().BeTrue();
        dto.HasCatalog.Should().BeTrue();
        dto.HasItemMappings.Should().BeTrue();
        dto.HasDeliveryConfig.Should().BeTrue();
        dto.HasTestFired.Should().BeTrue();
        dto.FirstSupplierId.Should().Be(supplier.Id);
        dto.FirstActionableOrderId.Should().Be(pending.Id);
        dto.SupplierCount.Should().Be(1);
        dto.OrderCount.Should().Be(2);
        dto.DeliveredCount.Should().Be(1);
        dto.HasSampleOrder.Should().BeFalse();
        dto.SampleOrderId.Should().BeNull();
    }

    // ── supplier-side exclusions ─────────────────────────────────────────────

    [Fact]
    public async Task SoftDeletedSupplier_DoesNotCount_AndItsConfigDoesNotCertify()
    {
        var (ctrl, orgId, db) = Build();

        var deleted = AddSupplier(db, orgId, deletedAt: DateTime.UtcNow);
        AddCatalogRow(db, orgId, deleted.Id, "SUP-GONE");
        AddItemMapping(db, orgId, deleted.Id, "BUY-GONE", "SUP-GONE");
        AddDeliveryConfig(db, orgId, deleted.Id);
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.HasSupplier.Should().BeFalse();
        dto.SupplierCount.Should().Be(0);
        dto.FirstSupplierId.Should().BeNull();
        dto.HasCatalog.Should().BeFalse();
        dto.HasItemMappings.Should().BeFalse();
        dto.HasDeliveryConfig.Should().BeFalse();
    }

    [Fact]
    public async Task FirstSupplierId_IsOldestNonSampleSupplier()
    {
        var (ctrl, orgId, db) = Build();

        var sampleOlder = AddSupplier(db, orgId, isSample: true, code: "__sample__",
            createdAt: DateTime.UtcNow.AddDays(-10));
        var oldest = AddSupplier(db, orgId, createdAt: DateTime.UtcNow.AddDays(-5));
        var newer  = AddSupplier(db, orgId, createdAt: DateTime.UtcNow.AddDays(-1));
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.FirstSupplierId.Should().Be(oldest.Id);
        dto.SupplierCount.Should().Be(2); // sample excluded
        _ = (sampleOlder, newer);
    }

    // ── hasTestFired: the null-OrderId "success" convention ──────────────────

    [Fact]
    public async Task HasTestFired_TrueFromNullOrderIdSuccessAttempt()
    {
        var (ctrl, orgId, db) = Build();
        AddTestFire(db, orgId, status: "success");
        await db.SaveChangesAsync();

        (await GetStatusAsync(ctrl)).HasTestFired.Should().BeTrue();
    }

    [Fact]
    public async Task HasTestFired_FalseForFailedTestFire_AndForOrderAttachedSuccess()
    {
        var (ctrl, orgId, db) = Build();

        var supplier = AddSupplier(db, orgId);
        var order    = AddOrder(db, orgId, supplier.Id, "delivered");

        // A failed test-fire — not a certification.
        AddTestFire(db, orgId, status: "failed");
        // A SUCCESSFUL but order-attached attempt — that's a real delivery, not a test-fire.
        AddTestFire(db, orgId, status: "success", orderId: order.Id);
        await db.SaveChangesAsync();

        (await GetStatusAsync(ctrl)).HasTestFired.Should().BeFalse();
    }

    // ── order-side exclusions + actionable ordering ──────────────────────────

    [Fact]
    public async Task OrderFlags_ExcludeSampleOrders()
    {
        var (ctrl, orgId, db) = Build();

        var supplier = AddSupplier(db, orgId, isSample: true, code: "__sample__");
        var sample   = AddOrder(db, orgId, supplier.Id, "delivered", isSample: true);
        AddResolvedLine(db, sample.Id);
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.HasUpload.Should().BeFalse();
        dto.HasDelivery.Should().BeFalse();
        dto.HasResolvedMapping.Should().BeFalse();
        dto.OrderCount.Should().Be(0);
        dto.DeliveredCount.Should().Be(0);
    }

    [Fact]
    public async Task FirstActionableOrderId_PicksOldestActionable_SkippingSamplesTransientsAndTerminals()
    {
        var (ctrl, orgId, db) = Build();
        var supplier   = AddSupplier(db, orgId);
        var sampleSupp = AddSupplier(db, orgId, isSample: true, code: "__sample__");
        var t          = DateTime.UtcNow;

        AddOrder(db, orgId, sampleSupp.Id, "pending_review", isSample: true, createdAt: t.AddDays(-9)); // sample — skipped
        AddOrder(db, orgId, supplier.Id, "delivered",      createdAt: t.AddDays(-8)); // terminal success — skipped
        AddOrder(db, orgId, supplier.Id, "parsing",        createdAt: t.AddDays(-7)); // transient — skipped
        AddOrder(db, orgId, supplier.Id, "failed",         createdAt: t.AddDays(-6)); // dead-letter — skipped
        var expected = AddOrder(db, orgId, supplier.Id, "pending_review", createdAt: t.AddDays(-5));
        AddOrder(db, orgId, supplier.Id, "ready",            createdAt: t.AddDays(-4)); // actionable but newer
        AddOrder(db, orgId, supplier.Id, "delivery_failed",  createdAt: t.AddDays(-3)); // actionable but newer
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.FirstActionableOrderId.Should().Be(expected.Id);
    }

    [Fact]
    public async Task SampleOrderId_PointsAtMostRecentSampleRun()
    {
        var (ctrl, orgId, db) = Build();
        var supplier = AddSupplier(db, orgId, isSample: true, code: "__sample__");

        AddOrder(db, orgId, supplier.Id, "delivered", isSample: true,
            createdAt: DateTime.UtcNow.AddDays(-2));
        var latest = AddOrder(db, orgId, supplier.Id, "pending_review", isSample: true,
            createdAt: DateTime.UtcNow.AddDays(-1));
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.HasSampleOrder.Should().BeTrue();
        dto.SampleOrderId.Should().Be(latest.Id);
    }

    // ── tenancy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoOrgs_AreFullyIsolated()
    {
        var (ctrl, orgId, db) = Build();

        // A second org with EVERYTHING configured must not leak into the first.
        var otherOrg      = Guid.NewGuid();
        var otherSupplier = AddSupplier(db, otherOrg);
        var otherOrder    = AddOrder(db, otherOrg, otherSupplier.Id, "delivered");
        AddResolvedLine(db, otherOrder.Id);
        AddCatalogRow(db, otherOrg, otherSupplier.Id, "OTHER-001");
        AddItemMapping(db, otherOrg, otherSupplier.Id, "B-1", "OTHER-001");
        AddDeliveryConfig(db, otherOrg, otherSupplier.Id);
        AddTestFire(db, otherOrg, status: "success");

        // First org has just one bare supplier.
        var mine = AddSupplier(db, orgId);
        await db.SaveChangesAsync();

        var dto = await GetStatusAsync(ctrl);

        dto.HasSupplier.Should().BeTrue();
        dto.FirstSupplierId.Should().Be(mine.Id);
        dto.SupplierCount.Should().Be(1);
        dto.HasUpload.Should().BeFalse();
        dto.HasResolvedMapping.Should().BeFalse();
        dto.HasDelivery.Should().BeFalse();
        dto.HasCatalog.Should().BeFalse();
        dto.HasItemMappings.Should().BeFalse();
        dto.HasDeliveryConfig.Should().BeFalse();
        dto.HasTestFired.Should().BeFalse();
        dto.OrderCount.Should().Be(0);
        dto.FirstActionableOrderId.Should().BeNull();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Supplier AddSupplier(
        ProcuLinkDbContext db, Guid orgId,
        bool isSample = false, string? code = null,
        DateTime? deletedAt = null, DateTime? createdAt = null)
    {
        var s = new Supplier
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = $"Supplier {Guid.NewGuid():N}",
            Code      = code,
            IsSample  = isSample,
            DeletedAt = deletedAt,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        db.Suppliers.Add(s);
        return s;
    }

    private static PurchaseOrderEntity AddOrder(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string status,
        bool isSample = false, DateTime? createdAt = null)
    {
        var o = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = $"PO-{Guid.NewGuid():N}",
            Status     = status,
            IsSample   = isSample,
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            CreatedAt  = createdAt ?? DateTime.UtcNow,
            UpdatedAt  = createdAt ?? DateTime.UtcNow,
        };
        db.PurchaseOrders.Add(o);
        return o;
    }

    private static void AddResolvedLine(ProcuLinkDbContext db, Guid orderId) =>
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id               = Guid.NewGuid(),
            OrderId          = orderId,
            LineNumber       = 1,
            BuyerItemCode    = "BUY-001",
            SupplierItemCode = "SUP-001",
            Quantity         = 1,
            UnitPrice        = 1m,
        });

    private static void AddCatalogRow(ProcuLinkDbContext db, Guid orgId, Guid supplierId, string code) =>
        db.SupplierProducts.Add(new SupplierProduct
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            Code       = code,
            Name       = "Catalog item",
            IsActive   = true,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });

    private static void AddItemMapping(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string buyerCode, string supplierCode) =>
        db.ItemMappings.Add(new ItemMapping
        {
            Id               = Guid.NewGuid(),
            OrgId            = orgId,
            SupplierId       = supplierId,
            BuyerItemCode    = buyerCode,
            SupplierItemCode = supplierCode,
            Confidence       = 1.0f,
            Source           = "manual",
            CreatedAt        = DateTime.UtcNow,
            UpdatedAt        = DateTime.UtcNow,
        });

    private static void AddDeliveryConfig(ProcuLinkDbContext db, Guid orgId, Guid supplierId) =>
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id                   = Guid.NewGuid(),
            OrgId                = orgId,
            SupplierId           = supplierId,
            Protocol             = "http",
            ConfigJson           = """{"url":"https://supplier.example/orders"}""",
            EncryptedCredentials = string.Empty,
            CreatedAt            = DateTime.UtcNow,
            UpdatedAt            = DateTime.UtcNow,
        });

    /// <summary>
    /// Mirrors the row DeliveryService.TestFireAsync writes: OrderId null,
    /// AttemptNumber 0, Status "success"|"failed". Pass <paramref name="orderId"/>
    /// to simulate a real order-attached delivery attempt instead.
    /// </summary>
    private static void AddTestFire(
        ProcuLinkDbContext db, Guid orgId, string status, Guid? orderId = null) =>
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            Channel       = "http",
            Destination   = "https://supplier.example/orders",
            Status        = status,
            AttemptNumber = orderId is null ? 0 : 1,
            AttemptedAt   = DateTime.UtcNow,
        });
}
