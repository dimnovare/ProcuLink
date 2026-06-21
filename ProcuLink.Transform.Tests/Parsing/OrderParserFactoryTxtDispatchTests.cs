using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;
using ProcuLink.Transform.Tests.FormatMatrix;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// IN-3 regression: a <c>.txt</c> EDIFACT file must route to the EDIFACT parser
/// rather than being rejected as an unsupported format.
///
/// Root cause: the ingest path runs an EXTENSION-ONLY gate
/// (<see cref="OrderParserFactory.GetParser(string)"/>) before the content-sniffing
/// stream overload. <see cref="EdifactOrderParser.CanParse"/> previously claimed
/// only <c>.edi</c>, so a <c>.txt</c> EDIFACT file threw
/// <see cref="UnsupportedFileFormatException"/> at the gate and was set Failed —
/// never reaching the sniffing overload that WOULD route it to EDIFACT.
///
/// These tests assert BOTH the gate (extension-only) and the dispatch
/// (stream-aware) behaviours, plus the critical edge: a NON-EDIFACT <c>.txt</c>
/// must NOT silently succeed as an empty/0-line order.
/// </summary>
public class OrderParserFactoryTxtDispatchTests
{
    // A factory wired with the full set of order parsers, mirroring DI registration
    // order in Program.cs closely enough for dispatch behaviour.
    private static OrderParserFactory BuildFactory() => new(new IPurchaseOrderParser[]
    {
        new CsvOrderParser(),
        new XlsxOrderParser(),
        new CxmlOrderParser(),
        new UblOrderParser(),
        new IDocOrders05Parser(),
        new EdifactOrderParser(),
        new X12OrderParser(),
        new PdfOrderParser(),
    });

