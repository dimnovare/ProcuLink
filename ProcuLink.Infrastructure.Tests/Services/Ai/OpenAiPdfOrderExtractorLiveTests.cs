using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Infrastructure.Services.Ai;
using ProcuLink.Infrastructure.Services.Ocr;
using ProcuLink.TestSupport;
using SkiaSharp;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// Opt-in live smoke for <see cref="OpenAiPdfOrderExtractor"/> against the real
/// OpenAI API. No-ops in CI / normal runs (mirrors the repo's live-endpoint test
/// gating): only runs when <c>PROCULINK_LIVE_AI_TESTS=1</c> AND an OpenAI key is
/// present in the environment. Validates the one seam the unit tests can't cover
/// without a fake transport: the strict-schema call + snake_case JSON binding +
/// ValidateAndMap, end to end.
///
/// Run (PowerShell), sourcing the key without printing it, e.g.:
///   $env:PROCULINK_LIVE_AI_TESTS = "1"
///   $env:Ai__OpenAI__ApiKey = "<key>"
///   $env:Ai__OpenAI__ExtractionModel = "gpt-4o-mini"
///   dotnet test ... --filter FullyQualifiedName~OpenAiPdfOrderExtractorLiveTests
/// </summary>
public class OpenAiPdfOrderExtractorLiveTests
{
    private const string ApiKeyVar = "Ai__OpenAI__ApiKey";

    /// <summary>
    /// Real-corpus location, required explicitly. The previous default hard-coded one developer's
    /// Downloads folder, so on every other machine the corpus test returned early — covering
    /// nothing while reporting Passed.
    /// </summary>
    private const string PosDirVar = "PROCULINK_LIVE_AI_POS_DIR";

    private static string PosDir => Environment.GetEnvironmentVariable(PosDirVar)!;

