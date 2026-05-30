using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

public class ParsedOrderTransformFactoryTests
{
    private static ParsedOrderTransformFactory BuildFactory() =>
        new(new IParsedOrderTransform[]
        {
            new UblParsedOrderTransform(),
            new X12ParsedOrderTransform(),
            new EdifactParsedOrderTransform(),
        });

    private static ParsedOrder SampleOrder() =>
        new(
            PoNumber:  "PO-FACTORY-1",
            OrderDate: new DateTime(2026, 5, 30),
            BuyerName: "Acme",
            Currency:  "EUR",
            Lines: new List<ParsedOrderLine>
            {
                new(LineNumber: 1, BuyerItemCode: "X-1", Description: "Item", Quantity: 1m, Unit: "EA", UnitPrice: 1m),
            });

    [Theory]
    [InlineData(OutputFormat.UblOrder,     typeof(UblParsedOrderTransform))]
    [InlineData(OutputFormat.X12_850,      typeof(X12ParsedOrderTransform))]
    [InlineData(OutputFormat.EdifactOrders, typeof(EdifactParsedOrderTransform))]
    public void GetTransform_SelectsTheRegisteredImplementation(OutputFormat format, Type expected)
    {
        var factory = BuildFactory();

        factory.GetTransform(format).Should().BeOfType(expected);
    }

    [Theory]
    [InlineData(OutputFormat.UblOrder,      "application/xml",      ".xml")]
    [InlineData(OutputFormat.X12_850,       "application/edi-x12",  ".x12")]
    [InlineData(OutputFormat.EdifactOrders, "application/edifact",  ".edi")]
    public void Transform_ProducesNonEmptyContentWithExpectedMetadata(
        OutputFormat format, string expectedContentType, string expectedExtension)
    {
        var factory = BuildFactory();

        var result = factory.Transform(SampleOrder(), format);

        result.Content.Should().NotBeEmpty();
        result.ContentType.Should().Be(expectedContentType);
        result.FileExtension.Should().Be(expectedExtension);
    }

    [Fact]
    public void GetTransform_Throws_ForFormatWithoutParsedOrderTransform()
    {
        var factory = BuildFactory();

        // Csv has no IParsedOrderTransform implementation (it is entity-only).
        var act = () => factory.GetTransform(OutputFormat.Csv);

        act.Should().Throw<NotSupportedException>()
           .WithMessage("*Csv*");
    }
}