    private static Stream ToStream(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    private const string NL = "\r\n";

    private static string EdifactOrders(string poNumber = "PO-TXT-001") =>
        "UNA:+.? '" +
        "UNB+UNOC:3+SENDER:14+RECEIVER:14+240115:0900+1'" + NL +
        "UNH+1+ORDERS:D:96A:UN'" + NL +
        $"BGM+220+{poNumber}+9'" + NL +
        "DTM+137:20240115:102'" + NL +
        "CUX+2:EUR:9'" + NL +
        "NAD+BY+5412345678901::9++Acme Buyer Ltd'" + NL +
        "LIN+1++ITEM-A:IN'" + NL +
        "IMD+F+ANM+:::Widget Type A'" + NL +
        "QTY+21:100:PCE'" + NL +
        "PRI+AAA:12.50'" + NL +
        "UNS+S'" + NL +
        "UNT+10+1'" + NL +
        "UNZ+1+1'" + NL;

    // ── 1. .txt EDIFACT routes to EdifactOrderParser, gate passes, lines parse ──

    [Fact]
    public void GetParser_TxtEdifactContent_ReturnsEdifactParser()
    {
        var factory = BuildFactory();
        using var stream = ToStream(EdifactOrders());

        var parser = factory.GetParser(".txt", stream);

        parser.Should().BeOfType<EdifactOrderParser>();
    }

    [Fact]
    public void GetParser_ExtensionOnlyGate_AcceptsTxt_DoesNotThrowUnsupported()
    {
        // The ingest path runs GetParser(extension) (extension-only) as a gate before
        // the stream overload. Before the fix this threw UnsupportedFileFormatException
        // for .txt and the order was set Failed. It must now resolve a parser.
        var factory = BuildFactory();

        var act = () => factory.GetParser(".txt");

        act.Should().NotThrow();
        new EdifactOrderParser().CanParse(".txt").Should().BeTrue();
    }

    [Fact]
    public async Task GetParser_TxtEdifactContent_ParsesRealFieldsAndLines()
    {
        var factory = BuildFactory();
        using var stream = ToStream(EdifactOrders("PO-TXT-LIVE"));

        var parser = factory.GetParser(".txt", stream);
        stream.Position = 0;
        var order = await parser.ParseAsync(stream, CancellationToken.None);

        order.PoNumber.Should().Be("PO-TXT-LIVE");
        order.Currency.Should().Be("EUR");
        order.Lines.Should().HaveCountGreaterThan(0);
        order.Lines[0].BuyerItemCode.Should().Be("ITEM-A");
        order.Lines[0].Quantity.Should().Be(100m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
    }

    // ── 2. Non-EDIFACT .txt does NOT silently succeed ──────────────────────────

    [Fact]
    public async Task GetParser_PlainTextTxt_RoutesToEdifact_ButParsingFailsLoudly()
    {
        // A plain-text .txt fails content-sniffing (not X12, not EDIFACT envelope),
        // so it falls through to GetParser(".txt") → EdifactOrderParser. Parsing it
        // MUST fail loudly (typed exception) rather than yield a silent empty order.
        var factory = BuildFactory();
        const string plain =
            "Hi team, this is a normal text note, not an EDI file." + NL +
            "We will ship the goods next week. Thanks." + NL;

        using var stream = ToStream(plain);
        var parser = factory.GetParser(".txt", stream);
        parser.Should().BeOfType<EdifactOrderParser>();

        stream.Position = 0;
        var act = async () => await parser.ParseAsync(stream, CancellationToken.None);

        // Loud failure — never a populated-but-empty success.
        await act.Should().ThrowAsync<EdifactParseException>();
    }

    [Fact]
    public async Task PlainTextTxt_NeverProducesSilentEmptyZeroLineSuccess()
    {
        // Belt-and-braces: even if a stray ' terminator made the tokenizer emit a
        // segment, a plain-text .txt has no UNH/ORDERS envelope, so ParseAsync throws
        // (it never returns an order with 0 lines that the ingest guard would have to
        // catch as "delivered but empty").
        const string plain = "just one line of plain text with no edi structure";

        var parser = new EdifactOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(plain), CancellationToken.None);

        await act.Should().ThrowAsync<EdifactParseException>();
    }

    // ── 3. Regression: X12 .txt still routes to X12, NOT EDIFACT ────────────────

    [Fact]
    public void GetParser_TxtX12Content_StillRoutesToX12_NotEdifact()
    {
        // The X12 (ISA…ST*850) sniff runs BEFORE the EDIFACT sniff in the factory, so
        // an X12 interchange carrying a .txt extension must still select X12.
        var factory = BuildFactory();
        var x12Bytes = FormatFixtures.X12(
            "PO-X12-TXT", "USD", "Acme Buying Corp", FormatFixtures.RepresentativeLines());
        using var stream = new MemoryStream(x12Bytes);

        var parser = factory.GetParser(".txt", stream);

        parser.Should().BeOfType<X12OrderParser>();
    }

    // ── 4. Byte-safety: untouched extensions select their existing parsers ──────

    [Theory]
    [InlineData(".csv", typeof(CsvOrderParser))]
    [InlineData(".xlsx", typeof(XlsxOrderParser))]
    [InlineData(".pdf", typeof(PdfOrderParser))]
    [InlineData(".x12", typeof(X12OrderParser))]
    [InlineData(".edi", typeof(EdifactOrderParser))]
    public void GetParser_ExtensionOnly_UntouchedExtensions_SelectExistingParser(
        string extension, Type expected)
    {
        var factory = BuildFactory();

        var parser = factory.GetParser(extension);

        parser.Should().BeOfType(expected);
    }

    [Fact]
    public void CanParse_UntouchedParsers_AreUnchangedForTheirOwnExtensions()
    {
        // The fix only widened EdifactOrderParser.CanParse. Confirm the others still
        // claim exactly their own extension and nothing extra leaked in.
        new CsvOrderParser().CanParse(".csv").Should().BeTrue();
        new XlsxOrderParser().CanParse(".xlsx").Should().BeTrue();
        new PdfOrderParser().CanParse(".pdf").Should().BeTrue();
        new X12OrderParser().CanParse(".x12").Should().BeTrue();
        new X12OrderParser().CanParse(".txt").Should().BeFalse();   // X12 .txt is sniffed, not CanParse-claimed
        new CsvOrderParser().CanParse(".txt").Should().BeFalse();
        new XlsxOrderParser().CanParse(".txt").Should().BeFalse();
    }
}
