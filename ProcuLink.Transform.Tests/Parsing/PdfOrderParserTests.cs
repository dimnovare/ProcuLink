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

    [Fact]
    public async Task PdfTextExtractor_ExtractLinesAsync_MatchesSyncResult()
    {
        var bytes = CreatePdf(
            "PO Number: PO-2026-008412",
            "1 HEI-PLT-09 Mounting plate 90mm 4 PCS 12.50");

        var sync  = PdfTextExtractor.ExtractLines(bytes);
        var async = await PdfTextExtractor.ExtractLinesAsync(bytes, CancellationToken.None);

        async.Should().BeEquivalentTo(sync, o => o.WithStrictOrdering(),
            "the timeout-bounded overload must produce the same lines as the synchronous one");
    }

    [Fact]
    public async Task PdfTextExtractor_ExtractLinesAsync_AlreadyCancelledToken_Throws()
    {
        var bytes = CreatePdf("PO Number: PO-CANCEL", "1 ABC Widget 2 PCS 5.00");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await PdfTextExtractor.ExtractLinesAsync(bytes, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "an already-cancelled token must fail fast rather than parse");
    }

    // ── B3: locale-aware decimal parsing (shared NumberParsing reader) ───────────
    // The deterministic PDF text fallback previously read numbers with the naive
    // "swap ','→'.' only when no '.' present" reader, which never flagged the
    // silent-corruption class. It now delegates to NumberParsing.TryParseFlexibleDecimal
    // (european: false) so EU comma-decimals read correctly and ambiguous tokens flag
    // NeedsReview, while invariant numbers stay byte-identical.
    //
    // NOTE: the line-column regex captures a single separator group
    // (-?\d+(?:[.,]\d+)?), so a grouped EU price like "1.234,56" never matches a PDF
    // line row in the first place (two separators) — only single-separator tokens
    // ("12.50", "73,22") reach ParseDecimal here. We therefore exercise the reachable
    // tokens: the EU comma-decimal must never become 100×, and invariant numbers are
    // unchanged.

    private static async Task<ParsedOrderLine> ParsePdfLineAsync(string qty, string price)
    {
        var parser = new PdfOrderParser();
        await using var stream = new MemoryStream(CreatePdf(
            "PO Number: PO-DEC-1",
            "Currency: EUR",
            $"1 ITEM-1 Widget {qty} PCS {price}"));
        var result = await parser.ParseAsync(stream, CancellationToken.None);
        result.Lines.Should().ContainSingle();
        return result.Lines[0];
    }

    [Fact]
    public async Task ParseAsync_EuCommaDecimalPrice_IsNeverReadAs100x()
    {
        // "73,22" must never become 7322 (a comma is the decimal here, not a thousands group).
        var line = await ParsePdfLineAsync(qty: "2", price: "73,22");

        line.UnitPrice.Should().NotBe(7322m, "a comma must never be treated as a thousands group here");
        (line.UnitPrice == 73.22m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_EuCommaDecimalQuantity_IsNeverReadAs100x()
    {
        // Quantity "73,22" (e.g. 73.22 kg) must never become 7322.
        var line = await ParsePdfLineAsync(qty: "73,22", price: "10.00");

        line.Quantity.Should().NotBe(7322m, "a comma must never be treated as a thousands group here");
        (line.Quantity == 73.22m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_InvariantPrices_AreByteUnchangedAndNotFlagged()
    {
        // The fix must not alter correctly-formatted invariant numbers, nor flag them.
        var a = await ParsePdfLineAsync(qty: "4", price: "73.22");
        a.UnitPrice.Should().Be(73.22m);
        a.Quantity.Should().Be(4m);
        a.NeedsReview.Should().BeFalse();
        a.ReviewReason.Should().BeNull();

        var b = await ParsePdfLineAsync(qty: "1", price: "1234.56");
        b.UnitPrice.Should().Be(1234.56m);
        b.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_SingleSeparatorAmbiguousPrice_IsCorrectOrFlagged_NeverSilentlyWrong()
    {
        // "1.234" is single-dot, 3 trailing digits — could be the invariant decimal 1.234
        // or an EU thousands group 1234. Under european:false the reader takes the decimal;
        // whichever it chooses it must be a real reading, never silently wrong.
        var line = await ParsePdfLineAsync(qty: "2", price: "1.234");

        (line.UnitPrice == 1.234m || line.UnitPrice == 1234m || line.NeedsReview)
            .Should().BeTrue("the value must be a defensible reading or flagged for review");
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
