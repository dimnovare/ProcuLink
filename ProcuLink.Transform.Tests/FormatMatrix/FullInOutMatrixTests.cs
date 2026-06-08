using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using static ProcuLink.Transform.Tests.FormatMatrix.FormatFixtures;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// The FULL deterministic IN×OUT matrix: EVERY inbound parser × EVERY entity-based
/// outbound transform, run hermetically with no network and no OpenAI key.
///
/// This complements the existing matrices, which were narrower:
///   • <see cref="InOutMatrixTheoryTests"/> — 7 IN × 3 OUT, but only the
///     ParsedOrder-family outputs (UblOrder / X12_850 / EdifactOrders).
///   • <see cref="OutCoverageMatrixTests"/> — every OUT transform, but each
///     against a SINGLE hand-built canonical order, not crossed with every IN.
///
/// THIS suite crosses the two: each inbound parser parses a representative fixture
/// into the canonical <see cref="ParsedOrder"/>, the parsed order is projected to a
/// fully-resolved <see cref="PurchaseOrderEntity"/> (every line given a non-null
/// SupplierItemCode + NeedsReview=false so the delivery-pipeline transforms accept it),
/// and then EVERY entity-based outbound transform is run against it. Each combo asserts
/// the output is NON-EMPTY and STRUCTURALLY VALID for that format:
///   • Xml / CXml / Ubl  → well-formed XML (XDocument.Parse) with the expected root.
///   • Json              → parses (JsonDocument.Parse) with poNumber + lines.
///   • X12               → ISA / ST*850 / BEG / SE / IEA mandatory segments present.
///   • Csv               → header line + ≥1 data row.
///   • MappedCSV         → override-driven header columns + ≥1 data row.
///
/// IN  parsers:  CSV, XLSX, UBL, cXML, IDoc ORDERS05, EDIFACT, X12, PDF-text   (8)
/// OUT transforms: Csv, Xml, Json, CXml, Ubl, X12, MappedCSV                   (7)
/// = 56 deterministic combinations.
///
/// PDF is INCLUDED here via the deterministic text-layer path only: a text-layer
/// PDF is built in-memory and parsed by <see cref="PdfOrderParser"/> (the regex
/// fallback that runs when no OpenAI key is configured). The non-deterministic
/// text→LLM / vision paths are NOT exercised (see PdfMatrixPlaceholderTests).
///
/// Known-invalid combos are marked with a Skip reason rather than asserting false.
/// (Currently none: every entity-based transform accepts a fully-resolved order.)
/// A guard <see cref="Matrix_HasNoUnexpectedFailures"/> runs the WHOLE matrix
/// in-process, captures per-combo pass/fail, and FAILS LOUDLY naming exactly which
/// IN×OUT combinations broke and why — these are the real gotchas the task hunts for.
/// </summary>
public class FullInOutMatrixTests
{
    // ── The 8 inbound parsers and the representative fixture each one parses. ──
    private static readonly string[] InFormats =
        { "csv", "xlsx", "ubl", "cxml", "idoc", "edifact", "x12", "pdf" };

    // ── The 7 entity-based outbound transforms under test. ────────────────────
    // MappedCsv is a synthetic label routed to MappedTransformService(OutputFormat.Csv).
    private static readonly string[] OutFormats =
        { "Csv", "Xml", "Json", "CXml", "Ubl", "X12", "MappedCsv" };

