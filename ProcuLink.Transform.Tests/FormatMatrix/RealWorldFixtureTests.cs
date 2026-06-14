using System.Globalization;
using System.Reflection;
using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// Tests against the real-world XML fixtures committed in RealWorldFixtures/.
/// Each fixture is a sanitized (PII-removed) copy of a real PO that arrived in
/// the founder's inbox.  The fixtures are embedded resources so they are
/// hermetic and version-controlled.
///
/// What is tested here (deterministic, no network):
///   1. Parser routing — content-sniffing correctly identifies cXML vs IDoc.
///   2. Canonical parse — order has ≥1 line, a non-null PO number, no negative/NaN
///      quantities or prices, and where the document states a line amount,
///      qty × unitPrice reconciles within €0.01.
///   3. All-output round-trip — every parsed order is serialized through ALL
///      available output transforms; no transform may throw, and every output
///      must be non-empty.
///
/// What is NOT tested here (non-deterministic / integration-only):
///   • PDF text→LLM extraction (OpenAI key required) — see PdfMatrixPlaceholderTests.
///   • PDF vision fallback (OpenAI vision required) — see PdfMatrixPlaceholderTests.
///   • Real-network delivery (webhook, SFTP, Erply, Directo).
/// </summary>
public class RealWorldFixtureTests
{
    // ── Fixture loading (embedded resources) ────────────────────────────────

    /// <summary>
    /// Returns a Stream for a fixture embedded as a resource.
    /// Fixtures are in ProcuLink.Transform.Tests.FormatMatrix.RealWorldFixtures.
    /// </summary>
    private static Stream LoadFixture(string resourceName)
    {
        var asm      = Assembly.GetExecutingAssembly();
        var fullName = $"ProcuLink.Transform.Tests.FormatMatrix.RealWorldFixtures.{resourceName}";
        var stream   = asm.GetManifestResourceStream(fullName);
        stream.Should().NotBeNull($"Embedded resource '{fullName}' must exist in the test assembly. " +
            "Check that the file is marked as EmbeddedResource in the .csproj.");
        return stream!;
    }

    // The production DI registration order (matches InOutMatrixTheoryTests.Factory()).
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

    // All ParsedOrder transforms (UBL, X12, EDIFACT) — the hermetic trio.
    private static ParsedOrderTransformFactory TransformFactory() =>
        new(new IParsedOrderTransform[]
        {
            new UblParsedOrderTransform(),
            new X12ParsedOrderTransform(),
            new EdifactParsedOrderTransform(),
        });

    private static readonly OutputFormat[] AllParsedFormats =
        { OutputFormat.UblOrder, OutputFormat.X12_850, OutputFormat.EdifactOrders };

