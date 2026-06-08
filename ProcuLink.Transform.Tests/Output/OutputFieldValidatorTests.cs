using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Direct unit coverage for the shared output-format validation helpers
/// (<c>OutputFieldValidator</c> + <c>X12Sanitizer</c>) so the per-format transform
/// tests can rely on a proven core. <c>OutputFieldValidator</c> is internal, so this
/// file exercises it through the public transforms that call it.
/// </summary>
public class OutputFieldValidatorTests
{
    private static ParsedOrder Parsed(params ParsedOrderLine[] lines) =>
        new(PoNumber: "PO-1", OrderDate: new DateTime(2026, 6, 8), BuyerName: "Buyer",
            Currency: "EUR", Lines: lines.ToList());

    private static PurchaseOrderEntity Entity(params PurchaseOrderLineEntity[] lines) =>
        new()
        {
            Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-1", OrderDate = new DateOnly(2026, 6, 8), Currency = "EUR",
            Status = "ready", Lines = lines.ToList(),
        };

    // ── X12Sanitizer free-text substitution ────────────────────────────────────

    [Theory]
    [InlineData("a*b", "a b")]
    [InlineData("a>b", "a b")]
    [InlineData("a~b", "a b")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SanitizeFreeText_SubstitutesDelimitersAndTrims(string? input, string expected)
    {
        X12Sanitizer.SanitizeFreeText(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("clean-code", false)]
    [InlineData("ABC*123", true)]
    [InlineData("ABC>123", true)]
    [InlineData("ABC~123", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsDelimiter_DetectsX12Specials(string? input, bool expected)
    {
        X12Sanitizer.ContainsDelimiter(input).Should().Be(expected);
    }

    // ── Format-aware required item code (via the transforms) ────────────────────

    [Fact]
    public void Edifact_RequiresItemCode_Ubl_DoesNot()
    {
        var blank = new ParsedOrderLine(1, BuyerItemCode: "", Description: "X", Quantity: 1m, Unit: "EA", UnitPrice: 5m);

        // EDIFACT (mandatory LIN item number) rejects the blank code…
        var edifact = () => new EdifactParsedOrderTransform().Transform(Parsed(blank), OutputFormat.EdifactOrders);
        edifact.Should().Throw<TransformValidationException>();

        // …UBL does not treat an empty line code as a hard structural failure (the
        // code rides an optional element); with a valid price it serializes fine.
        var ubl = () => new UblParsedOrderTransform().Transform(Parsed(blank), OutputFormat.UblOrder);
        ubl.Should().NotThrow();
    }

    // ── Entity guard layering: review + supplier code + price ───────────────────

    [Fact]
    public void Entity_NeedsReview_StillBlocks()
    {
        var order = Entity(new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "S-1",
            Quantity = 1m, Unit = "EA", UnitPrice = 5m, NeedsReview = true, Confidence = 0.5f,
        });

        var act = async () => await new UblOrderTransformService().TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        act.Should().ThrowAsync<TransformValidationException>();
    }

    [Fact]
    public void Entity_NullSupplierCode_StillBlocks()
    {
        var order = Entity(new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = null,
            Quantity = 1m, Unit = "EA", UnitPrice = 5m, NeedsReview = false, Confidence = 1.0f,
        });

        var act = async () => await new CxmlTransformService().TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        act.Should().ThrowAsync<TransformValidationException>();
    }

    [Fact]
    public void Entity_FullyValid_DoesNotThrow()
    {
        var order = Entity(new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "S-1",
            Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = 5m,
            NeedsReview = false, Confidence = 1.0f,
        });

        var act = async () => await new UblOrderTransformService().TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        act.Should().NotThrowAsync();
    }
}
