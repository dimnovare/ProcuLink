using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Pure unit tests for the mandatory, supplier-independent order invariants. These run for EVERY
/// order regardless of acceptance profile, so a genuinely-invalid order can never surface as green.
/// </summary>
public class InvariantValidatorTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private static PurchaseOrderEntity Order(
        string poNumber = "PO-1", string currency = "EUR",
        params (decimal qty, decimal price)[] lines)
    {
        var orderId = Guid.NewGuid();
        return new PurchaseOrderEntity
        {
            Id = orderId, OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = poNumber, Currency = currency,
            Lines = lines.Select((l, i) => new PurchaseOrderLineEntity
            {
                Id = Guid.NewGuid(), OrderId = orderId, LineNumber = i + 1,
                Quantity = l.qty, UnitPrice = l.price,
            }).ToList(),
        };
    }

    private static IReadOnlyList<OrderValidationResult> Run(PurchaseOrderEntity o) =>
        InvariantValidator.Evaluate(o.OrgId, o.Id, o, Now);

    [Fact]
    public void ValidOrder_AllInvariantsPass()
    {
        var r = Run(Order(lines: new[] { (2m, 10m), (1m, 5.50m) }));
        Assert.NotEmpty(r);
        Assert.All(r, x => Assert.Equal("pass", x.Status));
        Assert.All(r, x => Assert.True(InvariantValidator.IsInvariantCode(x.Code)));
        Assert.All(r, x => Assert.Null(x.ProfileId));   // invariants belong to no profile
        Assert.All(r, x => Assert.Null(x.RuleId));
    }

    [Fact]
    public void NegativeQuantity_FailsAsError()
    {
        var r = Run(Order(lines: new[] { (-3m, 10m) }));
        var qty = Assert.Single(r, x => x.Code == "invariant.quantity_positive");
        Assert.Equal("fail", qty.Status);
        Assert.Equal("error", qty.Severity);
        Assert.Equal(1, qty.LineNumber);
    }

    [Fact]
    public void ZeroQuantity_FailsAsError()
    {
        var r = Run(Order(lines: new[] { (0m, 10m) }));
        var qty = Assert.Single(r, x => x.Code == "invariant.quantity_positive");
        Assert.Equal("fail", qty.Status);
        Assert.Equal("error", qty.Severity);
    }

    [Fact]
    public void NegativeUnitPrice_FailsAsError()
    {
        var r = Run(Order(lines: new[] { (1m, -10m) }));
        var price = Assert.Single(r, x => x.Code == "invariant.unit_price_valid");
        Assert.Equal("fail", price.Status);
        Assert.Equal("error", price.Severity);
    }

    [Fact]
    public void ZeroUnitPrice_FailsAsWarning_NotError()
    {
        // A €0 line is suspicious (often a non-numeric source price parsed to 0) but legitimate for a
        // free line — flag as a warning, never silently accept, never hard-error.
        var r = Run(Order(lines: new[] { (1m, 0m) }));
        var price = Assert.Single(r, x => x.Code == "invariant.unit_price_valid");
        Assert.Equal("fail", price.Status);
        Assert.Equal("warning", price.Severity);
    }

    [Fact]
    public void MissingCurrency_FailsAsError()
    {
        var r = Run(Order(currency: "", lines: new[] { (1m, 10m) }));
        var cur = Assert.Single(r, x => x.Code == "invariant.currency_present");
        Assert.Equal("fail", cur.Status);
        Assert.Equal("error", cur.Severity);
    }

    [Fact]
    public void MissingPoNumber_FailsAsError()
    {
        var r = Run(Order(poNumber: "  ", lines: new[] { (1m, 10m) }));
        var po = Assert.Single(r, x => x.Code == "invariant.po_number_present");
        Assert.Equal("fail", po.Status);
        Assert.Equal("error", po.Severity);
    }

    [Fact]
    public void OrderWithNoLines_StillRunsOrderInvariants()
    {
        var r = Run(Order());
        Assert.Equal(2, r.Count);   // po_number_present + currency_present, no line rows
        Assert.Contains(r, x => x.Code == "invariant.po_number_present");
        Assert.Contains(r, x => x.Code == "invariant.currency_present");
    }
}