    /// <summary>
    /// Core assertion helper: parse the fixture, check canonical invariants,
    /// then round-trip through every output transform.
    /// </summary>
    private static async Task AssertRealWorldFixture(
        string resourceName,
        Type expectedParserType,
        string expectedPoNumber,
        int expectedMinLines,
        string? expectedCurrency = null)
    {
        using var stream = LoadFixture(resourceName);

        // ── Parser routing ───────────────────────────────────────────────────
        var factory = Factory();
        var parser  = factory.GetParser(".xml", stream);
        parser.Should().BeOfType(expectedParserType,
            $"'{resourceName}' must route to {expectedParserType.Name}");

        // ── Parse ─────────────────────────────────────────────────────────────
        stream.Position = 0;
        var order = await parser.ParseAsync(stream, CancellationToken.None);

        order.PoNumber.Should().Be(expectedPoNumber,
            $"'{resourceName}' should preserve the PO number '{expectedPoNumber}'");
        order.Lines.Should().HaveCountGreaterThanOrEqualTo(expectedMinLines,
            $"'{resourceName}' should have ≥{expectedMinLines} lines");

        if (expectedCurrency is not null)
            order.Currency.Should().Be(expectedCurrency,
                $"'{resourceName}' currency should be '{expectedCurrency}'");

        // ── Invariants on every line ──────────────────────────────────────────
        foreach (var line in order.Lines)
        {
            var ctx = $"line {line.LineNumber} in '{resourceName}'";

            // Real POs should have positive quantities (negative would be a return/credit,
            // which is flagged for review). The key assertion is no silent corruption to
            // a nonsense value.
            line.Quantity.Should().BeGreaterThanOrEqualTo(0m,
                $"{ctx}: real POs should not have negative quantities");

            if (line.UnitPrice is { } price)
                price.Should().BeGreaterThanOrEqualTo(0m,
                    $"{ctx}: unit price must not be negative");

            // Line amount reconciliation: qty × unitPrice must round to the stated amount.
            if (line.LineAmount is { } lineAmt && line.UnitPrice is { } up)
            {
                var computed = Math.Round(line.Quantity * up, 2, MidpointRounding.AwayFromZero);
                Math.Abs(computed - lineAmt).Should().BeLessThanOrEqualTo(0.01m,
                    $"{ctx}: qty({line.Quantity}) × unitPrice({up}) = {computed} " +
                    $"must reconcile with stated lineAmount({lineAmt}) within €0.01");
            }
        }

        // ── All-output round-trip: no transform may crash ─────────────────────
        foreach (var fmt in AllParsedFormats)
        {
            var result = TransformFactory().Transform(order, fmt);
            result.Content.Should().NotBeNull($"'{resourceName}' → {fmt} must not throw");
            result.Content.Length.Should().BeGreaterThan(0,
                $"'{resourceName}' → {fmt} must produce a non-empty document");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // cXML real-world fixtures
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task REDACTED_TEST_NAME()
    {
        // Real Nestlé PL cXML: 1 line, PLN currency, Polish unicode (Dodatkowa słuch).
        await AssertRealWorldFixture(
            resourceName:       "rw_cxml_pl_singleline_pln_unicode.xml",
            expectedParserType: typeof(CxmlOrderParser),
            expectedPoNumber:   "4577738226",
            expectedMinLines:   1,
            expectedCurrency:   "PLN");
    }

    [Fact]
    public async Task REDACTED_TEST_NAME()
    {
        // Sanitized Nasdaq/Coupa cXML from Spain: 1 line, EUR, distributor context.
        // The real file had no <Request deploymentMode> — that case is separately tested.
        await AssertRealWorldFixture(
            resourceName:       "rw_cxml_es_coupa_with_deploymentmode.xml",
            expectedParserType: typeof(CxmlOrderParser),
            expectedPoNumber:   "140181",
            expectedMinLines:   1,
            expectedCurrency:   "EUR");
    }

    [Fact]
    public async Task REDACTED_TEST_NAME()
    {
        // Real DPD PL cXML: 2 lines, PLN, original had a <!DOCTYPE> in it.
        // The sanitized copy strips the DOCTYPE so our parser doesn't reject it
        // (DtdProcessing.Prohibit). The test also validates the 2-line total reconciles.
        await AssertRealWorldFixture(
            resourceName:       "rw_cxml_pl_2lines_pln_no_dtd.xml",
            expectedParserType: typeof(CxmlOrderParser),
            expectedPoNumber:   "5293137",
            expectedMinLines:   2,
            expectedCurrency:   "PLN");

        // Explicit total check: 1×49.31 + 1×148.93 = 198.24 (matches stated Total).
        using var stream = LoadFixture("rw_cxml_pl_2lines_pln_no_dtd.xml");
        var parser = Factory().GetParser(".xml", stream);
        stream.Position = 0;
        var order = await parser.ParseAsync(stream, CancellationToken.None);

        var line1 = order.Lines.Single(l => l.LineNumber == 1);
        var line2 = order.Lines.Single(l => l.LineNumber == 2);
        line1.Quantity.Should().Be(1m);
        line1.UnitPrice.Should().Be(49.31m);
        line2.Quantity.Should().Be(1m);
        line2.UnitPrice.Should().Be(148.93m);
        var computedTotal = line1.Quantity * line1.UnitPrice!.Value
                          + line2.Quantity * line2.UnitPrice!.Value;
        computedTotal.Should().BeApproximately(198.24m, 0.01m,
            "1×49.31 + 1×148.93 should equal the declared total of 198.24 PLN");
    }

    [Fact]
    public async Task REDACTED_TEST_NAME()
    {
        // The real Nasdaq file has <Request> with no deploymentMode attribute (common in
        // Coupa tenants) and a <!DOCTYPE> header. Both used to make CxmlOrderParser throw —
        // this fixture documented that gap. It is now CLOSED: deploymentMode defaults to
        // "production" and the DOCTYPE is tolerated (XXE-safe), so a genuine Nasdaq/Coupa
        // OrderRequest parses end to end.
        using var stream = LoadFixture("rw_cxml_es_coupa_no_deploymentmode.xml");
        var factory = Factory();
        var parser = factory.GetParser(".xml", stream);
        parser.Should().BeOfType<CxmlOrderParser>("file has a cXML root element");

        stream.Position = 0;
        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().NotBeNullOrWhiteSpace();
        result.Lines.Should().NotBeEmpty();
    }

    // ════════════════════════════════════════════════════════════════════════
    // IDoc ORDERS05 real-world fixtures
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task REDACTED_TEST_NAME()
    {
        // Real SAP IDoc from Spain: CURCY=704 (numeric SAP-internal code) + SUNIT=EUR.
        // The parser must prefer SUNIT (alphabetic ISO) over CURCY (numeric). This is a
        // real-world instance of the canonical "IDoc currency gotcha" documented in the
        // memory notes and proven in InCoverageMatrixTests.Idoc_NumericCurcy704_*.
        using var stream = LoadFixture("rw_idoc_es_numeric_curcy704.xml");
        var factory = Factory();
        var parser  = factory.GetParser(".xml", stream);
        parser.Should().BeOfType<IDocOrders05Parser>(
            "ORDERS05 root element must route to IDocOrders05Parser");

        stream.Position = 0;
        var order = await parser.ParseAsync(stream, CancellationToken.None);

        order.PoNumber.Should().Be("0005472513");
        order.Currency.Should().Be("EUR",
            "E1EDS01 SUNIT=EUR must override the numeric CURCY=704 from E1EDK01");
        order.Lines.Should().HaveCount(1);
        order.Lines[0].Quantity.Should().Be(1m);
        order.Lines[0].UnitPrice.Should().Be(149.49m);

        foreach (var fmt in AllParsedFormats)
            TransformFactory().Transform(order, fmt).Content.Length.Should().BeGreaterThan(0,
                $"IDoc (numeric CURCY) → {fmt} must produce output");
    }

    [Fact]
    public async Task RealWorld_IDoc_ArayMond_IT_FiveLines_EUR_LineAmountsReconcile()
    {
        // Real REDACTED-NAME IDoc from Italy: 5 lines, EUR, long Italian descriptions.
        // Line amounts (NETWR) are explicitly stated; this test verifies
        // qty × unitPrice ≈ NETWR within €0.01 for every line.
        using var stream = LoadFixture("rw_idoc_it_5lines_netwr_reconcile.xml");
        var factory = Factory();
        var parser  = factory.GetParser(".xml", stream);
        parser.Should().BeOfType<IDocOrders05Parser>();

        stream.Position = 0;
        var order = await parser.ParseAsync(stream, CancellationToken.None);

        order.PoNumber.Should().Be("4501450099");
        order.Currency.Should().Be("EUR");
        order.Lines.Should().HaveCount(5);

        foreach (var line in order.Lines)
        {
            line.Quantity.Should().BeGreaterThan(0m);
            line.UnitPrice.Should().BeGreaterThan(0m);

            if (line.LineAmount is { } lineAmt)
            {
                var computed = Math.Round(line.Quantity * line.UnitPrice!.Value, 2, MidpointRounding.AwayFromZero);
                Math.Abs(computed - lineAmt).Should().BeLessThanOrEqualTo(0.01m,
                    $"Line {line.LineNumber}: qty({line.Quantity}) × price({line.UnitPrice}) " +
                    $"= {computed} vs stated {lineAmt}");
            }
        }

        order.GrandTotal.Should().BeApproximately(186.01m, 0.01m,
            "E1EDS01 SUMME=186.01 must be captured as GrandTotal");

        foreach (var fmt in AllParsedFormats)
            TransformFactory().Transform(order, fmt).Content.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task REDACTED_TEST_NAME()
    {
        await AssertRealWorldFixture(
            resourceName:       "rw_idoc_fr_2lines_eur.xml",
            expectedParserType: typeof(IDocOrders05Parser),
            expectedPoNumber:   "4501453089",
            expectedMinLines:   2,
            expectedCurrency:   "EUR");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PDF deterministic layer (text extraction only — NO LLM)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tests the DETERMINISTIC layer of the PDF pipeline (PdfPig text extraction
    /// + regex PdfOrderParser fallback). The founder's 22 real PDFs are NOT committed
    /// as test fixtures because:
    ///   a) they contain real PII (buyer names, addresses, prices);
    ///   b) the primary extraction path is text→LLM (non-deterministic);
    ///   c) the benchmark on all 22 docs is the right venue for that.
    ///
    /// LLM EXTRACTION IS INTEGRATION-ONLY:
    ///   Run: PLAYWRIGHT_API_URL=http://localhost:5223
    ///        bun run test:e2e:live -- tests/e2e/live-po-loop.spec.ts
    ///   Or use the live bulk runner: tools/edge-case-orders/run-live.mjs
    /// </summary>
    [Fact]
    public void Pdf_TextExtractor_ExtractsNonEmpty_ForWellFormedTextPdf()
    {
        var bytes = CreatePdfBytes(
            "PO Number: PO-RW-TEST",
            "Order Date: 2026-06-08",
            "Buyer: Test Buyer Ltd",
            "Currency: EUR",
            "1 BUY-001 Widget Type A 10 EA 12.50",
            "2 BUY-002 Widget Type B 5 EA 8.00");

        var lines = PdfTextExtractor.ExtractLines(bytes);
        lines.Should().NotBeEmpty("a text-layer PDF must yield non-empty extracted lines");
        string.Join(" ", lines).Should().Contain("PO-RW-TEST",
            "the PO number must survive text extraction");
        string.Join(" ", lines).Should().Contain("12.50",
            "numeric prices must survive text extraction");
    }

    [Fact]
    public void Pdf_TextExtractor_EmptyPdfReturnsNonNullResult()
    {
        // A PDF with no pages has no text layer. PdfPig should not crash.
        var bytes = CreateMinimalEmptyPdf();
        var lines = PdfTextExtractor.ExtractLines(bytes);
        lines.Should().NotBeNull("extraction from an empty PDF must return a non-null result");
    }

    [Fact]
    public async Task Pdf_DeterministicRegexParser_ParsesTextLayerPdf_ToCanonicalOrder()
    {
        // The PdfOrderParser (regex fallback) must parse a simple text PDF.
        // NOTE: In production, a real PDF would first go through LLM extraction —
        // this tests only the deterministic FALLBACK path, which runs when no OpenAI
        // key is set or when LLM confidence is too low.
        var bytes = CreatePdfBytes(
            "PO Number: PO-PDF-REGEX",
            "Order Date: 2026-06-08",
            "Buyer: Matrix Buyer OY",
            "Currency: EUR",
            "1 BUY-FRAG-01 FragmentA 3 EA 5.00",
            "2 BUY-FRAG-02 FragmentB 7 EA 2.99");

        var parser = new PdfOrderParser();
        var order = await parser.ParseAsync(new MemoryStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("PO-PDF-REGEX");
        order.Lines.Should().HaveCountGreaterThanOrEqualTo(1,
            "the deterministic regex parser must capture at least one line from a structured PDF");
    }

    // ── PDF builder (no external dependencies) ───────────────────────────────

    /// <summary>
    /// Builds a minimal, PdfPig-readable PDF byte array with a text layer.
    /// Replicates the private CreatePdf helper from PdfOrderParserTests — same raw PDF
    /// construction, no external PDF libraries required.
    /// </summary>
    private static byte[] CreatePdfBytes(params string[] textLines)
    {
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 12 Tf");
        content.AppendLine("72 720 Td");
        foreach (var line in textLines)
        {
            content.Append('(').Append(EscapePdfText(line)).AppendLine(") Tj");
            content.AppendLine("0 -18 Td");
        }
        content.AppendLine("ET");
        var contentText = content.ToString();

        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            string.Create(CultureInfo.InvariantCulture,
                $"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(contentText)} >>\nstream\n{contentText}endstream\nendobj\n"),
        };

        var pdf = new StringBuilder();
        pdf.AppendLine("%PDF-1.4");
        var offsets = new List<int> { 0 };
        foreach (var obj in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(obj);
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.AppendLine("xref");
        pdf.AppendLine("0 6");
        pdf.AppendLine("0000000000 65535 f ");
        for (var i = 1; i <= 5; i++)
            pdf.AppendLine(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n ");
        pdf.AppendLine("trailer");
        pdf.AppendLine("<< /Size 6 /Root 1 0 R >>");
        pdf.AppendLine("startxref");
        pdf.AppendLine(xrefOffset.ToString(CultureInfo.InvariantCulture));
        pdf.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string EscapePdfText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("(", "\\(", StringComparison.Ordinal)
             .Replace(")", "\\)", StringComparison.Ordinal);

    private static byte[] CreateMinimalEmptyPdf()
    {
        const string minimal =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
            "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
            "trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n115\n%%EOF\n";
        return Encoding.ASCII.GetBytes(minimal);
    }
}
