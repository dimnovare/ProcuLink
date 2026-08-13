using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
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

    /// <summary>
    /// A supplier that refused every order it was sent scored <b>100%</b> on the figure the
    /// dashboard labels "Delivery success rate, last 30 days", because the controller's private
    /// failure set omitted <c>rejected_by_supplier</c> — the one status that means the supplier
    /// explicitly refused the document.
    /// </summary>
    [Fact]
    public async Task GetTopology_SupplierThatRejectedEveryOrder_ScoresZero_NotOneHundred()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Contoso Supplies",
            CreatedAt = DateTime.UtcNow,
        });
        db.PurchaseOrders.AddRange(
            MakeOrderWithBuyerColumn(orgId, supplierId, OrderStatusConstants.RejectedBySupplier, "Contoso Buying OY"),
            MakeOrderWithBuyerColumn(orgId, supplierId, OrderStatusConstants.RejectedBySupplier, "Contoso Buying OY"));
        await db.SaveChangesAsync();

        var result = await ctrl.GetTopology(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<DashboardTopologyDto>().Subject;

        dto.Suppliers.Should().ContainSingle();
        dto.Suppliers[0].Health.Should().Be(
            0, "every order in the window was refused by this supplier — the omission rendered 100");

        dto.Wires.Should().ContainSingle();
        dto.Wires[0].Health.Should().Be(
            "down", "wire health reads the failure count first; a rejected-only wire used to read amber 'risk'");
    }

    /// <summary>
    /// The generalisation of the test above, walked over the canonical
    /// <see cref="OrderStatusConstants.FailureBucket"/> rather than over a list re-typed here —
    /// a re-typed list is how the defect entered the controller in the first place. Every member
    /// of the bucket must drag supplier health to 0 and the wire to "down".
    /// </summary>
    [Theory]
    [MemberData(nameof(FailureBucketMembers))]
    public async Task GetTopology_EveryCanonicalFailureStatus_CountsAsAFailure(string status)
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Contoso Supplies",
            CreatedAt = DateTime.UtcNow,
        });
        db.PurchaseOrders.Add(MakeOrderWithBuyerColumn(orgId, supplierId, status, "Contoso Buying OY"));
        await db.SaveChangesAsync();

        var result = await ctrl.GetTopology(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<DashboardTopologyDto>().Subject;

        dto.Suppliers.Should().ContainSingle();
        dto.Suppliers[0].Health.Should().Be(0, "'{0}' is in OrderStatusConstants.FailureBucket", status);
        dto.Wires.Should().ContainSingle();
        dto.Wires[0].Health.Should().Be("down", "'{0}' is in OrderStatusConstants.FailureBucket", status);
    }

    /// <summary>The statuses the theory above actually walked — enumerated from the bucket.</summary>
    private static readonly IReadOnlyList<string> WalkedFailureStatuses =
        OrderStatusConstants.FailureBucket.ToList();

    public static TheoryData<string> FailureBucketMembers()
    {
        var data = new TheoryData<string>();
        foreach (var status in WalkedFailureStatuses) data.Add(status);
        return data;
    }

    /// <summary>
    /// Anti-vacuity floor for the theory above. It counts what was actually extracted from the
    /// canonical bucket — not files scanned, not cases declared — so the walk cannot silently
    /// shrink to nothing (or to a subset) and keep reporting green.
    /// </summary>
    [Fact]
    public void FailureBucketWalk_CoversEveryCanonicalFailureStatus_AndIsNotEmpty()
    {
        var walked = WalkedFailureStatuses;

        FailureBucketMembers().Count.Should().Be(
            walked.Count, "every extracted status must become a theory case");
        walked.Should().HaveCountGreaterThanOrEqualTo(
            5, "the canonical failure bucket had five members when this floor was written");
        walked.Should().BeEquivalentTo(
            OrderStatusConstants.FailureBucket,
            "the walk must be the bucket itself, never a list re-typed beside it");
        walked.Should().Contain(
            OrderStatusConstants.RejectedBySupplier,
            "this is the member the controller's hand-written copy dropped");
    }

    /// <summary>
    /// A delivered order is the negative control for both tests above: if the controller counted
    /// every status as a failure they would pass while saying nothing.
    /// </summary>
    [Fact]
    public async Task GetTopology_DeliveredOnlySupplier_ScoresOneHundred()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Contoso Supplies",
            CreatedAt = DateTime.UtcNow,
        });
        db.PurchaseOrders.Add(
            MakeOrderWithBuyerColumn(orgId, supplierId, OrderStatusConstants.Delivered, "Contoso Buying OY"));
        await db.SaveChangesAsync();

        var result = await ctrl.GetTopology(CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto    = ok.Value.Should().BeOfType<DashboardTopologyDto>().Subject;

        dto.Suppliers[0].Health.Should().Be(100);
        dto.Wires[0].Health.Should().Be("ok");
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
