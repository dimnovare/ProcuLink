using System.Text;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Tests for the per-order override extended to ALL structured output formats
/// (XML / cXML / UBL / X12) via <see cref="EffectiveEntityResolver"/>:
/// a canonical-field override (e.g. a fixedValue on PoNumber) resolves into an effective entity and
/// shows up in the fixed-transform output, while a no-override resolve is byte-for-byte identical to
/// running the fixed transform on the original entity.
/// </summary>
public class EffectiveEntityResolverTests
{
    private static PurchaseOrderEntity BuildOrder()
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-9001",
            BuyerName  = "Acme Buyer Ltd",
            OrderDate  = new DateOnly(2026, 5, 1),
            Currency   = "EUR",
            Status     = "ready",
            Supplier   = new Supplier { Id = Guid.NewGuid(), Name = "Original Supplier OÜ" },
        };

        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new()
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 3m, Unit = "EA", UnitPrice = 10m,
                NeedsReview = false, Confidence = 1.0f,
            },
            new()
            {
                LineNumber = 2, BuyerItemCode = "B-2", SupplierItemCode = "SUP-2",
                Description = "Gadget", Quantity = 2m, Unit = "EA", UnitPrice = 5.5m,
                NeedsReview = false, Confidence = 1.0f,
            },
        };

        return order;
    }

    private static ITransformService TransformerFor(OutputFormat format) => format switch
    {
        OutputFormat.Xml  => new XmlTransformService(),
        OutputFormat.CXml => new CxmlTransformService(),
        OutputFormat.Ubl  => new UblOrderTransformService(),
        OutputFormat.X12  => new X12TransformService(),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string Run(ITransformService transformer, PurchaseOrderEntity order, OutputFormat format)
    {
        var result = transformer.TransformAsync(order, format, CancellationToken.None).GetAwaiter().GetResult();
        result.Content.Position = 0;
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    // ── SupportsOverrideFormat ────────────────────────────────────────────────

    [Fact]
    public void SupportsOverrideFormat_TrueForAllSixEntityFormats()
    {
        MappedTransformService.SupportsOverrideFormat(OutputFormat.Csv).Should().BeTrue();
        MappedTransformService.SupportsOverrideFormat(OutputFormat.Json).Should().BeTrue();
        MappedTransformService.SupportsOverrideFormat(OutputFormat.Xml).Should().BeTrue();
        MappedTransformService.SupportsOverrideFormat(OutputFormat.CXml).Should().BeTrue();
        MappedTransformService.SupportsOverrideFormat(OutputFormat.Ubl).Should().BeTrue();
        MappedTransformService.SupportsOverrideFormat(OutputFormat.X12).Should().BeTrue();

        // Canonical-model output formats remain out of scope.
        MappedTransformService.SupportsOverrideFormat(OutputFormat.UblOrder).Should().BeFalse();
        MappedTransformService.SupportsOverrideFormat(OutputFormat.X12_850).Should().BeFalse();
        MappedTransformService.SupportsOverrideFormat(OutputFormat.EdifactOrders).Should().BeFalse();
    }

    // ── No-override → byte-identical to the plain fixed transform ──────────────

    [Theory]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.Ubl)]
    public void NoOverride_ProducesByteIdenticalOutput_DeterministicFormats(OutputFormat format)
    {
        // XML + UBL contain no clock-dependent tokens, so a no-override effective entity must render
        // byte-for-byte identical to running the fixed transform on the original entity. The SAME source
        // instance feeds both renders (UBL embeds order.SupplierId, which is random per fixture build).
        var transformer = TransformerFor(format);
        var source      = BuildOrder();

        var original  = Run(transformer, source, format);
        var effective = Run(transformer, EffectiveEntityResolver.Resolve(source, new OrderMappingOverride()), format);

        effective.Should().Be(original);
    }

    [Fact]
    public void NoOverride_EffectiveEntityIsValueIdenticalToSource()
    {
        // Belt-and-braces for the clock-bearing formats (cXML / X12): prove the effective entity carries
        // the exact same canonical values, so the only output difference can be the timestamp the fixed
        // transform itself injects (not anything the override path changed).
        var source    = BuildOrder();
        var effective = EffectiveEntityResolver.Resolve(BuildOrder(), new OrderMappingOverride());

        effective.PoNumber.Should().Be(source.PoNumber);
        effective.OrderDate.Should().Be(source.OrderDate);
        effective.Currency.Should().Be(source.Currency);
        effective.BuyerName.Should().Be(source.BuyerName);
        effective.Supplier!.Name.Should().Be(source.Supplier!.Name);
        effective.Lines.Should().BeEquivalentTo(source.Lines, opts => opts
            .Including(l => l.LineNumber)
            .Including(l => l.BuyerItemCode)
            .Including(l => l.SupplierItemCode)
            .Including(l => l.Description)
            .Including(l => l.Quantity)
            .Including(l => l.Unit)
            .Including(l => l.UnitPrice));
    }

    // ── Header fixedValue override shows up in every structured format ─────────

    [Theory]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.CXml)]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.X12)]
    public void HeaderFixedValueOverride_OnPoNumber_AppearsInOutput(OutputFormat format)
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PoNumber", FixedValue = "OVERRIDDEN-PO" } },
            },
        };

        var effective = EffectiveEntityResolver.Resolve(BuildOrder(), ov);
        effective.PoNumber.Should().Be("OVERRIDDEN-PO");

        var output = Run(TransformerFor(format), effective, format);
        output.Should().Contain("OVERRIDDEN-PO");
        output.Should().NotContain("PO-9001");
    }

    [Theory]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.CXml)]
    [InlineData(OutputFormat.Ubl)]
    public void HeaderCurrencyOverride_AppliesToEffectiveEntity(OutputFormat format)
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["cur"] = new OutputFieldRule { OutputPath = "Currency", FixedValue = "USD" } },
            },
        };

        var effective = EffectiveEntityResolver.Resolve(BuildOrder(), ov);
        effective.Currency.Should().Be("USD");

        var output = Run(TransformerFor(format), effective, format);
        output.Should().Contain("USD");
    }

    [Fact]
    public void HeaderSupplierNameOverride_UpdatesBothSupplierNameAndNavName_AndShowsInXml()
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["sup"] = new OutputFieldRule { OutputPath = "SupplierName", FixedValue = "New Supplier GmbH" } },
            },
        };

        var effective = EffectiveEntityResolver.Resolve(BuildOrder(), ov);
        effective.SupplierName.Should().Be("New Supplier GmbH");
        effective.Supplier!.Name.Should().Be("New Supplier GmbH");

        var xml = Run(new XmlTransformService(), effective, OutputFormat.Xml);
        xml.Should().Contain("<SupplierName>New Supplier GmbH</SupplierName>");
    }

    // ── Line override (canonicalField) shows up ───────────────────────────────

    [Theory]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.CXml)]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.X12)]
    public void LineSupplierItemCodeOverride_WithManipulator_AppearsInOutput(OutputFormat format)
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["code"] = new OutputFieldRule
                    {
                        OutputPath = "SupplierItemCode", CanonicalField = "SupplierItemCode",
                        FieldManipulators = { new ManipulatorEntry { Type = "Replace", Params = { "SUP-", "VND/" } } },
                    },
                },
            },
        };

        var effective = EffectiveEntityResolver.Resolve(BuildOrder(), ov);
        effective.Lines.Select(l => l.SupplierItemCode).Should().BeEquivalentTo(new[] { "VND/1", "VND/2" });

        var output = Run(TransformerFor(format), effective, format);
        output.Should().Contain("VND/1");
        output.Should().Contain("VND/2");
    }

    // ── Expression override (Scriban) shows up ────────────────────────────────

    [Fact]
    public void HeaderExpressionOverride_OnPoNumber_AppearsInUbl()
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PoNumber", Expression = "{{ order.Currency }}-{{ order.PoNumber }}" } },
            },
        };

        var effective = EffectiveEntityResolver.Resolve(BuildOrder(), ov);
        effective.PoNumber.Should().Be("EUR-PO-9001");

        var ubl = Run(new UblOrderTransformService(), effective, OutputFormat.Ubl);
        ubl.Should().Contain("EUR-PO-9001");
    }

    // ── Unrecognised output path is ignored for structured formats ────────────

    [Fact]
    public void UnrecognisedOutputPath_DoesNotAlterTypedColumns()
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                // "CustomThing" is not a recognised canonical column — must be ignored for structured formats.
                Header = { ["x"] = new OutputFieldRule { OutputPath = "CustomThing", FixedValue = "ignored" } },
            },
        };

        var effective = EffectiveEntityResolver.Resolve(BuildOrder(), ov);
        effective.PoNumber.Should().Be("PO-9001");      // unchanged
        effective.Currency.Should().Be("EUR");          // unchanged

        var xml = Run(new XmlTransformService(), effective, OutputFormat.Xml);
        xml.Should().NotContain("ignored");
    }

    // ── The clone never mutates the source entity ─────────────────────────────

    [Fact]
    public void Resolve_NeverMutatesTheSourceEntity()
    {
        var source = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PoNumber", FixedValue = "MUTATED" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "SupplierItemCode", FixedValue = "X" } },
            },
        };

        _ = EffectiveEntityResolver.Resolve(source, ov);

        source.PoNumber.Should().Be("PO-9001");
        source.Lines[0].SupplierItemCode.Should().Be("SUP-1");
        source.Supplier!.Name.Should().Be("Original Supplier OÜ");
    }
}
