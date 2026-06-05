using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Ocr;

namespace ProcuLink.Infrastructure.Tests.Services.Ocr;

/// <summary>
/// Tests for <see cref="SkiaPdfRasterizer"/> (PDFtoImage + SkiaSharp). Exercises the
/// real native code path — PDFtoImage bundles the platform native assets, so this runs
/// on the CI/dev runner without extra setup. (Debian load is proven separately by a
/// Docker probe; here we prove the API + rendering on the build host.)
/// </summary>
public class SkiaPdfRasterizerTests
{
    private static SkiaPdfRasterizer New() =>
        new(NullLogger<SkiaPdfRasterizer>.Instance);

    [Fact]
    public void RenderPagesPng_TextPdf_ReturnsPngPages()
    {
        var pdf = CreatePdf("PURCHASE ORDER PO-2026-1", "1 ABC Widget 4 PCS 12.50");

        var pages = New().RenderPagesPng(pdf, maxPages: 3);

        pages.Should().NotBeEmpty("a 1-page PDF rasterizes to at least one image");
        pages.Should().HaveCount(1);
        // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
        pages[0].Should().StartWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        pages[0].Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public void RenderPagesPng_EmptyOrInvalidInput_ReturnsEmpty()
    {
        New().RenderPagesPng(Array.Empty<byte>(), 3).Should().BeEmpty();
        New().RenderPagesPng(Encoding.ASCII.GetBytes("not a pdf"), 3).Should().BeEmpty();
        New().RenderPagesPng(CreatePdf("x"), 0).Should().BeEmpty("maxPages 0 → nothing");
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
            content.Append('(').Append(line.Replace("(", "\\(").Replace(")", "\\)")).AppendLine(") Tj");
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
}
