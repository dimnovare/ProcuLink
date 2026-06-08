using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.SourceMap;

/// <summary>
/// Tests for <see cref="SourceTokenizer"/>.
/// Covers: CSV cell-by-cell addressing, XML leaf+attribute addressing, and empty-list
/// passthrough for unsupported formats.
/// </summary>
public class SourceTokenizerTests
{
    private static readonly SourceTokenizer Tokenizer = new();

    // ── CSV ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csv_SingleHeaderSingleDataRow_EmitsExpectedIds()
    {
        var csv = "PoNumber,BuyerName\nPO-001,Acme\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".csv");

        // Row 1 (header): cell:r1c1 = "PoNumber", cell:r1c2 = "BuyerName"
        tokens.Should().Contain(t => t.Id == "cell:r1c1" && t.Value == "PoNumber");
        tokens.Should().Contain(t => t.Id == "cell:r1c2" && t.Value == "BuyerName");
        // Row 2 (data): cell:r2c1 = "PO-001", cell:r2c2 = "Acme"
        tokens.Should().Contain(t => t.Id == "cell:r2c1" && t.Value == "PO-001");
        tokens.Should().Contain(t => t.Id == "cell:r2c2" && t.Value == "Acme");
    }

    [Fact]
    public async Task Csv_HeaderRowTokens_HaveHeaderGroup()
    {
        var csv = "PoNumber,BuyerName\nPO-001,Acme\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".csv");

        tokens.Where(t => t.Id.StartsWith("cell:r1")).Should().OnlyContain(t => t.Group == "header");
    }

    [Fact]
    public async Task Csv_DataRowTokens_HaveLineGroup()
    {
        var csv = "PoNumber,BuyerName\nPO-001,Acme\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".csv");

        tokens.Where(t => t.Id.StartsWith("cell:r2")).Should().OnlyContain(t => t.Group == "line");
    }

    [Fact]
    public async Task Csv_MultipleDataRows_AllCellsEmitted()
    {
        var csv = "A,B\n1,2\n3,4\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".csv");

        tokens.Should().HaveCount(6); // 3 rows × 2 cols
        tokens.Should().Contain(t => t.Id == "cell:r3c2" && t.Value == "4");
    }

    [Fact]
    public async Task Csv_SemicolonDelimiter_ResolvedCorrectly()
    {
        // Semicolon-delimited (European locale) file — no commas in the header.
        var csv = "PoNumber;BuyerName\nPO-001;Acme\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".csv");

        tokens.Should().Contain(t => t.Id == "cell:r1c1" && t.Value == "PoNumber");
        tokens.Should().Contain(t => t.Id == "cell:r2c2" && t.Value == "Acme");
    }

    [Fact]
    public async Task Csv_Label_IncludesColumnHeaderName()
    {
        var csv = "PoNumber,BuyerName\nPO-001,Acme\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".csv");

        // Data-row labels should reference the column header name.
        var poCell = tokens.First(t => t.Id == "cell:r2c1");
        poCell.Label.Should().Contain("PoNumber");
    }

    // ── XML ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Xml_LeafElements_EmittedAsTokens()
    {
        var xml = "<Order><PoNumber>PO-999</PoNumber><BuyerName>Corp</BuyerName></Order>";
        var bytes = Encoding.UTF8.GetBytes(xml);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xml");

        tokens.Should().Contain(t => t.Id == "/Order/PoNumber" && t.Value == "PO-999");
        tokens.Should().Contain(t => t.Id == "/Order/BuyerName" && t.Value == "Corp");
    }

    [Fact]
    public async Task Xml_Attributes_EmittedAsTokens()
    {
        var xml = "<Order id=\"ORD-1\"><Line qty=\"5\">Widget</Line></Order>";
        var bytes = Encoding.UTF8.GetBytes(xml);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xml");

        tokens.Should().Contain(t => t.Id == "/Order/@id" && t.Value == "ORD-1");
        tokens.Should().Contain(t => t.Id == "/Order/Line/@qty" && t.Value == "5");
    }

    [Fact]
    public async Task Xml_RepeatingElements_PositionalPredicatesAdded()
    {
        var xml = "<Order><Lines><Line><ItemCode>A</ItemCode></Line><Line><ItemCode>B</ItemCode></Line></Lines></Order>";
        var bytes = Encoding.UTF8.GetBytes(xml);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xml");

        tokens.Should().Contain(t => t.Id == "/Order/Lines/Line[1]/ItemCode" && t.Value == "A");
        tokens.Should().Contain(t => t.Id == "/Order/Lines/Line[2]/ItemCode" && t.Value == "B");
    }

    [Fact]
    public async Task Xml_NonRepeatingElements_NoPositionalPredicate()
    {
        var xml = "<Order><Header><PoNumber>PO-1</PoNumber></Header></Order>";
        var bytes = Encoding.UTF8.GetBytes(xml);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xml");

        // Header and PoNumber each appear once — no predicate.
        tokens.Should().Contain(t => t.Id == "/Order/Header/PoNumber" && t.Value == "PO-1");
        tokens.Should().NotContain(t => t.Id.Contains("[1]"));
    }

    [Fact]
    public async Task Xml_MalformedXml_ReturnsEmptyList()
    {
        var bytes = Encoding.UTF8.GetBytes("<not valid xml << &");

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xml");

        tokens.Should().BeEmpty();
    }

    // ── Unsupported formats → empty list ─────────────────────────────────────

    [Theory]
    [InlineData(".pdf")]   // PDF has no stable addressable cells
    [InlineData(".edifact")] // Unknown extension — not the canonical .edi
    [InlineData(".txt")]
    [InlineData("")]
    public async Task UnsupportedFormat_ReturnsEmptyList(string ext)
    {
        // Tokenisation not implemented for these formats/extensions.
        // Must return empty, not throw.
        var bytes = Encoding.UTF8.GetBytes("some content");

        var tokens = await Tokenizer.TokenizeAsync(bytes, ext);

        tokens.Should().BeEmpty();
    }

    [Fact]
    public async Task Csv_EmptyFile_ReturnsEmptyList()
    {
        var tokens = await Tokenizer.TokenizeAsync(Array.Empty<byte>(), ".csv");
        tokens.Should().BeEmpty();
    }
}
