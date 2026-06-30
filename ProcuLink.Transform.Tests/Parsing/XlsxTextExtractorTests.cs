using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class XlsxTextExtractorTests
{
    /// <summary>
    /// The real-world Markit shape that the row1=headers <see cref="XlsxOrderParser"/> can't
    /// read: header fields are LABEL→value cells stacked down the page, and the lines live in a
    /// later <c>Lines …</c> section. The text rendering must preserve that structure (labels
    /// adjacent to values, line rows intact) so the downstream LLM can extract it.
    /// </summary>
    [Fact]
    public void ExtractText_LabelledSectionedWorkbook_PreservesStructure()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Order");

        // Header section — label/value pairs down column A/B.
        ws.Cell("A1").Value = "PurchaseOrderNr"; ws.Cell("B1").Value = "226131000790";
        ws.Cell("A2").Value = "Currency";        ws.Cell("B2").Value = "PLN";
        ws.Cell("A3").Value = "BillToName";      ws.Cell("B3").Value = "Markit Sp. z o.o.";

        // Lines section — a header row of "Lines …" labels, then two data rows.
        ws.Cell("A5").Value = "Lines LineNr";
        ws.Cell("B5").Value = "Lines ManufPN";
        ws.Cell("C5").Value = "Lines ProductName";
        ws.Cell("D5").Value = "Lines Quantity";
        ws.Cell("E5").Value = "Lines PriceWoVAT";

        ws.Cell("A6").Value = 1; ws.Cell("B6").Value = "ABC-123"; ws.Cell("C6").Value = "Widget A";
        ws.Cell("D6").Value = 10; ws.Cell("E6").Value = 4.5;
        ws.Cell("A7").Value = 2; ws.Cell("B7").Value = "XYZ-999"; ws.Cell("C7").Value = "Widget B";
        ws.Cell("D7").Value = 6;  ws.Cell("E7").Value = 8.25;

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;

        var text = XlsxTextExtractor.ExtractText(stream);

        // Sheet header + every labelled value survives.
        text.Should().Contain("# Sheet: Order");
        text.Should().Contain("PurchaseOrderNr | 226131000790");
        text.Should().Contain("Currency | PLN");
        text.Should().Contain("BillToName | Markit Sp. z o.o.");

        // The Lines header and BOTH data rows survive as joined rows.
        text.Should().Contain("Lines LineNr | Lines ManufPN | Lines ProductName | Lines Quantity | Lines PriceWoVAT");
        text.Should().Contain("ABC-123");
        text.Should().Contain("Widget A");
        text.Should().Contain("XYZ-999");

        // Numbers render under InvariantCulture — never a comma-decimal even on an EU server.
        text.Should().Contain("4.5");
        text.Should().Contain("8.25");
        text.Should().NotContain("4,5");
    }

    [Fact]
    public void ExtractText_EmptyWorkbook_ReturnsEmptyString()
    {
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Empty");

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;

        XlsxTextExtractor.ExtractText(stream).Should().BeEmpty();
    }
}
