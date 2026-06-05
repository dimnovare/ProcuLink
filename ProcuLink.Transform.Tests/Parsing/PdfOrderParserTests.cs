using System.Globalization;
using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class PdfOrderParserTests
{
    [Fact]
    public void CanParse_ReturnsTrueForPdfExtension()
    {
        var parser = new PdfOrderParser();

        parser.CanParse(".pdf").Should().BeTrue();
        parser.CanParse(".PDF").Should().BeTrue();
        parser.CanParse(".csv").Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_TextPurchaseOrderPdf_ReturnsParsedOrder()
    {
        var parser = new PdfOrderParser();
        await using var stream = new MemoryStream(CreatePdf(
            "PO Number: PO-2026-008412",
            "Order Date: 2026-05-20",
            "Buyer: Heinrich Industries",
            "Currency: EUR",
            "Line BuyerItemCode Description Quantity Unit UnitPrice",
            "1 HEI-PLT-09 Mounting plate 90mm 4 PCS 12.50",
            "2 HEI-BRK-40 Steel bracket 8 PCS 7.25"));

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("PO-2026-008412");
        result.OrderDate.Should().Be(new DateTime(2026, 5, 20));
        result.BuyerName.Should().Be("Heinrich Industries");
        result.Currency.Should().Be("EUR");

        result.Lines.Should().HaveCount(2);
        result.Lines[0].LineNumber.Should().Be(1);
        result.Lines[0].BuyerItemCode.Should().Be("HEI-PLT-09");
        result.Lines[0].Description.Should().Be("Mounting plate 90mm");
        result.Lines[0].Quantity.Should().Be(4m);
        result.Lines[0].Unit.Should().Be("PCS");
        result.Lines[0].UnitPrice.Should().Be(12.50m);
    }

    [Fact]
    public async Task ParseAsync_TextWithoutLineRows_ReturnsHeaderAndEmptyLines()
    {
        var parser = new PdfOrderParser();
        await using var stream = new MemoryStream(CreatePdf(
            "PO Number: PO-EMPTY",
            "Currency: EUR",
            "Thank you for your order."));

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("PO-EMPTY");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public void PdfTextExtractor_ExtractText_ReturnsTextLayerInReadingOrder()
    {
        var bytes = CreatePdf(
            "PO Number: PO-2026-008412",
            "1 HEI-PLT-09 Mounting plate 90mm 4 PCS 12.50");

        var text = PdfTextExtractor.ExtractText(bytes);

        text.Should().Contain("PO-2026-008412");
        text.Should().Contain("HEI-PLT-09");
        text.Should().Contain("12.50");
    }

    [Fact]
    public void PdfTextExtractor_ExtractLines_DropsBlankLines()
    {
        var bytes = CreatePdf("Currency: EUR", "1 ABC Widget 2 PCS 5.00");

        var lines = PdfTextExtractor.ExtractLines(bytes);

        lines.Should().NotBeEmpty();
        lines.Should().OnlyContain(l => !string.IsNullOrWhiteSpace(l));
    }

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
            string.Create(CultureInfo.InvariantCulture, $"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(contentText)} >>\nstream\n{contentText}endstream\nendobj\n")
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
