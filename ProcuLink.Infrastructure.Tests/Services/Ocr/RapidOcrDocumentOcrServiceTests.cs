using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Ocr;

namespace ProcuLink.Infrastructure.Tests.Services.Ocr;

/// <summary>
/// Tests for the self-hosted no-egress OCR engine (RapidOcrNet). The enabled test runs
/// the real OCR pipeline (rasterize → PP-OCRv5 via ONNX) on the build host — RapidOcrNet
/// bundles the platform natives + models, so no extra setup is needed. (Debian load is
/// proven separately by a Docker probe; see docs/verification/native-deps.md.)
/// </summary>
public class RapidOcrDocumentOcrServiceTests
{
    private static RapidOcrDocumentOcrService Create(bool enabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NoEgressOcr:Enabled"] = enabled ? "true" : "false",
            })
            .Build();
        return new RapidOcrDocumentOcrService(
            config,
            new SkiaPdfRasterizer(NullLogger<SkiaPdfRasterizer>.Instance),
            NullLogger<RapidOcrDocumentOcrService>.Instance);
    }

    [Fact]
    public async Task NotEnabled_IsUnavailable_AndReturnsEmpty()
    {
        var svc = Create(enabled: false);

        svc.IsAvailable.Should().BeFalse();

        await using var pdf = new MemoryStream(CreatePdf("INVOICE 12345"));
        var text = await svc.ExtractTextAsync(pdf, "application/pdf", CancellationToken.None);

        text.Should().BeEmpty("no models are loaded when the feature is off");
    }

    [Fact]
    public async Task Enabled_OcrsRasterizedPdf_RecognizesPrintedText()
    {
        var svc = Create(enabled: true);
        svc.IsAvailable.Should().BeTrue();

        // Clear, OCR-friendly content; digits are the most reliable to assert on.
        await using var pdf = new MemoryStream(CreatePdf("PURCHASE ORDER 20260012345"));
        var text = await svc.ExtractTextAsync(pdf, "application/pdf", CancellationToken.None);

        text.Should().NotBeNullOrWhiteSpace("the self-hosted OCR must read the rendered text");
        // Allow for minor OCR variance — assert a substantial digit run round-trips.
        text.Should().Contain("2026");
    }

    [Fact]
    public async Task WarmAsync_WhenDisabled_IsNoOp_AndDoesNotThrow()
    {
        var svc = Create(enabled: false);

        // Must complete quickly without loading any models or throwing.
        await svc.WarmAsync(CancellationToken.None);

        svc.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task WarmAsync_WhenEnabled_LoadsModels_IsIdempotent_AndEngineStillUsable()
    {
        var svc = Create(enabled: true);

        // Idempotent: warming twice must not throw or double-load.
        await svc.WarmAsync(CancellationToken.None);
        await svc.WarmAsync(CancellationToken.None);

        // After warm-up the engine is usable and the gate is intact — a real OCR still works.
        await using var pdf = new MemoryStream(CreatePdf("PURCHASE ORDER 20260012345"));
        var text = await svc.ExtractTextAsync(pdf, "application/pdf", CancellationToken.None);

        text.Should().NotBeNullOrWhiteSpace("warming must not break the OCR gate");
        text.Should().Contain("2026");
    }

    // Minimal valid text PDF.
    private static byte[] CreatePdf(params string[] lines)
    {
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 28 Tf");
        content.AppendLine("72 700 Td");
        foreach (var line in lines)
        {
            content.Append('(').Append(line.Replace("(", "\\(").Replace(")", "\\)")).AppendLine(") Tj");
            content.AppendLine("0 -40 Td");
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
}
