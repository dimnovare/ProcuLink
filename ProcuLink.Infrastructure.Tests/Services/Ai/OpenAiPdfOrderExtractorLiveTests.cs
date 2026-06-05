using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Ai;

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
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("PROCULINK_LIVE_AI_TESTS") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Ai__OpenAI__ApiKey"));

    [Fact]
    public async Task ExtractAsync_RealTextPdf_ProducesStructuredOrder()
    {
        if (!Enabled) return; // no-op unless explicitly enabled with a key

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
