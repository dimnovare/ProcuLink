using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// Cross-format guard: every deterministic parser must read a header date through the ONE
/// shared reader (<see cref="DateParsing.TryParseFlexibleDate"/>) so that a single input
/// yields a single answer, and a genuinely ambiguous ordering is FLAGGED rather than guessed.
///
/// <para>Two defects are pinned here verbatim:</para>
/// <list type="number">
///   <item><b>The vanishing dotted date.</b> The UBL and cXML format arrays omitted
///     <c>"d.M.yyyy"</c>, so a German <c>12.06.2026</c> failed <c>TryParseExact</c>, fell
///     through to <c>DateTime.TryParse</c> with InvariantCulture (date separator <c>/</c>)
///     and returned <b>null</b> — the date silently disappeared. The same input parsed
///     day-first in CSV/PDF/XLSX: two different wrong answers for one input.</item>
///   <item><b>The unflagged guess.</b> Every hand-rolled array put <c>"dd/MM/yyyy"</c> before
///     <c>"MM/dd/yyyy"</c>, so <c>TryParseExact</c> silently committed to day-first on any
///     ≤12/≤12 date with nothing recording that a choice had been made.</item>
/// </list>
///
/// <para>Formats that genuinely DECLARE their date convention (X12 <c>BEG05</c>/<c>DTM</c>,
/// EDIFACT <c>DTM</c> format qualifiers, IDoc <c>YYYYMMDD</c>) keep that declaration
/// authoritative and must NEVER be flagged — pinned below so a later "consistency" change
/// cannot start flagging conformant EDI traffic.</para>
/// </summary>
public class ParserDateAmbiguityTests
{
    // A genuine ≤12/≤12 collision: 3 April (day-first) or 4 March (month-first)?
    private const string AmbiguousDate = "03/04/2026";

    // The German dotted date that vanished to null in UBL and cXML.
    private const string GermanDottedDate = "12.06.2026";

    // ── Defect 2: the dotted date must not vanish, and must mean the same thing everywhere ──