    // The [Theory] data EXCLUDES known-invalid combos (SkipReasonFor != null), which are
    // accounted for in Matrix_HasNoUnexpectedFailures instead. Currently none are excluded.
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var inf in InFormats)
            foreach (var outf in OutFormats)
                if (SkipReasonFor(inf, outf) is null)
                    yield return new object[] { inf, outf };
    }

    // The full grid (including any known-invalid combos) — used only by the coverage guard.
    public static IEnumerable<object[]> FullGrid()
    {
        foreach (var inf in InFormats)
            foreach (var outf in OutFormats)
                yield return new object[] { inf, outf };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Inbound: build a fixture, parse it, project to a resolved entity.
    // ════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<Line> Lines() => RepresentativeLines();

    /// <summary>
    /// Parse the representative fixture for <paramref name="inFormat"/> into the
    /// canonical <see cref="ParsedOrder"/> using the format's native parser.
    /// </summary>
    private static async Task<ParsedOrder> ParseInbound(string inFormat)
    {
        var lines = Lines();
        return inFormat switch
        {
            "csv"     => await Parse(new CsvOrderParser(),      Csv("PO-FX-CSV", "EUR", "Acme Buyer Ltd", lines)),
            "xlsx"    => await Parse(new XlsxOrderParser(),     Xlsx("PO-FX-XLS", "EUR", "Acme Buyer Ltd", lines)),
            "ubl"     => await Parse(new UblOrderParser(),      Ubl("PO-FX-UBL", "EUR", "Acme Buyer Ltd", lines)),
            "cxml"    => await Parse(new CxmlOrderParser(),     Cxml("PO-FX-CX", "EUR", lines)),
            "idoc"    => await Parse(new IDocOrders05Parser(),  Idoc("PO-FX-IDOC", "Acme Buyer Ltd", lines)),
            "edifact" => await Parse(new EdifactOrderParser(),  Edifact("PO-FX-EDI", "EUR", "Acme Buyer Ltd", lines)),
            "x12"     => await Parse(new X12OrderParser(),      X12("PO-FX-X12", "EUR", "Acme Buyer Ltd", lines)),
            "pdf"     => await Parse(new PdfOrderParser(),      TextPdf("PO-FX-PDF", lines)),
            _ => throw new ArgumentOutOfRangeException(nameof(inFormat), inFormat, null),
        };

        static async Task<ParsedOrder> Parse(IPurchaseOrderParser parser, byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            return await parser.ParseAsync(stream, CancellationToken.None);
        }
    }

    /// <summary>
    /// Project a canonical <see cref="ParsedOrder"/> to a fully-RESOLVED
    /// <see cref="PurchaseOrderEntity"/>: every line gets a synthesised
    /// SupplierItemCode and NeedsReview=false, so the delivery-pipeline transforms
    /// (which refuse to serialize an unresolved order) accept it. This is exactly
    /// what the resolve step does in production before transform/deliver.
    /// </summary>
    private static PurchaseOrderEntity ToResolvedEntity(ParsedOrder parsed)
    {
        var entity = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = parsed.PoNumber ?? "PO-UNKNOWN",
            BuyerName  = parsed.BuyerName,
            // Some formats (e.g. cXML) carry no buyer-side OrderDate; default to today.
            OrderDate  = parsed.OrderDate is { } d ? DateOnly.FromDateTime(d) : DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = string.IsNullOrWhiteSpace(parsed.Currency) ? "EUR" : parsed.Currency!,
            Status     = "ready",
            Supplier   = new Supplier { Name = parsed.SupplierName ?? "Matrix Supplier OY" },
            Lines      = parsed.Lines
                .OrderBy(l => l.LineNumber)
                .Select((l, i) => new PurchaseOrderLineEntity
                {
                    LineNumber       = l.LineNumber,
                    BuyerItemCode    = l.BuyerItemCode,
                    // Synthesised resolution: maps BUY-001 → SUP-001 deterministically.
                    SupplierItemCode = $"SUP-{i + 1:000}",
                    Description      = l.Description,
                    Quantity         = l.Quantity,
                    Unit             = l.Unit,
                    UnitPrice        = l.UnitPrice ?? 0m,
                    NeedsReview      = false,
                    Confidence       = 1.0f,
                })
                .ToList(),
        };
        return entity;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Outbound: run one transform, return its text + a structural-validity check.
    // ════════════════════════════════════════════════════════════════════════

    private static readonly OrderMappingOverride MappedCsvOverride = new()
    {
        Output = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
            Lines  =
            {
                ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" },
                ["qty"]  = new OutputFieldRule { OutputPath = "Qty",      CanonicalField = "Quantity" },
            },
        },
    };

    /// <summary>Run the named outbound transform against the resolved entity, returning the document text.</summary>
    private static async Task<string> RunOutbound(string outFormat, PurchaseOrderEntity entity)
    {
        switch (outFormat)
        {
            case "Csv":
                return ReadAll(await new CsvTransformService().TransformAsync(entity, OutputFormat.Csv, CancellationToken.None));
            case "Xml":
                return ReadAll(await new XmlTransformService().TransformAsync(entity, OutputFormat.Xml, CancellationToken.None));
            case "Json":
                return ReadAll(await new JsonTransformService().TransformAsync(entity, OutputFormat.Json, CancellationToken.None));
            case "CXml":
                return ReadAll(await new CxmlTransformService().TransformAsync(entity, OutputFormat.CXml, CancellationToken.None));
            case "Ubl":
                return ReadAll(await new UblOrderTransformService().TransformAsync(entity, OutputFormat.Ubl, CancellationToken.None));
            case "X12":
                return ReadAll(await new X12TransformService().TransformAsync(entity, OutputFormat.X12, CancellationToken.None));
            case "MappedCsv":
                return ReadAll(new MappedTransformService().Build(entity, MappedCsvOverride, OutputFormat.Csv));
            default:
                throw new ArgumentOutOfRangeException(nameof(outFormat), outFormat, null);
        }
    }

    /// <summary>
    /// Assert the document text is non-empty AND structurally valid for the format.
    /// Throws (with a descriptive message) on any structural defect.
    /// </summary>
    private static void AssertStructurallyValid(string outFormat, string text, string poNumber)
    {
        text.Should().NotBeNullOrWhiteSpace($"{outFormat} output must be non-empty");

        switch (outFormat)
        {
            case "Xml":
            {
                var doc = XDocument.Parse(text); // throws XmlException if not well-formed
                doc.Root!.Name.LocalName.Should().Be("PurchaseOrder");
                doc.Descendants().Where(e => e.Name.LocalName == "Line").Should().NotBeEmpty();
                break;
            }
            case "CXml":
            {
                var doc = XDocument.Parse(text);
                doc.Root!.Name.LocalName.Should().Be("cXML");
                break;
            }
            case "Ubl":
            {
                var doc = XDocument.Parse(text);
                doc.Root!.Name.LocalName.Should().Be("Order");
                break;
            }
            case "Json":
            {
                using var json = JsonDocument.Parse(text); // throws on invalid JSON
                json.RootElement.GetProperty("poNumber").GetString().Should().Be(poNumber);
                json.RootElement.GetProperty("lines").GetArrayLength().Should().BeGreaterThan(0);
                break;
            }
            case "X12":
            {
                var segs = text.Split('~', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim('\r', '\n', ' ', '\t'))
                    .Where(s => s.Length > 0)
                    .ToList();
                segs.Should().NotBeEmpty();
                segs[0].Should().StartWith("ISA*", "X12 must open with the ISA interchange header");
                segs.Should().Contain(s => s.StartsWith("ST*850"), "X12 850 transaction set header is mandatory");
                segs.Should().Contain(s => s.StartsWith("BEG*"), "X12 BEG beginning segment is mandatory");
                segs.Should().Contain(s => s.StartsWith("SE*"), "X12 SE transaction-set trailer is mandatory");
                segs.Should().Contain(s => s.StartsWith("IEA*"), "X12 IEA interchange trailer is mandatory");
                break;
            }
            case "Csv":
            {
                var rows = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                rows.Should().HaveCountGreaterThanOrEqualTo(2, "CSV must have a header row + ≥1 data row");
                rows[0].Should().StartWith("SupplierItemCode,", "fixed CSV header is SupplierItemCode,…");
                break;
            }
            case "MappedCsv":
            {
                var rows = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                rows.Should().HaveCountGreaterThanOrEqualTo(2, "MappedCSV must have a header row + ≥1 data row");
                rows[0].Should().Be("OrderRef,ItemCode,Qty", "MappedCSV header columns come from the override OutputPaths");
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(outFormat), outFormat, null);
        }
    }

    private static string ReadAll(TransformResult r)
    {
        r.Content.Position = 0;
        using var sr = new StreamReader(r.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024, leaveOpen: true);
        return sr.ReadToEnd();
    }

    /// <summary>Combos that are genuinely invalid → Skip with a reason, never assert false.</summary>
    private static string? SkipReasonFor(string inFormat, string outFormat) => null;

    // ════════════════════════════════════════════════════════════════════════
    // The matrix [Theory] — one xUnit case per IN×OUT combination.
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ParseThenTransform_ProducesStructurallyValidOutput(string inFormat, string outFormat)
    {
        var parsed = await ParseInbound(inFormat);

        // Sanity on the inbound leg so an OUT failure is never masked by a bad IN parse.
        parsed.Lines.Should().HaveCount(2, $"in={inFormat} must parse both representative lines");

        var entity = ToResolvedEntity(parsed);
        var text   = await RunOutbound(outFormat, entity);

        AssertStructurallyValid(outFormat, text, entity.PoNumber);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Coverage guard — the matrix is the full 8×7 grid, not a quietly-shrunk subset.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Matrix_Covers_AllEightInputs_TimesSevenOutputs()
    {
        FullGrid().Should().HaveCount(56);
        FullGrid().Select(o => (string)o[0]).Distinct().Should().HaveCount(8);
        FullGrid().Select(o => (string)o[1]).Distinct().Should().HaveCount(7);

        // The executable [Theory] grid = full grid minus any known-invalid (skipped) combos.
        var skipped = FullGrid().Count(o => SkipReasonFor((string)o[0], (string)o[1]) is not null);
        Matrix().Should().HaveCount(56 - skipped);
    }

    // ════════════════════════════════════════════════════════════════════════
    // The "report" guard — run the WHOLE matrix in-process, capture per-combo
    // pass/fail, and fail loudly naming EXACTLY which IN×OUT combos broke and why.
    //
    // This is the deliverable: the failure list IS the gotcha report. A combo that
    // is known-invalid is recorded by SkipReasonFor (and excluded), so the only
    // failures that surface here are UNEXPECTED breakages.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Matrix_HasNoUnexpectedFailures()
    {
        var failures = new List<string>();
        var ran      = 0;
        var skipped  = 0;

        foreach (var inFormat in InFormats)
        {
            ParsedOrder parsed;
            try
            {
                parsed = await ParseInbound(inFormat);
            }
            catch (Exception ex)
            {
                // An inbound parse failure breaks the whole row — record it once.
                foreach (var outFormat in OutFormats)
                    failures.Add($"{inFormat,-8} -> {outFormat,-10} : INBOUND PARSE FAILED — {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var entity = ToResolvedEntity(parsed);

            foreach (var outFormat in OutFormats)
            {
                if (SkipReasonFor(inFormat, outFormat) is not null) { skipped++; continue; }

                ran++;
                try
                {
                    var text = await RunOutbound(outFormat, entity);
                    AssertStructurallyValid(outFormat, text, entity.PoNumber);
                }
                catch (Exception ex)
                {
                    failures.Add($"{inFormat,-8} -> {outFormat,-10} : {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Always surface the run accounting so the report is legible even on success.
        ran.Should().BeGreaterThan(0, "the matrix must execute at least one combo");

        failures.Should().BeEmpty(
            $"all {ran} executed IN×OUT combos ({skipped} skipped) must produce structurally valid output. " +
            $"FAILING COMBINATIONS:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Deterministic text-layer PDF builder (no OpenAI, no external PDF library).
    // Mirrors the raw-PDF construction used by RealWorldFixtureTests / PdfOrderParserTests
    // so the regex PdfOrderParser fallback can read it. The line layout matches the
    // fixed-column shape PdfOrderParser expects: "<n> <code> <desc> <qty> <unit> <price>".
    // ════════════════════════════════════════════════════════════════════════

    private static byte[] TextPdf(string poNumber, IReadOnlyList<Line> lines)
    {
        var textLines = new List<string>
        {
            $"PO Number: {poNumber}",
            "Order Date: 2026-06-08",
            "Buyer: Acme Buyer Ltd",
            "Currency: EUR",
        };
        foreach (var l in lines)
        {
            textLines.Add(string.Join(" ",
                l.LineNumber.ToString(CultureInfo.InvariantCulture),
                l.Code,
                (l.Description ?? "Item").Replace(" ", "_"),
                l.Quantity.ToString(CultureInfo.InvariantCulture),
                l.Unit ?? "EA",
                (l.UnitPrice ?? 0m).ToString(CultureInfo.InvariantCulture)));
        }

        return CreatePdfBytes(textLines.ToArray());
    }

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
}
