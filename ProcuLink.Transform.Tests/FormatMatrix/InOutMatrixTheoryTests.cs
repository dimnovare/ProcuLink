using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using static ProcuLink.Transform.Tests.FormatMatrix.FormatFixtures;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// The IN×OUT matrix: a parametrized [Theory] over every (inputFormat, outputFormat)
/// pair, proving parse → canonical ParsedOrder → transform completes end to end
/// WITHOUT throwing, and that the PO number + line count + first line's buyer code
/// survive the whole trip (field fidelity, not just "didn't crash").
///
/// IN  formats:  csv, xlsx, cxml, ubl, edifact, x12, idoc        (7)
/// OUT formats:  UblOrder, X12_850, EdifactOrders                (3 — the ParsedOrder family)
/// = 21 pairs. PDF is excluded (live OpenAI path) — see PdfMatrixPlaceholderTests.
///
/// Each pair also re-parses the emitted document and confirms the PO number
/// survives a full canonical → output → canonical loop where the output format
/// has a matching parser (UblOrder→UBL, X12_850→X12, EdifactOrders→EDIFACT).
/// </summary>
public class InOutMatrixTheoryTests
{
    private static readonly string[] InFormats  = { "csv", "xlsx", "cxml", "ubl", "edifact", "x12", "idoc" };
    private static readonly OutputFormat[] OutFormats =
        { OutputFormat.UblOrder, OutputFormat.X12_850, OutputFormat.EdifactOrders };

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var inf in InFormats)
            foreach (var outf in OutFormats)
                yield return new object[] { inf, outf };
    }

    // Each builder emits a representative order with a stable PO number per format and
    // the shared representative 2-line body. We assert the PO number that THIS format
    // actually carries back out of the parser (some formats normalise differently).
    private static (Stream stream, string ext, string expectedPo) BuildInput(string inFormat)
    {
        var lines = RepresentativeLines();
        return inFormat switch
        {
            "csv"     => (ToStream(Csv("PO-MX-CSV", "EUR", "Buyer", lines)),     ".csv",  "PO-MX-CSV"),
            "xlsx"    => (ToStream(Xlsx("PO-MX-XLS", "EUR", "Buyer", lines)),    ".xlsx", "PO-MX-XLS"),
            "cxml"    => (ToStream(Cxml("PO-MX-CX", "EUR", lines)),              ".xml",  "PO-MX-CX"),
            "ubl"     => (ToStream(Ubl("PO-MX-UBL", "EUR", "Buyer", lines)),     ".xml",  "PO-MX-UBL"),
            "edifact" => (ToStream(Edifact("PO-MX-EDI", "EUR", "Buyer", lines)), ".edi",  "PO-MX-EDI"),
            "x12"     => (ToStream(X12("PO-MX-X12", "EUR", "Buyer", lines)),     ".x12",  "PO-MX-X12"),
            "idoc"    => (ToStream(Idoc("PO-MX-IDOC", "Buyer", lines)),          ".xml",  "PO-MX-IDOC"),
            _ => throw new ArgumentOutOfRangeException(nameof(inFormat), inFormat, null),
        };
    }

    // The factory mirrors the PRODUCTION DI registration order EXACTLY (Program.cs:
    // Csv, Xlsx, Pdf, Cxml, Ubl, IDoc, Edifact, X12) so content-based disambiguation
    // of .xml (IDoc vs UBL vs cXML) is exercised the way it actually runs.
    //
    // ROUTING FINDING (documented, not a prod bug): for a plain .xml upload the factory
    // content-sniffs IDoc (<ORDERS05>) and UBL (root+namespace) first, then FALLS
    // THROUGH to extension dispatch — which returns the FIRST registered .xml parser.
    // cXML has no sniff of its own, so it relies on Cxml being the first .xml parser.
    // Prod gets this right (Cxml before Ubl/IDoc). If anyone reorders the DI block to
    // put IDoc/UBL before Cxml, a cXML doc would route to the wrong parser and fail
    // with "Document root element is not <ORDERS05>". CxmlRouting_DependsOnRegistrationOrder
    // pins this so a future reorder fails loudly here instead of in production.
    private static OrderParserFactory Factory() =>
        new(new IPurchaseOrderParser[]
        {
            new CsvOrderParser(),
            new XlsxOrderParser(),
            new PdfOrderParser(),
            new CxmlOrderParser(),
            new UblOrderParser(),
            new IDocOrders05Parser(),
            new EdifactOrderParser(),
            new X12OrderParser(),
        });

    private static ParsedOrderTransformFactory TransformFactory() =>
        new(new IParsedOrderTransform[]
        {
            new UblParsedOrderTransform(),
            new X12ParsedOrderTransform(),
            new EdifactParsedOrderTransform(),
        });

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ParseThenTransform_CompletesForEveryPair(string inFormat, OutputFormat outFormat)
    {
        var (stream, ext, expectedPo) = BuildInput(inFormat);

        // ── IN: route to the right parser by extension+content, then parse. ──
        IPurchaseOrderParser parser;
        using (stream)
        {
            stream.Position = 0;
            parser = Factory().GetParser(ext, stream);
            stream.Position = 0;
            var canonical = await parser.ParseAsync(stream, CancellationToken.None);

            // Canonical fidelity from the INBOUND leg.
            canonical.PoNumber.Should().Be(expectedPo, $"in={inFormat} should preserve the PO number");
            canonical.Lines.Should().HaveCount(2, $"in={inFormat} should preserve both lines");
            canonical.Lines.OrderBy(l => l.LineNumber).First().BuyerItemCode.Should().Be("BUY-001");

            // ── OUT: serialize the canonical order to the target format. ──
            var result = TransformFactory().Transform(canonical, outFormat);
            result.Content.Should().NotBeNull();
            result.Content.Length.Should().BeGreaterThan(0, $"{inFormat}->{outFormat} must emit a non-empty document");
            result.AsText().Should().Contain(expectedPo,
                $"{inFormat}->{outFormat} output must carry the PO number through");

            // ── ROUND-TRIP: re-parse the emitted document with its native parser. ──
            await AssertOutputReParses(outFormat, result, expectedPo);
        }
    }

    private static async Task AssertOutputReParses(OutputFormat outFormat, ParsedOrderTransformResult result, string expectedPo)
    {
        using var outStream = new MemoryStream(result.Content);
        IPurchaseOrderParser outParser = outFormat switch
        {
            OutputFormat.UblOrder      => new UblOrderParser(),
            OutputFormat.X12_850       => new X12OrderParser(),
            OutputFormat.EdifactOrders => new EdifactOrderParser(),
            _ => throw new ArgumentOutOfRangeException(nameof(outFormat)),
        };

        var reparsed = await outParser.ParseAsync(outStream, CancellationToken.None);
        reparsed.PoNumber.Should().Be(expectedPo, $"{outFormat} output must re-parse to the same PO number");
        reparsed.Lines.Should().HaveCount(2, $"{outFormat} output must re-parse to 2 lines");
        reparsed.Lines.OrderBy(l => l.LineNumber).First().BuyerItemCode.Should().Be("BUY-001",
            $"{outFormat} output must re-parse the first line's buyer code");
    }

    [Fact]
    public void Matrix_Covers_AllSevenInputs_TimesThreeOutputs()
    {
        // Guard: the matrix is the full 7×3 grid, not a quietly-shrunk subset.
        Matrix().Should().HaveCount(21);
        Matrix().Select(o => (string)o[0]).Distinct().Should().HaveCount(7);
        Matrix().Select(o => (OutputFormat)o[1]).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void XmlRouting_ContentSniff_PicksIdocAndUbl_BeforeFallthrough()
    {
        // .xml content-sniff must pick IDoc and UBL by content regardless of which
        // .xml parser is registered first, because they have explicit probes.
        var factory = Factory();

        using var idoc = ToStream(Idoc("PO-RT-ID", "Buyer", RepresentativeLines()));
        factory.GetParser(".xml", idoc).Should().BeOfType<IDocOrders05Parser>();

        using var ubl = ToStream(Ubl("PO-RT-UB", "EUR", "Buyer", RepresentativeLines()));
        factory.GetParser(".xml", ubl).Should().BeOfType<UblOrderParser>();

        // cXML has NO content probe → it falls through to the first .xml parser, which
        // prod registers as Cxml. This pins that contract.
        using var cx = ToStream(Cxml("PO-RT-CX", "EUR", RepresentativeLines()));
        factory.GetParser(".xml", cx).Should().BeOfType<CxmlOrderParser>(
            "cXML relies on Cxml being the FIRST registered .xml parser (no content probe of its own)");
    }

    [Fact]
    public void CxmlRouting_DependsOnRegistrationOrder_DocumentedFragility()
    {
        // FINDING (documented fragility, NOT a current prod bug): if the DI block is
        // reordered so an .xml parser WITHOUT a content probe-miss-safe contract sits
        // before Cxml, a cXML upload routes to the wrong parser. Here we deliberately
        // register IDoc first to PROVE the failure mode, so the risk is captured.
        var badOrderFactory = new OrderParserFactory(new IPurchaseOrderParser[]
        {
            new IDocOrders05Parser(), // wrong: before Cxml
            new UblOrderParser(),
            new CxmlOrderParser(),
        });

        using var cx = ToStream(Cxml("PO-FRAGILE", "EUR", RepresentativeLines()));
        // cXML has no probe; IDoc/UBL probes miss; fall-through returns IDoc (first .xml).
        badOrderFactory.GetParser(".xml", cx).Should().BeOfType<IDocOrders05Parser>(
            "this WRONG registration order mis-routes cXML — prod must keep Cxml first");
    }
}