    [Theory]
    [InlineData("ubl")]
    [InlineData("cxml")]
    [InlineData("csv")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public async Task German_dotted_date_never_vanishes_and_reads_as_12_June(string format)
    {
        var parsed = await ParseWithHeaderDateAsync(format, GermanDottedDate);

        parsed.OrderDate.Should().NotBeNull(
            "a date the source printed must never silently disappear — 12.06.2026 returned null " +
            "in UBL/cXML because their format arrays omitted \"d.M.yyyy\"");
        parsed.OrderDate!.Value.Date.Should().Be(new DateTime(2026, 6, 12),
            "12.06.2026 is 12 June — 12 and 06 are both ≤ 12, so day-first policy decides");
    }

    [Fact]
    public async Task Every_format_agrees_on_the_same_dotted_date()
    {
        var formats = new[] { "ubl", "cxml", "csv", "xlsx", "pdf" };

        var answers = new List<(string Format, DateTime? Date)>();
        foreach (var f in formats)
            answers.Add((f, (await ParseWithHeaderDateAsync(f, GermanDottedDate)).OrderDate?.Date));

        answers.Should().OnlyContain(a => a.Date == new DateTime(2026, 6, 12),
            "one input must not mean 12 June in CSV and 'no date at all' in UBL. Actual: " +
            string.Join(", ", answers.Select(a => $"{a.Format}={a.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "null"}")));
    }

    // ── Defect 1: a genuine ≤12/≤12 collision is resolved by policy AND flagged ──

    [Theory]
    [InlineData("ubl")]
    [InlineData("cxml")]
    [InlineData("csv")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public async Task Ambiguous_date_is_flagged_for_review_not_silently_guessed(string format)
    {
        var parsed = await ParseWithHeaderDateAsync(format, AmbiguousDate);

        parsed.OrderDate!.Value.Date.Should().Be(new DateTime(2026, 4, 3),
            "day-first is the documented product default, so the emitted value is 3 April");
        parsed.NeedsReview.Should().BeTrue(
            "03/04/2026 is 3 April or 4 March depending on who sent it — the parser made a " +
            "policy choice and a human must confirm it");
        parsed.ReviewReason.Should().NotBeNullOrWhiteSpace(
            "a flag with no reason tells the reviewer nothing");
        parsed.ReviewReason.Should().Contain(AmbiguousDate,
            "the reason must name the raw value the reviewer has to adjudicate");
    }

    // ── No regression: unambiguous dates keep their value and are never flagged ──

    [Theory]
    [InlineData("ubl",  "2026-05-28", 2026, 5, 28)]
    [InlineData("cxml", "2026-05-28", 2026, 5, 28)]
    [InlineData("csv",  "2026-05-28", 2026, 5, 28)]
    [InlineData("xlsx", "2026-05-28", 2026, 5, 28)]
    [InlineData("pdf",  "2026-05-28", 2026, 5, 28)]
    // 25 > 12 forces day-first — the data decides, not the policy.
    [InlineData("ubl",  "25/12/2026", 2026, 12, 25)]
    [InlineData("cxml", "25/12/2026", 2026, 12, 25)]
    [InlineData("csv",  "25/12/2026", 2026, 12, 25)]
    [InlineData("xlsx", "25/12/2026", 2026, 12, 25)]
    [InlineData("pdf",  "25/12/2026", 2026, 12, 25)]
    // 25 in slot 2 forces month-first — the US ordering, still forced by the data.
    [InlineData("ubl",  "12/25/2026", 2026, 12, 25)]
    [InlineData("cxml", "12/25/2026", 2026, 12, 25)]
    [InlineData("csv",  "12/25/2026", 2026, 12, 25)]
    [InlineData("xlsx", "12/25/2026", 2026, 12, 25)]
    [InlineData("pdf",  "12/25/2026", 2026, 12, 25)]
    public async Task Unambiguous_dates_keep_their_value_and_are_never_flagged(
        string format, string raw, int y, int m, int d)
    {
        var parsed = await ParseWithHeaderDateAsync(format, raw);

        parsed.OrderDate!.Value.Date.Should().Be(new DateTime(y, m, d));
        parsed.NeedsReview.Should().BeFalse(
            $"\"{raw}\" has exactly one reading — flagging it would train reviewers to ignore the flag");
        parsed.ReviewReason.Should().BeNull();
    }

    // ── Declared formats stay authoritative and are NEVER flagged ──

    [Fact]
    public async Task X12_declared_date_is_authoritative_and_never_flagged()
    {
        // BEG05 is CCYYMMDD by the X12 850 spec — 20260304 is 4 March, not a guess.
        var edi =
            "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *260304*1200*U*00401*000000001*0*P*>~\n" +
            "GS*PO*SENDER*RECEIVER*20260304*1200*1*X*004010~\n" +
            "ST*850*0001~\n" +
            "BEG*00*NE*PO-X12-DATE**20260304~\n" +
            "PO1*1*10*EA*125.00**BP*ITEM-1~\n" +
            "CTT*1~\n" +
            "SE*6*0001~\n" +
            "GE*1*1~\n" +
            "IEA*1*000000001~\n";

        var parsed = await ParseAsync(new X12OrderParser(), edi);

        parsed.OrderDate!.Value.Date.Should().Be(new DateTime(2026, 3, 4),
            "the X12 spec declares CCYYMMDD — the ordering is not in question");
        parsed.NeedsReview.Should().BeFalse(
            "flagging a date the standard fully declares would flood review with conformant EDI");
    }

    [Fact]
    public async Task Edifact_declared_date_is_authoritative_and_never_flagged()
    {
        // DTM+137:<value>:102 — qualifier 102 declares CCYYMMDD explicitly.
        var edi =
            "UNH+1+ORDERS:D:96A:UN'\n" +
            "BGM+220+PO-EDI-DATE+9'\n" +
            "DTM+137:20260304:102'\n" +
            "LIN+1++ITEM-1:BP'\n" +
            "QTY+21:10'\n" +
            "PRI+AAA:125.00'\n" +
            "UNS+S'\n" +
            "UNT+8+1'\n";

        var parsed = await ParseAsync(new EdifactOrderParser(), edi);

        parsed.OrderDate!.Value.Date.Should().Be(new DateTime(2026, 3, 4),
            "format qualifier 102 declares CCYYMMDD — the ordering is not in question");
        parsed.NeedsReview.Should().BeFalse(
            "an explicit format qualifier is a declaration, not a guess");
    }

    [Fact]
    public async Task IDoc_declared_date_is_authoritative_and_never_flagged()
    {
        var idoc = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ORDERS05>
              <IDOC BEGIN="1">
                <E1EDK01 SEGMENT="1"><CURCY>EUR</CURCY><BELNR>PO-IDOC-DATE</BELNR></E1EDK01>
                <E1EDK02 SEGMENT="1"><QUALF>001</QUALF><BELNR>PO-IDOC-DATE</BELNR><DATUM>20260304</DATUM></E1EDK02>
                <E1EDP01 SEGMENT="1">
                  <POSEX>1</POSEX>
                  <MENGE>10</MENGE>
                  <MENEE>EA</MENEE>
                  <E1EDP19 SEGMENT="1"><QUALF>001</QUALF><IDTNR>ITEM-1</IDTNR></E1EDP19>
                </E1EDP01>
              </IDOC>
            </ORDERS05>
            """;

        var parsed = await ParseAsync(new IDocOrders05Parser(), idoc);

        parsed.OrderDate!.Value.Date.Should().Be(new DateTime(2026, 3, 4),
            "IDoc DATUM is YYYYMMDD by definition");
        parsed.NeedsReview.Should().BeFalse(
            "a fixed-width YYYYMMDD field cannot be ambiguous");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static async Task<ParsedOrder> ParseWithHeaderDateAsync(string format, string date) =>
        format switch
        {
            "ubl"  => await ParseAsync(new UblOrderParser(),  UblWithIssueDate(date)),
            "cxml" => await ParseAsync(new CxmlOrderParser(), CxmlWithOrderDate(date)),
            "csv"  => await ParseAsync(new CsvOrderParser(),  CsvWithOrderDate(date)),
            "pdf"  => await ParseAsync(new PdfOrderParser(),  CreatePdf(
                          "Purchase Order: PO-9001",
                          $"Order Date: {date}",
                          "Currency: EUR",
                          "1 ITEM-1 Widget A 10 EA 125.00")),
            "xlsx" => await ParseAsync(new XlsxOrderParser(), XlsxWithOrderDate(date)),
            _      => throw new ArgumentOutOfRangeException(nameof(format), format, "unknown fixture format"),
        };

    private static async Task<ParsedOrder> ParseAsync(IPurchaseOrderParser parser, string text) =>
        await ParseAsync(parser, Encoding.UTF8.GetBytes(text));

    private static async Task<ParsedOrder> ParseAsync(IPurchaseOrderParser parser, byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        return await parser.ParseAsync(stream, CancellationToken.None);
    }

    private static string UblWithIssueDate(string issueDate) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
               xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
               xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
          <cbc:ID>PO-9001</cbc:ID>
          <cbc:IssueDate>{issueDate}</cbc:IssueDate>
          <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cac:OrderLine>
            <cac:LineItem>
              <cbc:ID>1</cbc:ID>
              <cbc:Quantity unitCode="EA">10</cbc:Quantity>
              <cac:Price><cbc:PriceAmount currencyID="EUR">125.00</cbc:PriceAmount></cac:Price>
              <cac:Item>
                <cbc:Name>Widget A</cbc:Name>
                <cac:SellersItemIdentification><cbc:ID>ITEM-1</cbc:ID></cac:SellersItemIdentification>
              </cac:Item>
            </cac:LineItem>
          </cac:OrderLine>
        </Order>
        """;

    private static string CxmlWithOrderDate(string orderDate) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <cXML payloadID="test@proculink" timestamp="2026-01-15T09:00:00Z" xml:lang="en-US">
          <Header>
            <From><Credential domain="DUNS"><Identity>buyer-id</Identity></Credential></From>
            <To><Credential domain="DUNS"><Identity>supplier-id</Identity></Credential></To>
            <Sender><Credential domain="NetworkUserId"><Identity>sender</Identity></Credential></Sender>
          </Header>
          <Request deploymentMode="production">
            <OrderRequest>
              <OrderRequestHeader orderID="PO-9001" orderDate="{orderDate}" type="new">
                <Total><Money currency="EUR">1250.00</Money></Total>
              </OrderRequestHeader>
              <ItemOut quantity="10" lineNumber="1">
                <ItemID><SupplierPartID>ITEM-1</SupplierPartID></ItemID>
                <ItemDetail>
                  <UnitPrice><Money currency="EUR">125.00</Money></UnitPrice>
                  <Description xml:lang="en">Widget A</Description>
                  <UnitOfMeasure>EA</UnitOfMeasure>
                </ItemDetail>
              </ItemOut>
            </OrderRequest>
          </Request>
        </cXML>
        """;

    private static string CsvWithOrderDate(string orderDate) =>
        "PoNumber,OrderDate,Currency,LineNumber,BuyerItemCode,Description,Quantity,Unit,UnitPrice\n" +
        $"PO-9001,{orderDate},EUR,1,ITEM-1,Widget A,10,EA,125.00\n";

    private static byte[] XlsxWithOrderDate(string orderDate)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");

        var headers = new[]
        {
            "PoNumber", "Currency", "OrderDate",
            "LineNumber", "BuyerItemCode", "Description", "Quantity", "UnitPrice",
        };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        // Written as TEXT on purpose: a typed date cell would never exercise the string
        // reader that carries the defect.
        var row = new[] { "PO-9001", "EUR", orderDate, "1", "ITEM-1", "Widget A", "10", "125.00" };
        for (var c = 0; c < row.Length; c++) ws.Cell(2, c + 1).SetValue(row[c]);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
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
