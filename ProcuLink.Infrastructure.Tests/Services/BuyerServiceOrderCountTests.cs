using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Verifies that <see cref="BuyerService.ListAsync"/> returns a real
/// <c>OrderCount</c> (and <c>LastOrderAge</c>) derived from the denormalised
/// <see cref="PurchaseOrderEntity.BuyerName"/> column rather than a hard-coded 0.
///
/// Correlation key: <c>Buyer.Name == PurchaseOrderEntity.BuyerName</c> (case-insensitive).
/// The column is populated at parse time (<c>OrderIngestionService</c>) and indexed
/// via <c>IX_purchase_orders_org_id_buyer_name</c>, so the rollup aggregates in SQL
/// without loading CanonicalJson blobs.
/// </summary>
public class BuyerServiceOrderCountTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PurchaseOrderEntity MakeOrder(Guid orgId, Guid supplierId, string? buyerName) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = $"PO-{Guid.NewGuid():N}",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            // Mirror the ingestion write path: the denormalised buyer_name column.
            BuyerName  = buyerName,
        };

    // ── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsNonZeroOrderCount_WhenOrdersExistForBuyer()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // Seed one buyer record and two parsed orders whose buyer_name column names that buyer.
        db.Buyers.Add(new Buyer
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = "Acme Corp",
            Code      = "ACM",
            CreatedAt = DateTime.UtcNow,
        });

        db.PurchaseOrders.Add(MakeOrder(orgId, supplierId, buyerName: "Acme Corp"));
        db.PurchaseOrders.Add(MakeOrder(orgId, supplierId, buyerName: "Acme Corp"));
        await db.SaveChangesAsync();

        var svc = new BuyerService(db);
        var results = await svc.ListAsync(orgId, default);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Acme Corp");
        results[0].OrderCount.Should().Be(2, "two orders reference this buyer by name");
        results[0].LastOrderAge.Should().NotBeNull("at least one order exists so age is derivable");
    }

    [Fact]
    public async Task ListAsync_ExcludesOrdersFromOtherOrgs()
    {
        await using var db = NewDb();
        var orgA       = Guid.NewGuid();
        var orgB       = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // Buyer in orgA.
        db.Buyers.Add(new Buyer
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgA,
            Name      = "Nordic AS",
            Code      = "NOR",
            CreatedAt = DateTime.UtcNow,
        });

        // One order for orgA (should count) and one for orgB (must not count).
        db.PurchaseOrders.Add(MakeOrder(orgA, supplierId, buyerName: "Nordic AS"));
        db.PurchaseOrders.Add(MakeOrder(orgB, supplierId, buyerName: "Nordic AS"));
        await db.SaveChangesAsync();

        var svc = new BuyerService(db);
        var results = await svc.ListAsync(orgA, default);

        results.Should().HaveCount(1);
        results[0].OrderCount.Should().Be(1, "only the order belonging to orgA should be counted");
    }

    [Fact]
    public async Task ListAsync_ReturnsZero_WhenNoParsedOrdersForBuyer()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();

        db.Buyers.Add(new Buyer
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = "Empty Buyer Ltd",
            Code      = "EMP",
            CreatedAt = DateTime.UtcNow,
        });
        // No orders seeded.
        await db.SaveChangesAsync();

        var svc = new BuyerService(db);
        var results = await svc.ListAsync(orgId, default);

        results.Should().HaveCount(1);
        results[0].OrderCount.Should().Be(0);
        results[0].LastOrderAge.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_IgnoresOrdersWithNullOrMissingBuyerName()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Buyers.Add(new Buyer
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = "Visible Buyer",
            Code      = "VIS",
            CreatedAt = DateTime.UtcNow,
        });

        // Order with no buyer name (still in pending_parse state).
        db.PurchaseOrders.Add(MakeOrder(orgId, supplierId, buyerName: null));
        // Order where buyer name is a different buyer — should not count for "Visible Buyer".
        db.PurchaseOrders.Add(MakeOrder(orgId, supplierId, buyerName: "Other Buyer"));
        await db.SaveChangesAsync();

        var svc = new BuyerService(db);
        var results = await svc.ListAsync(orgId, default);

        results.Should().HaveCount(1);
        results[0].OrderCount.Should().Be(0, "neither order names this buyer");
    }

    [Fact]
    public async Task ListAsync_MatchesBuyerNameCaseInsensitively()
    {
        // Buyer↔order name matching has always been OrdinalIgnoreCase; orders whose
        // buyer_name differs from Buyer.Name only by case must still be counted and
        // merged into a single rollup.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Buyers.Add(new Buyer
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = "Legacy Buyer",
            Code      = "LEG",
            CreatedAt = DateTime.UtcNow,
        });

        db.PurchaseOrders.Add(MakeOrder(orgId, supplierId, buyerName: "LEGACY BUYER"));
        db.PurchaseOrders.Add(MakeOrder(orgId, supplierId, buyerName: "legacy buyer"));
        await db.SaveChangesAsync();

        var svc = new BuyerService(db);
        var results = await svc.ListAsync(orgId, default);

        results[0].OrderCount.Should().Be(2, "buyer name matching is case-insensitive");
    }
}
