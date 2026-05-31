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

    private static PurchaseOrderEntity MakeOrderWithBuyer(
        Guid orgId, Guid supplierId, string status, string buyerName)
    {
        var o = MakeOrder(orgId, supplierId, status);
        o.CanonicalJson = System.Text.Json.JsonDocument.Parse(
            $"{{\"buyerName\":\"{buyerName}\"}}");
        return o;
    }
}
