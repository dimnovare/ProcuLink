using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// B1 — the price &gt; 0 / qty &gt; 0 output invariant (<see cref="OutputFieldValidator"/>) now lives
/// inside the FIXED CSV / JSON / XML transforms, where the entity's canonical columns ARE the emitted
/// bytes — matching the X12 / UBL / cXML transforms which already enforced it. A zero/negative price or
/// quantity is HELD (<see cref="TransformValidationException"/>) instead of emitting a $0 / zero-qty
/// line; a fully-valid order emits normally.
///
/// <para>The guard is deliberately NOT applied to the override / whole-document-template / OutputNode
/// paths: those emit via a path that can legitimately transform values or drop lines (IncludeWhen), so
/// guarding the raw entity there would pre-empt those features. (An earlier draft guarded centrally in
/// OrderTransformService and the adversarial review caught exactly that regression — the guard belongs
/// here, in the fixed transforms.)</para>
/// </summary>
public class FixedTransformOutputInvariantTests
{
    private static PurchaseOrderEntity Order(decimal line1Price = 10m, decimal line1Qty = 3m) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-B1",
            BuyerName  = "Buyer",
            OrderDate  = new DateOnly(2026, 4, 2),
            Currency   = "EUR",
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = line1Qty, Unit = "EA", UnitPrice = line1Price, NeedsReview = false,
                },
            },
        };

    private static ITransformService For(OutputFormat format) =>
        new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new XmlTransformService() }
            .Single(t => t.CanTransform(format));

    public static IEnumerable<object[]> FixedFormats() => new[]
    {
        new object[] { OutputFormat.Csv },
        new object[] { OutputFormat.Json },
        new object[] { OutputFormat.Xml },
    };

    [Theory]
    [MemberData(nameof(FixedFormats))]
    public async Task ZeroPriceLine_IsHeld(OutputFormat format)
    {
        var act = async () => await For(format).TransformAsync(Order(line1Price: 0m), format, CancellationToken.None);
        await act.Should().ThrowAsync<TransformValidationException>();
    }

    [Theory]
    [MemberData(nameof(FixedFormats))]
    public async Task NegativeQuantityLine_IsHeld(OutputFormat format)
    {
        var act = async () => await For(format).TransformAsync(Order(line1Qty: -1m), format, CancellationToken.None);
        await act.Should().ThrowAsync<TransformValidationException>();
    }

    [Theory]
    [MemberData(nameof(FixedFormats))]
    public async Task FullyValidOrder_EmitsWithoutThrowing(OutputFormat format)
    {
        var result = await For(format).TransformAsync(Order(), format, CancellationToken.None);
        result.Content.Should().NotBeNull();
    }
}
