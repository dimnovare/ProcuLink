using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Tests for what is left of <see cref="MappedTransformService"/>: <c>ResolveRule</c>, the value
/// resolution every output-tree leaf goes through (fixed value, field, then the
/// manipulator chain), and <c>Escape</c>, the one CSV cell escaper. Rendered through
/// <see cref="OutputTemplateEmitter.EmitRows"/>, the entry the engine uses.
/// </summary>
public class MappedTransformServiceTests
{
    private static string ReadCsv(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Csv_AppliesManipulators()
    {
        var output = new OutputMappingConfig
        {
            Lines =
            {
                // Replace "SUP-" → "S/", then the qty multiplied by 100 (cents).
                ["code"] = new OutputFieldRule
                {
                    OutputPath = "Code", CanonicalField = "SupplierItemCode",
                    FieldManipulators = { new ManipulatorEntry { Type = "Replace", Params = { "SUP-", "S/" } } },
                },
                ["cents"] = new OutputFieldRule
                {
                    OutputPath = "PriceCents", CanonicalField = "UnitPrice",
                    FieldManipulators = { new ManipulatorEntry { Type = "Multiply", Params = { "100" } } },
                },
            },
        };

        var csv = ReadCsv(EmitterRows.FlatCsv(output));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Code,PriceCents");
        rows[1].Should().Be("S/1,1000");   // SUP-1 → S/1 ; 10 * 100 = 1000
        rows[2].Should().Be("S/2,550");    // SUP-2 → S/2 ; 5.5 * 100 = 550
    }

    [Fact]
    public void Csv_FixedValue_IsEmitted()
    {
        var output = new OutputMappingConfig
        {
            Header = { ["src"] = new OutputFieldRule { OutputPath = "Source", FixedValue = "ProcuLink" } },
            Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
        };

        var csv = ReadCsv(EmitterRows.FlatCsv(output));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Source,Code");
        rows[1].Should().Be("ProcuLink,SUP-1");
    }

    // CSV injection: mapped cells are untyped strings, so a formula is neutralised by value while a
    // plain number like -5,50 (a culture-formatted amount) is left numeric. OutputTemplateEmitter shares the same Escape.
    [Theory]
    [InlineData("-5,50")]
    [InlineData("-1 234,50")]
    [InlineData("-1\u00A0234,50")]
    public void Csv_FormulaNeutralised_PlainNumberStaysNumeric(string number)
    {
        var output = new OutputMappingConfig
        {
            Header = { ["src"] = new OutputFieldRule { OutputPath = "Note", FixedValue = "=HYPERLINK(\"x\")" } },
            Lines  = { ["adj"] = new OutputFieldRule { OutputPath = "Adj", FixedValue = number } },
        };

        var rows = ReadCsv(EmitterRows.FlatCsv(output)).Replace("\r\n", "\n").Split('\n');

        rows[1].Should().Be($"\"'=HYPERLINK(\"\"x\"\")\",\"{number}\"");
    }

    [Fact]
    public void Csv_EscapesValuesContainingCommas()
    {
        var output = new OutputMappingConfig
        {
            Lines = { ["desc"] = new OutputFieldRule { OutputPath = "Description", CanonicalField = "Description" } },
        };

        var csv = ReadCsv(EmitterRows.FlatCsv(output, EmitterRows.Po9001Header(),
            new Dictionary<string, string> { ["Description"] = "Widget, large" }));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[1].Should().Be("\"Widget, large\"");
    }
}
