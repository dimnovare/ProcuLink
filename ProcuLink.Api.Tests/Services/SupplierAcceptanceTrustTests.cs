using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Trust-layer guards: validation must never report a clean pass on a genuinely-invalid order, and
/// an unknown acceptance-rule operator must fail closed rather than silently pass.
/// </summary>
public class SupplierAcceptanceTrustTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task ValidateOrder_NegativeQuantity_NoProfile_StillFailsInvariant()
    {
        // The headline trust bug: qty -3, no acceptance profile → previously empty results → vacuous
        // green. Now the mandatory invariant fails, so the order can never report a clean pass.
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid(); var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-9",
            Status = "ready", Currency = "EUR", OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1, Quantity = -3, UnitPrice = 10 },
            },
        });
        await db.SaveChangesAsync();

        var results = await svc.ValidateOrderAsync(orgId, orderId, CancellationToken.None);

        Assert.NotNull(results);
        var qtyFail = Assert.Single(results!, r => r.Code == "invariant.quantity_positive");
        Assert.Equal("fail", qtyFail.Status);
        Assert.False(results!.All(r => r.Status == "pass")); // not a clean pass
    }

    [Fact]
    public static void EvaluateProfile_UnknownOperator_FailsClosed()
    {
        var orgId = Guid.NewGuid(); var orderId = Guid.NewGuid();
        var profile = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = Guid.NewGuid(), VersionNo = 1, Status = "active",
            Rules = new List<SupplierAcceptanceRule>
            {
                new() { Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency",
                        Operator = "frobnicate" /* unknown */, Severity = "error" },
            },
        };
        var order = new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, PoNumber = "PO-1", Currency = "EUR",
            Lines = new List<PurchaseOrderLineEntity>(),
        };

        var rows = SupplierAcceptanceService.EvaluateProfile(orgId, orderId, profile, order, DateTime.UtcNow);

        var row = Assert.Single(rows);
        Assert.Equal("fail", row.Status); // unknown operator → fail closed, not silent pass
    }

    [Fact]
    public static void KnownOperators_ContainsEverySupportedOperator()
    {
        foreach (var op in new[]
        {
            "required", "equals", "in", "min", "max", "not_equals", "contains",
            "greater_than", "less_than", "max_length", "date_sanity", "not_label",
            "vat_format", "line_amount_reconcile",
        })
            Assert.Contains(op, SupplierAcceptanceService.KnownOperators);
    }
}
