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

// ════════════════════════════════════════════════════════════════════════════
//  The dashboard must not count the practice order.
//
//  OnboardingController states the invariant for the whole product:
//
//    "Every flag/count EXCLUDES sample data (IsSample suppliers/orders):
//     running the sample order must never 'complete' onboarding with zero
//     real data."
//
//  Seven controllers and services honour it. DashboardController did not — it
//  carried zero IsSample references, so every KPI, the sidebar badge, the
//  notifications bell and the wire topology counted the practice order as real
//  work. A brand-new account that ran the practice flow read "1 delivered".
//
//  This was latent while the practice order could not reach `delivered`. WP-27
//  closes the practice delivery loop, which makes it reachable — so the defect
//  arrives with that packet rather than existing before it.
//
//  Every test here pins ONE query site. Reverting a single `!o.IsSample` must
//  turn exactly the matching test red; that is what makes this file evidence
//  rather than decoration.
// ════════════════════════════════════════════════════════════════════════════

public class DashboardExcludesSampleOrdersTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (DashboardController Ctrl, Guid OrgId, ProcuLinkDbContext Db) Build()
    {
        var db     = MakeDb();
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return (new DashboardController(db, tenant.Object), orgId, db);
    }

    private static PurchaseOrderEntity Order(
        Guid orgId, Guid supplierId, string status, bool isSample, string? buyerName = null)
    {
        var o = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            PoNumber = $"PO-{Guid.NewGuid():N}", Status = status,
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            IsSample = isSample,
        };
        if (buyerName is not null)
        {
            o.BuyerName = buyerName;
            o.CanonicalJson = System.Text.Json.JsonDocument.Parse(
                $"{{\"buyerName\":\"{buyerName}\"}}");
        }
        return o;
    }

    /// <summary>Reads a property off the anonymous object GetStats returns.</summary>
    private static int Stat(object payload, string name) =>
        (int)payload.GetType().GetProperty(name)!.GetValue(payload)!;

    // ── GET /api/dashboard/stats ─────────────────────────────────────────────

    [Fact]
    public async Task GetStats_DoesNotCountTheSampleOrderAsDelivered()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();

        // The exact state a new account reaches by running the practice flow:
        // one delivered sample order and nothing else.
        db.PurchaseOrders.Add(Order(orgId, supplierId, "delivered", isSample: true));
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetStats(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        Stat(ok.Value!, "delivered").Should().Be(0,
            "a brand-new account that ran the practice order has delivered nothing real");
        Stat(ok.Value!, "totalOrders").Should().Be(0);
        Stat(ok.Value!, "totalOrdersThisMonth").Should().Be(0);
    }

    [Fact]
    public async Task GetStats_CountsRealOrdersAlongsideASampleOne()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();

        db.PurchaseOrders.AddRange(
            Order(orgId, supplierId, "delivered",      isSample: true),
            Order(orgId, supplierId, "delivered",      isSample: false),
            Order(orgId, supplierId, "pending_review", isSample: true),
            Order(orgId, supplierId, "pending_review", isSample: false));
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetStats(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        Stat(ok.Value!, "delivered").Should().Be(1);
        Stat(ok.Value!, "pendingReview").Should().Be(1);
        Stat(ok.Value!, "totalOrders").Should().Be(2);
        Stat(ok.Value!, "totalOrdersThisMonth").Should().Be(2);
    }

    // ── GET /api/orders/summary ──────────────────────────────────────────────
    // Feeds the sidebar badge and the notifications bell.

    [Fact]
    public async Task GetSummary_ExcludesSampleOrdersFromEveryStatusBucket()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();

        db.PurchaseOrders.AddRange(
            Order(orgId, supplierId, "delivered",      isSample: true),
            Order(orgId, supplierId, "pending_review", isSample: true),
            Order(orgId, supplierId, "pending_review", isSample: false));
        await db.SaveChangesAsync();

        var ok  = (await ctrl.GetSummary(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<OrdersSummaryDto>().Subject;

        dto.Total.Should().Be(1);
        dto.ByStatus["pending_review"].Should().Be(1);
        dto.ByStatus.Should().NotContainKey("delivered",
            "the only delivered order is the practice one");
    }

    // ── GET /api/dashboard/topology ──────────────────────────────────────────
    // The practice order seeds its own `__sample__` supplier, so leaving it in
    // draws a wire on the landing page for a supplier the user never added.

    [Fact]
    public async Task GetTopology_SupplierHealth_ExcludesSampleOrders()
    {
        var (ctrl, orgId, db) = Build();
        var sampleSupplier = Guid.NewGuid();

        db.PurchaseOrders.Add(
            Order(orgId, sampleSupplier, "delivered", isSample: true, buyerName: "Practice Buyer"));
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetTopology(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        System.Text.Json.JsonSerializer.Serialize(ok.Value)
            .Should().NotContain(sampleSupplier.ToString(),
                "the practice supplier must not appear as a node on the topology");
    }

    [Fact]
    public async Task GetTopology_Wires_ExcludeSampleOrders()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();

        db.PurchaseOrders.Add(
            Order(orgId, supplierId, "delivered", isSample: true, buyerName: "Practice Buyer"));
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetTopology(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        System.Text.Json.JsonSerializer.Serialize(ok.Value)
            .Should().NotContain("Practice Buyer",
                "the practice buyer must not appear as a wire endpoint");
    }

    [Fact]
    public async Task GetTopology_LegacyJsonRows_ExcludeSampleOrders()
    {
        var (ctrl, orgId, db) = Build();
        var supplierId = Guid.NewGuid();

        // Legacy shape: buyer name ONLY in canonical_json, buyer_name column null.
        // A separate query path, so it needs its own guard and its own test.
        var legacy = Order(orgId, supplierId, "delivered", isSample: true);
        legacy.CanonicalJson = System.Text.Json.JsonDocument.Parse(
            "{\"buyerName\":\"Legacy Practice Buyer\"}");
        db.PurchaseOrders.Add(legacy);
        await db.SaveChangesAsync();

        var ok = (await ctrl.GetTopology(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        System.Text.Json.JsonSerializer.Serialize(ok.Value)
            .Should().NotContain("Legacy Practice Buyer");
    }
}
