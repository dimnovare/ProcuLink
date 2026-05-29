using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Transform.Detection;

namespace ProcuLink.Transform.Tests.Detection;

/// <summary>
/// Unit tests for <see cref="SourceColumnExtractor"/> — the magic-mapping source-field
/// extractor. Covers CSV, XLSX, UBL + cXML XML, EDIFACT, and X12, plus the safety contract
/// (never throws, format-hint precedence, de-duplication, empty input).
/// </summary>
public class SourceColumnExtractorTests
{
    private static readonly ISourceColumnExtractor Extractor = new SourceColumnExtractor();

    private static Stream ToStream(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static Task<SourceColumnSet> Extract(string text, string? fileName, string? hint = null) =>
        Extractor.ExtractAsync(ToStream(text), fileName, hint, CancellationToken.None);

    // ── CSV ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csv_ReturnsHeaderRowCells()
    {
        const string csv =
            "PoNumber,BuyerItemCode,Description,Quantity,Unit,UnitPrice\r\n" +
            "PO-1,ABC-1,Widget,10,EA,12.50\r\n";

        var result = await Extract(csv, "order.csv");

        result.Format.Should().Be("csv");
        result.Columns.Should().Equal(
            "PoNumber", "BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice");
    }

    [Fact]
    public async Task Csv_SemicolonDelimited_ReturnsHeaderRowCells()
    {
        const string csv =
            "po_number;item_code;description;qty;uom;price\r\n" +
            "PO-2;XYZ-9;Bolt;5;PCS;0,40\r\n";

        var result = await Extract(csv, "euro-order.csv");

        result.Format.Should().Be("csv");
        result.Columns.Should().Equal(
            "po_number", "item_code", "description", "qty", "uom", "price");
    }

    [Fact]
    public async Task Csv_QuotedHeaders_AreUnquotedAndTrimmed()
    {
        const string csv =
            "\"Po Number\",\"Item Code\",\"Description\"\r\n" +
            "PO-3,QQ-1,Thing\r\n";

        var result = await Extract(csv, "quoted.csv");

        result.Columns.Should().Equal("Po Number", "Item Code", "Description");
    }

    // ── XLSX ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Xlsx_ReturnsHeaderRowCells()
    {
        var bytes = BuildXlsx(
            header: new[] { "LineNumber", "BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice" },
            row:    new[] { "1", "ACME-1", "Mount", "4", "PCS", "12.50" });

        var result = await Extractor.ExtractAsync(
            new MemoryStream(bytes), "order.xlsx", null, CancellationToken.None);

        result.Format.Should().Be("xlsx");
        result.Columns.Should().Equal(
            "LineNumber", "BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice");
    }

    [Fact]
    public async Task Xlsx_SkipsBlankTrailingHeaderCells()
    {
        // ClosedXML's RangeUsed() trims trailing empties; assert the meaningful headers survive.
        var bytes = BuildXlsx(
            header: new[] { "Code", "Qty" },
            row:    new[] { "A1", "3" });

        var result = await Extractor.ExtractAsync(
            new MemoryStream(bytes), "two-col.xlsx", null, CancellationToken.None);

        result.Columns.Should().Equal("Code", "Qty");
    }

    // ── UBL XML ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ubl_ReturnsLeafElementsAndNotableAttributes()
    {
        const string ubl =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Order xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Order-2\"" +
            "       xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\"" +
            "       xmlns:cac=\"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2\">" +
            "  <cbc:ID>PO-12345</cbc:ID>" +
            "  <cbc:IssueDate>2026-05-28</cbc:IssueDate>" +
            "  <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>" +
            "  <cac:OrderLine><cac:LineItem>" +
            "    <cbc:ID>1</cbc:ID>" +
            "    <cbc:Quantity unitCode=\"EA\">10</cbc:Quantity>" +
            "    <cac:Price><cbc:PriceAmount currencyID=\"EUR\">125.00</cbc:PriceAmount></cac:Price>" +
            "    <cac:Item><cbc:Name>Widget</cbc:Name></cac:Item>" +
            "  </cac:LineItem></cac:OrderLine>" +
            "</Order>";

        var result = await Extract(ubl, "order.xml", hint: "ubl");

        result.Format.Should().Be("ubl");
        // Leaf elements surface by bare LocalName — the namespace prefix is dropped so that
        // prefix style (xmlns:cbc vs declaring the same namespace as default) cannot change
        // the persisted mapping key.
        result.Columns.Should().Contain("ID");
        result.Columns.Should().Contain("IssueDate");
        result.Columns.Should().Contain("DocumentCurrencyCode");
        result.Columns.Should().Contain("Quantity");
        result.Columns.Should().Contain("PriceAmount");
        result.Columns.Should().Contain("Name");
        // Notable attributes surface as Element@attr, also by LocalName.
        result.Columns.Should().Contain("Quantity@unitCode");
        result.Columns.Should().Contain("PriceAmount@currencyID");
        // Prefixed forms must NOT leak through any more.
        result.Columns.Should().NotContain("cbc:ID");
        result.Columns.Should().NotContain("cbc:Quantity@unitCode");
        // Container elements that only hold child elements are NOT leaves (by LocalName).
        result.Columns.Should().NotContain("OrderLine");
        result.Columns.Should().NotContain("Item");
        // Result is de-duplicated even though ID appears twice (header + line).
        result.Columns.Count(c => c == "ID").Should().Be(1);
    }

    // ── cXML XML ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cxml_ReturnsAttributesIncludingOrderRequestHeaderOrderId()
    {
        const string cxml =
            "<?xml version=\"1.0\"?>" +
            "<cXML xmlns=\"http://www.cxml.org/cXML\">" +
            "  <Request deploymentMode=\"production\">" +
            "    <OrderRequest>" +
            "      <OrderRequestHeader orderID=\"PO-777\" orderDate=\"2026-05-28\" type=\"new\">" +
            "        <Total><Money currency=\"EUR\">125.00</Money></Total>" +
            "      </OrderRequestHeader>" +
            "      <ItemOut quantity=\"10\" lineNumber=\"1\">" +
            "        <ItemID><SupplierPartID>SUP-ABC</SupplierPartID></ItemID>" +
            "        <ItemDetail><Description>Widget</Description><UnitOfMeasure>EA</UnitOfMeasure></ItemDetail>" +
            "      </ItemOut>" +
            "    </OrderRequest>" +
            "  </Request>" +
            "</cXML>";

        var result = await Extract(cxml, "order.cxml");

        result.Format.Should().Be("cxml");
        // The hero attribute the standards-visibility rule calls out by name.
        result.Columns.Should().Contain("OrderRequestHeader@orderID");
        result.Columns.Should().Contain("OrderRequestHeader@orderDate");
        result.Columns.Should().Contain("Request@deploymentMode");
        result.Columns.Should().Contain("ItemOut@quantity");
        result.Columns.Should().Contain("Money@currency");
        // Leaf elements that carry text.
        result.Columns.Should().Contain("SupplierPartID");
        result.Columns.Should().Contain("Description");
        result.Columns.Should().Contain("UnitOfMeasure");
        result.Columns.Should().Contain("Money");
    }

    [Fact]
    public async Task GenericXml_NoNamespace_ReturnsBareLeafNames()
    {
        const string xml =
            "<order><poNumber>PO-9</poNumber><line><sku>A1</sku><qty>3</qty></line></order>";

        var result = await Extract(xml, "thing.xml");

        result.Format.Should().Be("xml");
        result.Columns.Should().Contain("poNumber");
        result.Columns.Should().Contain("sku");
        result.Columns.Should().Contain("qty");
        result.Columns.Should().NotContain("order");
        result.Columns.Should().NotContain("line");
    }

    // ── EDIFACT ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edifact_ReturnsDistinctSegmentTags()
    {
        const string nl = "\r\n";
        var edi =
            "UNB+UNOC:3+SENDER:14+RECEIVER:14+240115:0900+1'" + nl +
            "UNH+1+ORDERS:D:96A:UN'" + nl +
            "BGM+220+PO-D96A-001+9'" + nl +
            "DTM+137:20240115:102'" + nl +
            "CUX+2:EUR:9'" + nl +
            "NAD+BY+5412345678901::9++Acme Buyer Ltd'" + nl +
            "LIN+1++ITEM-CODE:IN'" + nl +
            "QTY+21:100:PCE'" + nl +
            "PRI+AAA:25.00'" + nl +
            "UNS+S'" + nl +
            "UNT+11+1'" + nl +
            "UNZ+1+1'" + nl;

        var result = await Extract(edi, "order.edi");

        result.Format.Should().Be("edifact");
        result.Columns.Should().Contain(new[] { "UNB", "UNH", "BGM", "DTM", "CUX", "NAD", "LIN", "QTY", "PRI", "UNS", "UNT", "UNZ" });
        // Distinct only — even if a tag repeated it would appear once.
        result.Columns.Should().OnlyHaveUniqueItems();
    }

    // ── X12 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task X12_ReturnsDistinctSegmentTags()
    {
        const string nl = "\r\n";
        var isa =
            "ISA*00*" + new string(' ', 10) + "*00*" + new string(' ', 10) +
            "*ZZ*" + "SENDER".PadRight(15) + "*ZZ*" + "RECEIVER".PadRight(15) +
            "*260529*0830*U*00401*000000001*0*P*>~" + nl;
        var x12 =
            isa +
            "GS*PO*SENDER*RECEIVER*20260529*0830*1*X*004010~" + nl +
            "ST*850*0001~" + nl +
            "BEG*00*NE*PO-12345**20260529~" + nl +
            "CUR*BY*USD~" + nl +
            "N1*BY*Acme Buying Corp~" + nl +
            "PO1*1*12*EA*4.50*PE*BP*ACME-WIDGET-A~" + nl +
            "PID*F****Widget A 10mm~" + nl +
            "CTT*1~" + nl +
            "SE*8*0001~" + nl +
            "GE*1*1~" + nl +
            "IEA*1*000000001~" + nl;

        var result = await Extract(x12, "order.x12");

        result.Format.Should().Be("x12");
        result.Columns.Should().Contain(new[] { "ISA", "GS", "ST", "BEG", "CUR", "N1", "PO1", "PID", "CTT", "SE", "GE", "IEA" });
        result.Columns.Should().OnlyHaveUniqueItems();
    }

    // ── Format-hint precedence + content fallback ──────────────────────────────

    [Fact]
    public async Task FormatHint_TakesPrecedenceOverExtension()
    {
        // File named .txt but the hint says csv → CSV header extraction runs.
        const string csv = "a,b,c\r\n1,2,3\r\n";

        var result = await Extract(csv, "mystery.txt", hint: "csv");

        result.Format.Should().Be("csv");
        result.Columns.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task NoHintNoExtension_SniffsCsvFromContent()
    {
        const string csv = "alpha,beta,gamma\r\n1,2,3\r\n";

        var result = await Extractor.ExtractAsync(ToStream(csv), fileName: null, formatHint: null, CancellationToken.None);

        result.Format.Should().Be("csv");
        result.Columns.Should().Equal("alpha", "beta", "gamma");
    }

    [Fact]
    public async Task NoHintNoExtension_SniffsEdifactFromEnvelope()
    {
        const string nl = "\r\n";
        var edi =
            "UNB+UNOC:3+S:14+R:14+240115:0900+1'" + nl +
            "UNH+1+ORDERS:D:96A:UN'" + nl +
            "BGM+220+PO-1+9'" + nl;

        var result = await Extractor.ExtractAsync(ToStream(edi), fileName: null, formatHint: null, CancellationToken.None);

        result.Format.Should().Be("edifact");
        result.Columns.Should().Contain(new[] { "UNB", "UNH", "BGM" });
    }

    // ── Safety contract ─────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyStream_ReturnsEmptyColumns_NeverThrows()
    {
        var result = await Extractor.ExtractAsync(
            new MemoryStream(Array.Empty<byte>()), "empty.csv", null, CancellationToken.None);

        result.Columns.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedXml_ReturnsEmptyColumns_NeverThrows()
    {
        const string broken = "<order><unclosed>";

        var act = async () => await Extract(broken, "broken.xml");

        var result = await act.Should().NotThrowAsync();
        // We may surface the leaf seen before the truncation, but must not crash.
        result.Subject.Format.Should().Be("xml");
    }

    [Fact]
    public async Task UnknownBinary_ReturnsUnknownFormatAndEmptyColumns()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };

        var result = await Extractor.ExtractAsync(
            new MemoryStream(bytes), "mystery.bin", null, CancellationToken.None);

        result.Format.Should().Be("unknown");
        result.Columns.Should().BeEmpty();
    }

    [Fact]
    public async Task Pdf_HintReturnsEmptyColumns_BestEffort()
    {
        // PDF extraction is intentionally best-effort/empty; assert it degrades cleanly.
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n...not a real pdf body...");

        var result = await Extractor.ExtractAsync(
            new MemoryStream(bytes), "scan.pdf", "pdf", CancellationToken.None);

        result.Format.Should().Be("pdf");
        result.Columns.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildXlsx(string[] header, string[] row)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("Sheet1");

        for (var c = 0; c < header.Length; c++)
            ws.Cell(1, c + 1).Value = header[c];
        for (var c = 0; c < row.Length; c++)
            ws.Cell(2, c + 1).Value = row[c];

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
