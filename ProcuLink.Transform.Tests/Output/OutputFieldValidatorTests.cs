using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Direct unit coverage for the shared output-format validation helpers
/// (<c>OutputFieldValidator</c> + <c>X12Sanitizer</c>) so the per-format transform
/// tests can rely on a proven core. <c>OutputFieldValidator</c> is exercised through
/// its public <c>ValidateEntity</c> / <c>CollectEntityProblems</c> entry points and
/// the entity-based <c>ITransformService</c> transforms that call it.
/// </summary>
public class OutputFieldValidatorTests
{
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

    // ── Format-aware required item code ─────────────────────────────────────────

    [Fact]
    public void Edifact_RequiresItemCode_Ubl_DoesNot()
    {
        // A resolved line with a BLANK buyer item code (supplier code present, so the
        // review / supplier-code guards pass) — only the format-mandatory buyer-code
        // rule should differ between formats.
        PurchaseOrderEntity BlankBuyerCode() => Entity(new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "", SupplierItemCode = "S-1",
            Description = "X", Quantity = 1m, Unit = "EA", UnitPrice = 5m,
            NeedsReview = false, Confidence = 1.0f,
        });

        // EDIFACT (mandatory LIN item number) flags the blank buyer code…
        OutputFieldValidator.CollectEntityProblems(BlankBuyerCode(), OutputFormat.EdifactOrders)
            .Should().Contain(p => p.Kind == LineProblemKind.MissingItemCode);

        // …UBL does not treat an empty line code as a hard structural failure (the
        // code rides an optional element).
        OutputFieldValidator.CollectEntityProblems(BlankBuyerCode(), OutputFormat.Ubl)
            .Should().NotContain(p => p.Kind == LineProblemKind.MissingItemCode);
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

    // ── Zero / negative quantity guard (parallel to the price check) ─────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void CollectEntityProblems_NonPositiveQuantity_FlaggedForReview(int quantity)
    {
        var order = Entity(new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "S-1",
            Description = "Widget", Quantity = quantity, Unit = "EA", UnitPrice = 5m,
            NeedsReview = false, Confidence = 1.0f,
        });

        var problems = OutputFieldValidator.CollectEntityProblems(order, OutputFormat.Csv);

        problems.Should().ContainSingle(p => p.Kind == LineProblemKind.MissingOrZeroQuantity)
            .Which.LineNumber.Should().Be(1);
    }

    [Fact]
    public void CollectEntityProblems_PositiveQuantity_NoQuantityProblem()
    {
        var order = Entity(new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "S-1",
            Description = "Widget", Quantity = 2m, Unit = "EA", UnitPrice = 5m,
            NeedsReview = false, Confidence = 1.0f,
        });

        var problems = OutputFieldValidator.CollectEntityProblems(order, OutputFormat.Csv);

        problems.Should().NotContain(p => p.Kind == LineProblemKind.MissingOrZeroQuantity);
    }

    [Fact]
    public void ValidateEntity_ZeroQuantity_ThrowsAndHoldsLine()
    {
        var order = Entity(new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "S-1",
            Description = "Widget", Quantity = 0m, Unit = "EA", UnitPrice = 5m,
            NeedsReview = false, Confidence = 1.0f,
        });

        var act = () => OutputFieldValidator.ValidateEntity(order, OutputFormat.Csv);

        act.Should().Throw<TransformValidationException>()
            .Which.UnresolvedLineNumbers.Should().Contain(1);
    }
}
