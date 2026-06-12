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

public class DashboardControllerTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (DashboardController Ctrl, Guid OrgId, ProcuLinkDbContext Db)
        Build(ProcuLinkDbContext? db = null)
    {
        db ??= MakeDb();
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return (new DashboardController(db, tenant.Object), orgId, db);
    }

    // ── GET /api/orders/summary ───────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_ReturnsByStatusCountsForOrg()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();

        db.PurchaseOrders.AddRange(
            MakeOrder(orgId, supplierId, "pending_review"),
            MakeOrder(orgId, supplierId, "pending_review"),
            MakeOrder(orgId, supplierId, "delivered"),
            MakeOrder(Guid.NewGuid(), supplierId, "pending_review") // different org — must not appear
        );
        await db.SaveChangesAsync();

        var result = await ctrl.GetSummary(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<OrdersSummaryDto>().Subject;

        dto.Total.Should().Be(3);
        dto.ByStatus["pending_review"].Should().Be(2);
        dto.ByStatus["delivered"].Should().Be(1);
        dto.ByStatus.Should().NotContainKey("delivery_failed"); // absent statuses omitted
    }

    [Fact]
    public async Task GetSummary_EmptyOrg_ReturnsZeroTotal()
    {
        var (ctrl, _, _) = Build();
        var result = await ctrl.GetSummary(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<OrdersSummaryDto>().Subject;
        dto.Total.Should().Be(0);
        dto.ByStatus.Should().BeEmpty();
    }

    // ── GET /api/dashboard/topology ──────────────────────────────────────────

    [Fact]
    public async Task GetTopology_ReturnsAggregatedBuyersAndSuppliers()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Acme Supplies",
            CreatedAt = DateTime.UtcNow,
        });
        db.PurchaseOrders.AddRange(
            MakeOrderWithBuyer(orgId, supplierId, "delivered",       "Buyer Corp"),
            MakeOrderWithBuyer(orgId, supplierId, "delivered",       "Buyer Corp"),
            MakeOrderWithBuyer(orgId, supplierId, "delivery_failed", "Buyer Corp")
        );
        await db.SaveChangesAsync();

        var result = await ctrl.GetTopology(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<DashboardTopologyDto>().Subject;

        dto.Buyers.Should().HaveCount(1);
        dto.Buyers[0].Name.Should().Be("Buyer Corp");

        dto.Suppliers.Should().HaveCount(1);
        dto.Suppliers[0].Name.Should().Be("Acme Supplies");
        dto.Suppliers[0].Health.Should().Be(67); // 2/3 not-failed = 66.6 → round = 67

        dto.Wires.Should().HaveCount(1);
        dto.Wires[0].Health.Should().Be("down"); // has failed orders
        dto.Wires[0].Alert.Should().Be(1); // 1 delivery_failed (exception)
    }

    [Fact]
    public async Task GetTopology_SupplierHealth_ExcludesOrdersOlderThan30Days()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Acme Supplies",
            CreatedAt = DateTime.UtcNow,
        });

        // In-window: 2 delivered, 0 failed → health should be 100% over the window.
        var inWindowA = MakeOrderWithBuyer(orgId, supplierId, "delivered", "Buyer Corp");
        var inWindowB = MakeOrderWithBuyer(orgId, supplierId, "delivered", "Buyer Corp");

        // Out-of-window (40 days old): a failure that must NOT drag the 30-day figure down.
        var stale = MakeOrderWithBuyer(orgId, supplierId, "delivery_failed", "Buyer Corp");
        stale.CreatedAt = DateTime.UtcNow.AddDays(-40);

        db.PurchaseOrders.AddRange(inWindowA, inWindowB, stale);
        await db.SaveChangesAsync();

        var result = await ctrl.GetTopology(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<DashboardTopologyDto>().Subject;

        dto.Suppliers.Should().HaveCount(1);
        // If the stale failure were counted, health would be 2/3 → 67. Excluding it
        // (only the 2 in-window delivered orders count) gives 2/2 → 100.
        dto.Suppliers[0].Health.Should().Be(100);
    }

    [Fact]
    public async Task GetTopology_MixedColumnAndLegacyJsonRows_ProducesSameTotalsAsJsonOnlyDerivation()
    {
        // Equivalence guard for the column-first rewrite: seed both current-shaped
        // rows (buyer_name column populated — written by all current ingest paths)
        // AND legacy-shaped rows (null column, buyer name only in CanonicalJson).
        // The old JSON-only per-row loop would have produced one buyer with 3
        // orders, supplier health 67 and one "down" wire with alert 1 — the
        // column-first + capped-fallback implementation must match exactly.
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Acme Supplies",
            CreatedAt = DateTime.UtcNow,
        });
        db.PurchaseOrders.AddRange(
            // Current-shaped rows: column + JSON.
            MakeOrderWithBuyerColumn(orgId, supplierId, "delivered",       "Buyer Corp"),
            MakeOrderWithBuyerColumn(orgId, supplierId, "delivered",       "Buyer Corp"),
            // Legacy-shaped row: JSON only, null column — must still count via the fallback.
            MakeOrderWithBuyer(orgId, supplierId, "delivery_failed", "Buyer Corp")
        );
        await db.SaveChangesAsync();

        var result = await ctrl.GetTopology(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<DashboardTopologyDto>().Subject;

        dto.Buyers.Should().HaveCount(1, "column rows and legacy JSON rows name the same buyer");
        dto.Buyers[0].Name.Should().Be("Buyer Corp");
        dto.Buyers[0].Volume.Should().Be("3 ord", "all three orders must be counted regardless of where the buyer name lives");

        dto.Suppliers.Should().HaveCount(1);
        dto.Suppliers[0].Health.Should().Be(67); // 2/3 not-failed = 66.6 → round = 67

        dto.Wires.Should().HaveCount(1);
        dto.Wires[0].Health.Should().Be("down"); // legacy row carries the failure
        dto.Wires[0].Alert.Should().Be(1);       // 1 delivery_failed (exception) from the legacy row
    }

    [Fact]
    public async Task GetTopology_CrossOrg_Excluded()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "My Supplier",
            CreatedAt = DateTime.UtcNow,
        });
        // Order from a different org
        db.PurchaseOrders.Add(
            MakeOrderWithBuyer(Guid.NewGuid(), supplierId, "delivered", "Other Buyer"));
        await db.SaveChangesAsync();

        var result = await ctrl.GetTopology(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<DashboardTopologyDto>().Subject;

        dto.Buyers.Should().BeEmpty();
        dto.Wires.Should().BeEmpty();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static PurchaseOrderEntity MakeOrder(Guid orgId, Guid supplierId, string status) =>
        new()
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            PoNumber = $"PO-{Guid.NewGuid():N}", Status = status,
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };

    /// <summary>
    /// Legacy-shaped row: buyer name ONLY in CanonicalJson, buyer_name column null —
    /// the shape rows created before the Wave2BuyerNameColumn migration have.
    /// </summary>
    private static PurchaseOrderEntity MakeOrderWithBuyer(
        Guid orgId, Guid supplierId, string status, string buyerName)
    {
        var o = MakeOrder(orgId, supplierId, status);
        o.CanonicalJson = System.Text.Json.JsonDocument.Parse(
            $"{{\"buyerName\":\"{buyerName}\"}}");
        return o;
    }

    /// <summary>
    /// Current-shaped row: buyer name in BOTH the denormalized column and
    /// CanonicalJson — the shape every current ingest path writes.
    /// </summary>
    private static PurchaseOrderEntity MakeOrderWithBuyerColumn(
        Guid orgId, Guid supplierId, string status, string buyerName)
    {
        var o = MakeOrderWithBuyer(orgId, supplierId, status, buyerName);
        o.BuyerName = buyerName;
        return o;
    }
}