    private static OpenAiPdfOrderExtractor BuildExtractor(IPdfRasterizer? rasterizer = null)
    {
        var apiKey = Environment.GetEnvironmentVariable("Ai__OpenAI__ApiKey")!;
        var model = Environment.GetEnvironmentVariable("Ai__OpenAI__ExtractionModel");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = apiKey,
                ["Ai:OpenAI:ExtractionModel"] = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
            })
            .Build();

        return new OpenAiPdfOrderExtractor(
            config, NullLogger<OpenAiPdfOrderExtractor>.Instance, scopeFactory: null, rasterizer: rasterizer);
    }

    [EnvironmentGatedFact(
        "requires the real OpenAI API",
        LiveTestEnvironment.AiOptIn, ApiKeyVar)]
    public async Task ExtractAsync_RealTextPdf_ProducesStructuredOrder()
    {
        var apiKey = Environment.GetEnvironmentVariable("Ai__OpenAI__ApiKey")!;
        var model = Environment.GetEnvironmentVariable("Ai__OpenAI__ExtractionModel");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = apiKey,
                ["Ai:OpenAI:ExtractionModel"] = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
            })
            .Build();

        var extractor = new OpenAiPdfOrderExtractor(
            config, NullLogger<OpenAiPdfOrderExtractor>.Instance, scopeFactory: null);

        extractor.IsAvailable.Should().BeTrue();

        var pdf = CreatePdf(
            "PURCHASE ORDER",
            "PO Number: PO-2026-008412",
            "Order Date: 2026-05-20",
            "Buyer: Heinrich Industries OU",
            "Currency: EUR",
            "Line  Item Code   Description          Qty  Unit  Unit Price  Amount",
            "1     HEI-PLT-09  Mounting plate 90mm  4    PCS   12.50       50.00",
            "2     HEI-BRK-40  Steel bracket        8    PCS   7.25        58.00");

        await using var stream = new MemoryStream(pdf);
        var result = await extractor.ExtractAsync(stream, "application/pdf", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeTrue("a clear text PO should extract cleanly");
        result.Order.Should().NotBeNull();
        result.Order!.Lines.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Order.PoNumber.Should().Contain("008412");
        result.Order.Currency.Should().Be("EUR");

        // The numbers came straight from the source, so nothing should be flagged.
        result.ReviewLineNumbers.Should().BeEmpty();

        var first = result.Order.Lines[0];
        first.BuyerItemCode.Should().Be("HEI-PLT-09");
        first.Quantity.Should().Be(4m);
        first.UnitPrice.Should().Be(12.50m);
    }

    [EnvironmentGatedFact(
        "requires the real OpenAI API with a vision-capable model",
        LiveTestEnvironment.AiOptIn, ApiKeyVar)]
    public async Task ExtractAsync_ScannedImageOnlyPdf_ExtractsViaVision()
    {
        var apiKey = Environment.GetEnvironmentVariable("Ai__OpenAI__ApiKey")!;
        var model = Environment.GetEnvironmentVariable("Ai__OpenAI__ExtractionModel");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = apiKey,
                ["Ai:OpenAI:ExtractionModel"] = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model, // vision-capable
            })
            .Build();

        // Build a real PO as TEXT, then bake it into an IMAGE-ONLY PDF (no text layer)
        // so PdfPig finds nothing and the extractor must use the vision path.
        var textPdf = CreatePdf(
            "PURCHASE ORDER",
            "PO Number: PO-2026-008412",
            "Buyer: Heinrich Industries OU",
            "Currency: EUR",
            "Line  Item Code   Description          Qty  Unit  Unit Price",
            "1     HEI-PLT-09  Mounting plate 90mm  4    PCS   12.50",
            "2     HEI-BRK-40  Steel bracket        8    PCS   7.25");

        var rasterizer = new SkiaPdfRasterizer(NullLogger<SkiaPdfRasterizer>.Instance);
        var png = rasterizer.RenderPagesPng(textPdf, 1).Single();
        using var bmp = SKBitmap.Decode(png);
        using var jpeg = bmp.Encode(SKEncodedImageFormat.Jpeg, 90);
        var imageOnlyPdf = CreateImageOnlyPdf(jpeg.ToArray(), bmp.Width, bmp.Height);

        var extractor = new OpenAiPdfOrderExtractor(
            config, NullLogger<OpenAiPdfOrderExtractor>.Instance, scopeFactory: null, rasterizer: rasterizer);

        await using var stream = new MemoryStream(imageOnlyPdf);
        var result = await extractor.ExtractAsync(stream, "application/pdf", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeTrue("the vision model should read the rendered PO");
        result.Order!.Lines.Should().NotBeEmpty();
        // Vision extraction has no text layer to verify against → every line flagged for review.
        result.ReviewLineNumbers.Should().HaveCount(result.Order.Lines.Count);
    }

    // ── Party-role assignment (the founder-found buyer/supplier swap bug) ─────

    /// <summary>
    /// Anonymised, real-shaped POs that reproduce the exact ambiguity the founder hit:
    /// the system-customer-like name ("Markit") appears as the SUPPLIER/recipient, while
    /// a DIFFERENT organisation is the BUYER/issuer. The extractor must assign roles from
    /// the document labels — NOT pick the familiar name as the buyer. Verifies, end to end
    /// against the real OpenAI call, the fix in <see cref="OpenAiPdfOrderExtractor"/>'s
    /// system prompt.
    /// </summary>
    public static IEnumerable<object[]> PartyRoleFixtures()
    {
        // DK shape, "Supplier:" label: the buyer/issuer is a third org; Markit is the
        // supplier/recipient.
        yield return new object[]
        {
            "Exemplar Valves",  // expected buyer (issuer)
            "Markit",   // expected supplier (the system-customer-like recipient)
            CreatePdf(
                "Exemplar Valves A/S",
                "Teststadvej 81, 6430 Teststad, Denmark",
                "PURCHASE ORDER",
                "PO Number: 4500123987",
                "Order Date: 2026-04-14",
                "Currency: EUR",
                "Supplier:",
                "REDACTED-PARTY",
                "Tallinn, Estonia",
                "Line  Item Code   Description           Qty  Unit  Unit Price  Amount",
                "1     DF-VLT-110  Frequency converter   3    PCS   240.00      720.00",
                "2     DF-SEN-022  Pressure sensor       10   PCS   18.50       185.00")
        };

        // AT shape, German "Bestellung" + "Vendor / Supplier:" label: the buyer/issuer is a
        // third org; Markit is the supplier.
        yield return new object[]
        {
            "Exemplar",
            "Markit",
            CreatePdf(
                "Exemplar AG",
                "Exemplar-Strasse 1, 4020 Teststadt, Austria",
                "Bestellung / PURCHASE ORDER",
                "PO Number: VA-2026-77120",
                "Order Date: 2026-03-02",
                "Currency: EUR",
                "Vendor / Supplier:",
                "REDACTED-PARTY",
                "Tallinn, Estonia",
                "Line  Item Code   Description           Qty  Unit  Unit Price  Amount",
                "1     VA-STL-450  Steel coil            5    PCS   1250.00     6250.00",
                "2     VA-BLT-012  Bolt set              40   PCS   3.20        128.00")
        };

        // CH control case, "Sold to / Supplier:" label: the buyer/issuer is a third org; Markit
        // is the supplier.
        yield return new object[]
        {
            "Exemplar Drives",
            "Markit",
            CreatePdf(
                "Exemplar Drives Ltd",
                "Teststrasse 44, 8050 Teststadt, Switzerland",
                "PURCHASE ORDER",
                "PO Number: EXD-PO-558204",
                "Order Date: 2026-05-09",
                "Currency: EUR",
                "Sold to / Supplier:",
                "REDACTED-PARTY",
                "Tallinn, Estonia",
                "Line  Item Code   Description           Qty  Unit  Unit Price  Amount",
                "1     EXD-DRV-30  Motor drive            2    PCS   980.00      1960.00",
                "2     EXD-CON-08  Contactor             15    PCS   24.00       360.00")
        };
    }

    [EnvironmentGatedTheory(
        "requires the real OpenAI API",
        LiveTestEnvironment.AiOptIn, ApiKeyVar)]
    [MemberData(nameof(PartyRoleFixtures))]
    public async Task ExtractAsync_PurchaseOrder_AssignsBuyerFromLabels_NotFromFamiliarName(
        string expectedBuyer, string expectedSupplier, byte[] pdf)
    {
        var extractor = BuildExtractor();
        extractor.IsAvailable.Should().BeTrue();

        var sourceText = await ProcuLink.Transform.Parsing.PdfTextExtractor
            .ExtractTextAsync(pdf, CancellationToken.None);

        await using var stream = new MemoryStream(pdf);
        var result = await extractor.ExtractAsync(stream, "application/pdf", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeTrue("a clear text PO should extract cleanly");
        result.Order.Should().NotBeNull();

        // Correct line count.
        result.Order!.Lines.Should().HaveCount(2);

        // Every emitted number must appear verbatim in the source text.
        foreach (var line in result.Order.Lines)
        {
            if (line.Quantity != 0m)
                NumberAppearsInSource(line.Quantity, sourceText).Should().BeTrue(
                    "quantity {0} must be printed in the source", line.Quantity);
            if (line.UnitPrice is { } up && up != 0m)
                NumberAppearsInSource(up, sourceText).Should().BeTrue(
                    "unit price {0} must be printed in the source", up);
            if (line.LineAmount is { } la && la != 0m)
                NumberAppearsInSource(la, sourceText).Should().BeTrue(
                    "line amount {0} must be printed in the source", la);
        }

        // The two parties must be DIFFERENT.
        result.Order.BuyerName.Should().NotBeNullOrWhiteSpace();
        result.Order.SupplierName.Should().NotBeNullOrWhiteSpace();
        result.Order.BuyerName.Should().NotBe(result.Order.SupplierName);

        // The correct buyer for this fixture — assigned from the labels, NOT the familiar
        // system-customer name (which is the SUPPLIER here).
        result.Order.BuyerName!.Should().ContainEquivalentOf(expectedBuyer,
            "buyer must come from the document's issuer/header, not the familiar recipient name");
        result.Order.SupplierName!.Should().ContainEquivalentOf(expectedSupplier);
        // The system-customer-like name must NOT have leaked into the buyer slot.
        result.Order.BuyerName.Should().NotContainEquivalentOf(expectedSupplier,
            "the recipient/supplier name must never be assigned as the buyer");
    }

    /// <summary>
    /// Iterates a real local PO corpus (<c>PROCULINK_LIVE_AI_POS_DIR</c>). For each PDF it asserts
    /// the universal invariants the swap bug violated: at least one line, every emitted number
    /// verbatim in the source, and buyer != supplier. (The correct-buyer-per-document assertion
    /// lives in the synthetic theory above, which knows the ground truth.)
    ///
    /// <para>The corpus directory is part of the DECLARED gate: an unset or non-existent path is a
    /// reported skip, and a directory with no PDFs in it is a FAILURE — you asked for a corpus run
    /// and handed the test nothing to run on.</para>
    /// </summary>
    [EnvironmentGatedFact(
        "requires the real OpenAI API and a local corpus of real purchase-order PDFs",
        LiveTestEnvironment.AiOptIn, ApiKeyVar, LiveTestEnvironment.DirectoryPrefix + PosDirVar)]
    public async Task ExtractAsync_RealCorpus_BuyerDiffersFromSupplier_AndNumbersAreVerbatim()
    {
        var pdfs = Directory.EnumerateFiles(PosDir, "*.pdf", SearchOption.TopDirectoryOnly).ToList();
        pdfs.Should().NotBeEmpty($"{PosDirVar} ('{PosDir}') must contain at least one PDF to iterate");

        var extractor = BuildExtractor(new SkiaPdfRasterizer(NullLogger<SkiaPdfRasterizer>.Instance));

        foreach (var path in pdfs)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var sourceText = await ProcuLink.Transform.Parsing.PdfTextExtractor
                .ExtractTextAsync(bytes, CancellationToken.None);

            await using var stream = new MemoryStream(bytes);
            var result = await extractor.ExtractAsync(stream, "application/pdf", Guid.NewGuid(), CancellationToken.None);

            if (!result.Success || result.Order is null)
                continue; // skip docs the extractor declined (low confidence / no lines / scanned-illegible)

            var fileName = Path.GetFileName(path);
            result.Order.Lines.Should().NotBeEmpty($"{fileName} should yield at least one line");

            result.Order.BuyerName.Should().NotBeNullOrWhiteSpace($"{fileName} buyer must be set");
            result.Order.SupplierName.Should().NotBeNullOrWhiteSpace($"{fileName} supplier must be set");
            result.Order.BuyerName.Should().NotBe(result.Order.SupplierName,
                $"{fileName}: buyer and supplier must be two different parties");

            // Numbers verbatim — only when there is a text layer to verify against (scanned
            // docs route through vision, which has no source text and flags every line anyway).
            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                foreach (var line in result.Order.Lines)
                {
                    if (line.Quantity != 0m)
                        NumberAppearsInSource(line.Quantity, sourceText).Should().BeTrue(
                            $"{fileName}: quantity {line.Quantity} must appear in the source");
                    if (line.UnitPrice is { } up && up != 0m)
                        NumberAppearsInSource(up, sourceText).Should().BeTrue(
                            $"{fileName}: unit price {up} must appear in the source");
                }
            }
        }
    }

    // Lenient "does this number appear in the printed source" check for the live assertions.
    // Mirrors the extractor's own anti-hallucination intent (EU/Anglo separators, space-grouped
    // thousands) without reaching into its private matcher.
    private static bool NumberAppearsInSource(decimal value, string sourceText)
    {
        if (string.IsNullOrEmpty(sourceText)) return false;
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);

        // Collapse inter-digit spaces/NBSP so "1 250,00" reads as one token too.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(sourceText, @"(?<=\d)[\x20 ](?=\d)", "");

        foreach (var text in new[] { sourceText, collapsed })
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, @"\d{1,3}(?:[.,]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?"))
        {
            foreach (var candidate in NumberReadings(m.Value))
            {
                if (Math.Round(candidate, 4, MidpointRounding.AwayFromZero) == rounded)
                    return true;
            }
        }
        return false;
    }

    // All plausible decimal readings of a printed numeric token (EU vs Anglo separators).
    private static IEnumerable<decimal> NumberReadings(string token)
    {
        var t = token.Trim();
        if (t.Length == 0) yield break;

        var dots = t.Count(c => c == '.');
        var commas = t.Count(c => c == ',');

        if (dots > 0 && commas > 0)
        {
            var decimalSep = t.LastIndexOf('.') > t.LastIndexOf(',') ? '.' : ',';
            var thousandsSep = decimalSep == '.' ? ',' : '.';
            var normalized = t.Replace(thousandsSep.ToString(), string.Empty).Replace(decimalSep, '.');
            if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) yield return v;
            yield break;
        }

        // Single separator (or none): yield both the grouped-thousands and the decimal reading.
        var stripped = t.Replace(".", string.Empty).Replace(",", string.Empty);
        if (decimal.TryParse(stripped, NumberStyles.Number, CultureInfo.InvariantCulture, out var asGrouped))
            yield return asGrouped;
        var asDecimal = t.Replace(',', '.');
        if (asDecimal.Count(c => c == '.') == 1
            && decimal.TryParse(asDecimal, NumberStyles.Number, CultureInfo.InvariantCulture, out var v2))
            yield return v2;
    }

    // Embeds a JPEG as the sole content of a 1-page PDF (no text layer) — a synthetic
    // "scanned" document for exercising the vision path.
    private static byte[] CreateImageOnlyPdf(byte[] jpeg, int w, int h)
    {
        var body = new List<byte>();
        var offsets = new int[6];
        void Ascii(string s) => body.AddRange(Encoding.ASCII.GetBytes(s));

        Ascii("%PDF-1.4\n");
        offsets[1] = body.Count; Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = body.Count; Ascii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = body.Count; Ascii(string.Create(CultureInfo.InvariantCulture,
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n"));
        var content = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q"));
        offsets[4] = body.Count;
        Ascii(string.Create(CultureInfo.InvariantCulture, $"4 0 obj\n<< /Length {content.Length} >>\nstream\n"));
        body.AddRange(content); Ascii("\nendstream\nendobj\n");
        offsets[5] = body.Count;
        Ascii(string.Create(CultureInfo.InvariantCulture,
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"));
        body.AddRange(jpeg); Ascii("\nendstream\nendobj\n");

        var xref = body.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        Ascii(sb.ToString());
        return body.ToArray();
    }

    // Minimal valid text PDF (mirrors ProcuLink.Transform.Tests.PdfOrderParserTests).
    private static byte[] CreatePdf(params string[] lines)
    {
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 12 Tf");
        content.AppendLine("72 720 Td");
        foreach (var line in lines)
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
            string.Create(CultureInfo.InvariantCulture, $"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(contentText)} >>\nstream\n{contentText}endstream\nendobj\n"),
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
