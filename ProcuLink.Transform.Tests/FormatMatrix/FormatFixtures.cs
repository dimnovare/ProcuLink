using System.Globalization;
using System.Text;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// Self-contained, deterministic fixture builders for the FormatMatrix stress suite.
///
/// Every builder emits a byte payload in one of the seven deterministic input formats
/// the ingest pipeline accepts (CSV, XLSX, cXML, UBL, EDIFACT, X12, SAP IDoc ORDERS05).
/// They are intentionally string/byte builders — not file fixtures — so the matrix
/// tests are hermetic, fast, and don't depend on CopyToOutputDirectory wiring.
///
/// PDF is deliberately absent here: the primary PDF path needs the live OpenAI
/// extractor and is excluded from this deterministic suite (see PdfMatrixPlaceholderTests).
/// </summary>
public static class FormatFixtures
{
    public const string NL = "\r\n";

    public static Stream ToStream(byte[] bytes) => new MemoryStream(bytes, writable: false);
    public static Stream ToStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string XmlEsc(string? s) =>
        (s ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    // ── A small canonical line model used by every builder ───────────────────────
    public sealed record Line(
        int LineNumber,
        string Code,
        string? Description,
        decimal Quantity,
        string? Unit,
        decimal? UnitPrice);

    /// <summary>A representative 2-line EUR order shared by the round-trip matrix.</summary>
    public static IReadOnlyList<Line> RepresentativeLines() => new List<Line>
    {
        new(1, "BUY-001", "Widget Type A", 10m, "EA", 12.50m),
        new(2, "BUY-002", "Widget Type B", 5m,  "EA", 8.00m),
    };

    // ════════════════════════════════════════════════════════════════════════════
    // CSV
    // ════════════════════════════════════════════════════════════════════════════

    public static string CsvHeader =>
        "ponumber,orderdate,currency,buyername,linenumber,buyeritemcode,description,quantity,unit,unitprice";

    public static byte[] Csv(
        string poNumber, string currency, string buyerName,
        IReadOnlyList<Line> lines, string orderDate = "2026-06-08")
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append(NL);
        foreach (var l in lines)
        {
            sb.Append(string.Join(",",
                poNumber, orderDate, currency, buyerName,
                l.LineNumber.ToString(CultureInfo.InvariantCulture),
                l.Code,
                CsvField(l.Description),
                l.Quantity.ToString(CultureInfo.InvariantCulture),
                l.Unit ?? string.Empty,
                l.UnitPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
            sb.Append(NL);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string CsvField(string? v)
    {
        v ??= string.Empty;
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // XLSX  (ClosedXML, in-memory)
    // ════════════════════════════════════════════════════════════════════════════

    public static byte[] Xlsx(
        string poNumber, string currency, string buyerName,
        IReadOnlyList<Line> lines, string orderDate = "2026-06-08")
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Order");

        string[] headers =
        {
            "PoNumber", "OrderDate", "Currency", "BuyerName",
            "LineNumber", "BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice",
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var l in lines)
        {
            ws.Cell(r, 1).Value = poNumber;
            ws.Cell(r, 2).Value = orderDate;
            ws.Cell(r, 3).Value = currency;
            ws.Cell(r, 4).Value = buyerName;
            ws.Cell(r, 5).Value = l.LineNumber;
            ws.Cell(r, 6).Value = l.Code;
            ws.Cell(r, 7).Value = l.Description ?? string.Empty;
            ws.Cell(r, 8).Value = l.Quantity;
            ws.Cell(r, 9).Value = l.Unit ?? string.Empty;
            if (l.UnitPrice is { } p) ws.Cell(r, 10).Value = p;
            r++;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // cXML 1.2
    // ════════════════════════════════════════════════════════════════════════════

    public static byte[] Cxml(
        string orderId, string currency, IReadOnlyList<Line> lines,
        string? total = "250.00", bool omitTotal = false, bool wellFormed = true)
    {
        var items = new StringBuilder();
        foreach (var l in lines)
        {
            items.Append($@"      <ItemOut quantity=""{l.Quantity.ToString(CultureInfo.InvariantCulture)}"" lineNumber=""{l.LineNumber}"">
        <ItemID><SupplierPartID>{XmlEsc(l.Code)}</SupplierPartID></ItemID>
        <ItemDetail>
          <UnitPrice><Money currency=""{currency}"">{(l.UnitPrice ?? 0m).ToString(CultureInfo.InvariantCulture)}</Money></UnitPrice>
          <Description xml:lang=""en"">{XmlEsc(l.Description)}</Description>
          <UnitOfMeasure>{XmlEsc(l.Unit ?? "EA")}</UnitOfMeasure>
        </ItemDetail>
      </ItemOut>
");
        }

        var doc = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<cXML payloadID=""matrix-{XmlEsc(orderId)}@proculink.test"" timestamp=""2026-06-08T10:00:00+00:00"" version=""1.2.044"">
  <Header>
    <From><Credential domain=""NetworkId""><Identity>MATRIX_BUYER</Identity></Credential></From>
    <To><Credential domain=""NetworkId""><Identity>TESTSUPPLIER_EE</Identity></Credential></To>
    <Sender><Credential domain=""NetworkId""><Identity>MATRIX_BUYER</Identity><SharedSecret>x</SharedSecret></Credential><UserAgent>Matrix</UserAgent></Sender>
  </Header>
  <Request deploymentMode=""production"">
    <OrderRequest>
      <OrderRequestHeader orderID=""{XmlEsc(orderId)}"" orderDate=""2026-06-08T10:00:00+00:00"" type=""new"">
        {(omitTotal ? "" : $"<Total><Money currency=\"{currency}\">{total}</Money></Total>")}
      </OrderRequestHeader>
{items}    </OrderRequest>
  </Request>
</cXML>
";
        if (!wellFormed)
            doc = doc[..(doc.Length / 2)]; // truncate to break well-formedness

        return Encoding.UTF8.GetBytes(doc);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // UBL 2.1 Order
    // ════════════════════════════════════════════════════════════════════════════

    public static byte[] Ubl(
        string id, string currency, string buyerName,
        IReadOnlyList<Line> lines, bool peppol = false, bool omitId = false)
    {
        var olines = new StringBuilder();
        foreach (var l in lines)
        {
            olines.Append($@"  <cac:OrderLine>
    <cac:LineItem>
      <cbc:ID>{l.LineNumber}</cbc:ID>
      <cbc:Quantity unitCode=""{XmlEsc(l.Unit ?? "EA")}"">{l.Quantity.ToString(CultureInfo.InvariantCulture)}</cbc:Quantity>
      <cac:Price><cbc:PriceAmount currencyID=""{currency}"">{(l.UnitPrice ?? 0m).ToString(CultureInfo.InvariantCulture)}</cbc:PriceAmount></cac:Price>
      <cac:Item>
        <cbc:Name>{XmlEsc(l.Description)}</cbc:Name>
        <cac:BuyersItemIdentification><cbc:ID>{XmlEsc(l.Code)}</cbc:ID></cac:BuyersItemIdentification>
      </cac:Item>
    </cac:LineItem>
  </cac:OrderLine>
");
        }

        var doc = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Order xmlns=""urn:oasis:names:specification:ubl:schema:xsd:Order-2""
       xmlns:cbc=""urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2""
       xmlns:cac=""urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"">
  {(peppol ? "<cbc:CustomizationID>urn:fdc:peppol.eu:poacc:trns:order:3</cbc:CustomizationID>" : "")}
  {(omitId ? "" : $"<cbc:ID>{XmlEsc(id)}</cbc:ID>")}
  <cbc:IssueDate>2026-06-08</cbc:IssueDate>
  <cbc:DocumentCurrencyCode>{currency}</cbc:DocumentCurrencyCode>
  <cac:BuyerCustomerParty><cac:Party><cac:PartyName><cbc:Name>{XmlEsc(buyerName)}</cbc:Name></cac:PartyName></cac:Party></cac:BuyerCustomerParty>
  <cac:SellerSupplierParty><cac:Party><cac:PartyName><cbc:Name>Matrix Supplier OY</cbc:Name></cac:PartyName></cac:Party></cac:SellerSupplierParty>
{olines}</Order>
";
        return Encoding.UTF8.GetBytes(doc);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // EDIFACT ORDERS D96A
    // ════════════════════════════════════════════════════════════════════════════

    public static byte[] Edifact(
        string poNumber, string currency, string buyerName,
        IReadOnlyList<Line> lines, string orderDate = "20260608")
    {
        var sb = new StringBuilder();
        sb.Append("UNB+UNOC:3+SENDER:14+RECEIVER:14+260608:0900+1'").Append(NL);
        sb.Append("UNH+1+ORDERS:D:96A:UN'").Append(NL);
        sb.Append($"BGM+220+{poNumber}+9'").Append(NL);
        sb.Append($"DTM+137:{orderDate}:102'").Append(NL);
        sb.Append($"CUX+2:{currency}:9'").Append(NL);
        sb.Append($"NAD+BY+5412345678901::9++{EdiEsc(buyerName)}'").Append(NL);

        foreach (var l in lines)
        {
            sb.Append($"LIN+{l.LineNumber}++{EdiEsc(l.Code)}:IN'").Append(NL);
            if (!string.IsNullOrWhiteSpace(l.Description))
                sb.Append($"IMD+F+ANM+:::{EdiEsc(l.Description!)}'").Append(NL);
            sb.Append($"QTY+21:{l.Quantity.ToString(CultureInfo.InvariantCulture)}:{EdiEsc(l.Unit ?? "EA")}'").Append(NL);
            if (l.UnitPrice is { } p)
                sb.Append($"PRI+AAA:{p.ToString(CultureInfo.InvariantCulture)}'").Append(NL);
        }

        sb.Append("UNS+S'").Append(NL);
        sb.Append("UNT+99+1'").Append(NL); // count is not validated by the parser
        sb.Append("UNZ+1+1'").Append(NL);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string EdiEsc(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '?' or ':' or '+' or '\'')
                sb.Append('?');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // X12 850
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Conformant fixed-width ISA (105 chars before the '~').</summary>
    public static string X12Isa() =>
        "ISA" +
        "*00" +
        "*" + new string(' ', 10) +
        "*00" +
        "*" + new string(' ', 10) +
        "*ZZ" +
        "*" + "SENDER".PadRight(15) +
        "*ZZ" +
        "*" + "RECEIVER".PadRight(15) +
        "*260608" +
        "*0830" +
        "*U" +
        "*00401" +
        "*000000001" +
        "*0" +
        "*P" +
        "*>" +
        "~" + NL;

    public static byte[] X12(
        string poNumber, string currency, string buyerName,
        IReadOnlyList<Line> lines, string orderDate = "20260608")
    {
        var sb = new StringBuilder();
        sb.Append(X12Isa());
        sb.Append("GS*PO*SENDER*RECEIVER*20260608*0830*1*X*004010~").Append(NL);
        sb.Append("ST*850*0001~").Append(NL);
        sb.Append($"BEG*00*NE*{poNumber}**{orderDate}~").Append(NL);
        sb.Append($"CUR*BY*{currency}~").Append(NL);
        sb.Append($"N1*BY*{X12San(buyerName)}~").Append(NL);

        foreach (var l in lines)
        {
            sb.Append($"PO1*{l.LineNumber}*{l.Quantity.ToString(CultureInfo.InvariantCulture)}*{X12San(l.Unit ?? "EA")}*{(l.UnitPrice ?? 0m).ToString(CultureInfo.InvariantCulture)}*PE*BP*{X12San(l.Code)}~").Append(NL);
            if (!string.IsNullOrWhiteSpace(l.Description))
                sb.Append($"PID*F****{X12San(l.Description!)}~").Append(NL);
        }

        sb.Append($"CTT*{lines.Count}~").Append(NL);
        sb.Append("SE*99*0001~").Append(NL);
        sb.Append("GE*1*1~").Append(NL);
        sb.Append("IEA*1*000000001~").Append(NL);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string X12San(string v) =>
        v.Replace('*', ' ').Replace('>', ' ').Replace('~', ' ').Trim();

    // ════════════════════════════════════════════════════════════════════════════
    // SAP IDoc ORDERS05
    // ════════════════════════════════════════════════════════════════════════════

    /// <param name="curcy">E1EDK01 CURCY — often a numeric internal code (e.g. "704").</param>
    /// <param name="sunit">E1EDS01 SUNIT — the alphabetic ISO currency (e.g. "EUR").</param>
    public static byte[] Idoc(
        string poNumber, string buyerName, IReadOnlyList<Line> lines,
        string curcy = "704", string sunit = "EUR", string orderDate = "20260608",
        decimal? grandTotal = 186.01m)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(NL);
        sb.Append("<ORDERS05>").Append(NL);
        sb.Append("  <IDOC BEGIN=\"1\">").Append(NL);
        sb.Append($"    <E1EDK01><CURCY>{XmlEsc(curcy)}</CURCY><BELNR>{XmlEsc(poNumber)}</BELNR></E1EDK01>").Append(NL);
        sb.Append($"    <E1EDKA1><PARVW>AG</PARVW><NAME1>{XmlEsc(buyerName)}</NAME1></E1EDKA1>").Append(NL);
        sb.Append("    <E1EDKA1><PARVW>LF</PARVW><NAME1>Matrix Supplier OU</NAME1></E1EDKA1>").Append(NL);
        sb.Append($"    <E1EDK02><QUALF>001</QUALF><BELNR>{XmlEsc(poNumber)}</BELNR><DATUM>{orderDate}</DATUM></E1EDK02>").Append(NL);

        foreach (var l in lines)
        {
            sb.Append("    <E1EDP01>").Append(NL);
            sb.Append($"      <POSEX>{l.LineNumber:00000}</POSEX>");
            sb.Append($"<MENGE>{l.Quantity.ToString(CultureInfo.InvariantCulture)}</MENGE>");
            sb.Append($"<MENEE>{XmlEsc(l.Unit ?? "EA")}</MENEE>");
            sb.Append($"<VPREI>{(l.UnitPrice ?? 0m).ToString(CultureInfo.InvariantCulture)}</VPREI>");
            sb.Append($"<NETWR>{(l.Quantity * (l.UnitPrice ?? 0m)).ToString(CultureInfo.InvariantCulture)}</NETWR>").Append(NL);
            sb.Append($"      <E1EDP19><QUALF>002</QUALF><IDTNR>{XmlEsc(l.Code)}</IDTNR><KTEXT>{XmlEsc(l.Description)}</KTEXT></E1EDP19>").Append(NL);
            if (!string.IsNullOrWhiteSpace(l.Description))
                sb.Append($"      <E1EDPT1><E1EDPT2><TDLINE>{XmlEsc(l.Description)}</TDLINE></E1EDPT2></E1EDPT1>").Append(NL);
            sb.Append("    </E1EDP01>").Append(NL);
        }

        sb.Append($"    <E1EDS01><SUMME>{grandTotal?.ToString(CultureInfo.InvariantCulture)}</SUMME><SUNIT>{XmlEsc(sunit)}</SUNIT></E1EDS01>").Append(NL);
        sb.Append("  </IDOC>").Append(NL);
        sb.Append("</ORDERS05>").Append(NL);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
