using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Parsing;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace ProcuLink.Transform.Tests.Parsing;

public class XlsxOrderParserTests
{
    [Fact]
    public async Task ParseAsync_HeaderRowPlusLines_ReturnsParsedOrder()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");

        var headers = new[]
        {
            "PoNumber", "BuyerName", "Currency", "OrderDate",
            "LineNumber", "BuyerItemCode", "Description", "Quantity", "UnitPrice",
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        // Cells written as text so numeric parsing is culture-deterministic (the parser
        // reads cell.GetString() and parses with InvariantCulture).
        var line1 = new[] { "DEMO-2026-001", "Northwind Trading", "EUR", "2026-01-15", "1", "ACME-WIDGET-A", "Widget A 10mm", "12", "4.50" };
        var line2 = new[] { "DEMO-2026-001", "", "", "", "2", "ACME-WIDGET-B", "Widget B 20mm", "6", "8.25" };
        for (var c = 0; c < line1.Length; c++) ws.Cell(2, c + 1).Value = line1[c];
        for (var c = 0; c < line2.Length; c++) ws.Cell(3, c + 1).Value = line2[c];

        await using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;

        var result = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("DEMO-2026-001");
        result.BuyerName.Should().Be("Northwind Trading");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(2);
        result.Lines[0].LineNumber.Should().Be(1);
        result.Lines[0].BuyerItemCode.Should().Be("ACME-WIDGET-A");
        result.Lines[0].Description.Should().Be("Widget A 10mm");
        result.Lines[0].Quantity.Should().Be(12);
        result.Lines[0].UnitPrice.Should().Be(4.50m);
        result.Lines[1].BuyerItemCode.Should().Be("ACME-WIDGET-B");
        result.Lines[1].UnitPrice.Should().Be(8.25m);
    }

    /// <summary>
    /// Regression for live prod order ba89b09c / PO-20260606210428: a valid .xlsx whose zip
    /// parts use a compression method the .NET BCL <c>ZipArchive</c> can't read (Deflate64 in
    /// prod) failed with "The archive entry was compressed using an unsupported compression
    /// method." The parser must now repack such workbooks via SharpCompress and parse them.
    ///
    /// The fixture is repacked with <b>BZip2</b>: the BCL rejects EVERY non-Stored/Deflate
    /// method — Deflate64, BZip2, LZMA, … — with the identical "unsupported compression method"
    /// <see cref="InvalidDataException"/>, so BZip2 drives the exact prod failure path. We use it
    /// (rather than Deflate64) because SharpCompress can <i>write</i> BZip2 but not Deflate64, so
    /// the fixture is generated deterministically at test time — no opaque committed binary.
    /// </summary>
    [Fact]
    public async Task ParseAsync_WorkbookWithUnsupportedZipCompression_RepacksAndParses()
    {
        using var exotic = BuildUnsupportedCompressionXlsx();

        // Sanity: this fixture really makes a plain ClosedXML open fail. ClosedXML routes through
        // System.IO.Packaging, which MASKS the BCL compression error as
        // FileFormatException("File contains corrupted data.") — so the strict
        // IsUnsupportedCompression would NOT see it, but ShouldAttemptRepack (the recovery
        // trigger) must.
        Exception? bclFailure = null;
        try
        {
            using var _ = new XLWorkbook(new MemoryStream(exotic.ToArray()));
        }
        catch (Exception ex)
        {
            bclFailure = ex;
        }

        bclFailure.Should().NotBeNull("the fixture must make a plain ClosedXML open fail");
        XlsxCompressionFallback.ShouldAttemptRepack(bclFailure!).Should().BeTrue(bclFailure!.ToString());

        // The parser now opens it via the SharpCompress repack fallback.
        exotic.Position = 0;
        var result = await new XlsxOrderParser().ParseAsync(exotic, CancellationToken.None);

        result.Lines.Should().HaveCountGreaterThanOrEqualTo(1);
        result.PoNumber.Should().Be("DEFLATE64-PO");
        result.Lines[0].BuyerItemCode.Should().Be("WIDGET-X");
        result.Lines[0].Quantity.Should().Be(7m);
        result.Lines[0].UnitPrice.Should().Be(3.25m);
    }

    [Theory]
    // generic — exact string live prod order ba89b09c produced
    [InlineData("The archive entry was compressed using an unsupported compression method.", true)]
    // named — .NET 8 variants for methods it can name but not read
    [InlineData("The archive entry was compressed using BZip2 and is not supported.", true)]
    [InlineData("The archive entry was compressed using PPMd and is not supported.", true)]
    [InlineData("Some unrelated parse failure.", false)]
    public void IsUnsupportedCompression_MatchesBothBclMessageVariants(string message, bool expected)
    {
        XlsxCompressionFallback.IsUnsupportedCompression(new InvalidDataException(message))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("The archive entry was compressed using an unsupported compression method.", true)] // raw BCL
    [InlineData("File contains corrupted data.", true)]   // ClosedXML/System.IO.Packaging masked form
    [InlineData("CORRUPTED stream", true)]                // case-insensitive
    [InlineData("Worksheet 'Sheet1' not found.", false)]  // unrelated ClosedXML error — do not repack
    public void ShouldAttemptRepack_CoversMaskedAndRawFailures_ButNotUnrelated(string message, bool expected)
    {
        XlsxCompressionFallback.ShouldAttemptRepack(new Exception(message)).Should().Be(expected);
    }

    [Fact]
    public void IsUnsupportedCompression_WalksInnerExceptionChain()
    {
        // ClosedXML / System.IO.Packaging may wrap the raw BCL InvalidDataException.
        var wrapped = new Exception("Failed to open workbook",
            new InvalidDataException("The archive entry was compressed using an unsupported compression method."));

        XlsxCompressionFallback.IsUnsupportedCompression(wrapped).Should().BeTrue();
    }

    /// <summary>
    /// Builds a real, structurally-valid .xlsx (via ClosedXML) and then re-packs every OPC part
    /// into a zip whose entries use BZip2 — a compression method the .NET BCL cannot read.
    /// </summary>
    private static MemoryStream BuildUnsupportedCompressionXlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");
        var headers = new[]
        {
            "PoNumber", "BuyerName", "Currency", "OrderDate",
            "LineNumber", "BuyerItemCode", "Description", "Quantity", "UnitPrice",
        };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var line1 = new[] { "DEFLATE64-PO", "Acme Buyer OÜ", "EUR", "2026-06-06", "1", "WIDGET-X", "Exotic Widget", "7", "3.25" };
        for (var c = 0; c < line1.Length; c++) ws.Cell(2, c + 1).Value = line1[c];

        using var standard = new MemoryStream();
        wb.SaveAs(standard);
        standard.Position = 0;

        var exotic = new MemoryStream();
        using (var src = new System.IO.Compression.ZipArchive(standard, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
        using (var writer = WriterFactory.OpenWriter(exotic, ArchiveType.Zip, new WriterOptions(CompressionType.BZip2) { LeaveStreamOpen = true }))
        {
            foreach (var entry in src.Entries)
            {
                using var es = entry.Open();
                using var buf = new MemoryStream();
                es.CopyTo(buf); // BCL entry stream is forward-only; materialize before re-writing.
                buf.Position = 0;
                writer.Write(entry.FullName, buf, entry.LastWriteTime.DateTime);
            }
        }

        exotic.Position = 0;
        return exotic;
    }
}
