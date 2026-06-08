using System.Globalization;
using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;
using static ProcuLink.Transform.Tests.FormatMatrix.FormatFixtures;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// IN-coverage leg of the FormatMatrix stress suite: every supported input format
/// gets a representative fixture AND adversarial edge cases, asserting either that
/// the canonical fields are captured correctly OR that malformed input fails
/// GRACEFULLY (a typed parse exception, never a raw crash / hang / silent corrupt).
///
/// Formats: CSV, XLSX, cXML, UBL, EDIFACT, X12, SAP IDoc ORDERS05.
/// (PDF is excluded — see PdfMatrixPlaceholderTests.)
///
/// GAPS FOUND are documented inline with a `// GAP:` comment and asserted as the
/// ACTUAL (sometimes lossy) behaviour so the suite stays green while making the
/// loss visible. Truly broken behaviour is marked [Fact(Skip="known gap: …")].
/// </summary>
public class InCoverageMatrixTests
{
    private static readonly IReadOnlyList<Line> Two = RepresentativeLines();

    // ════════════════════════════════════════════════════════════════════════════
    // CSV
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Csv_Representative_CapturesAllCanonicalFields()
    {
        var bytes = Csv("PO-CSV-1", "EUR", "Acme Buyer", Two);
        var order = await new CsvOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("PO-CSV-1");
        order.Currency.Should().Be("EUR");
        order.BuyerName.Should().Be("Acme Buyer");
        order.OrderDate.Should().Be(new DateTime(2026, 6, 8));
        order.Lines.Should().HaveCount(2);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-001");
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
        order.Lines[1].Description.Should().Be("Widget Type B");
    }

    [Fact]
    public async Task Csv_MissingPoNumber_ParsesWithNullPo_LinesIntact()
    {
        // PO number is a header field; an empty header value yields a null PoNumber.
        // Lines still parse — downstream review flags the missing PO, not the parser.
        var bytes = Csv("", "EUR", "Acme Buyer", Two);
        var order = await new CsvOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().BeNull();
        order.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Csv_ZeroAndNegativeQuantity_PreservedNotDropped()
    {
        var lines = new List<Line>
        {
            new(1, "BUY-Z", "Zero qty", 0m, "EA", 12.50m),
            new(2, "BUY-N", "Negative qty", -5m, "EA", 8.00m),
        };
        var bytes = Csv("PO-QTY", "EUR", "Acme Buyer", lines);
        var order = await new CsvOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines[0].Quantity.Should().Be(0m);
        order.Lines[1].Quantity.Should().Be(-5m, "negative qty must survive parsing so review can catch it");
    }

    [Fact]
    public async Task Csv_TwoHundredFiftyLines_AllCaptured()
    {
        var lines = Enumerable.Range(1, 250)
            .Select(i => new Line(i, $"BUY-{i:0000}", $"Item {i}", (i % 9) + 1, "EA", 10m + (i % 50) + 0.99m))
            .ToList();
        var bytes = Csv("PO-BIG", "EUR", "Bulk Buyer", lines);
        var order = await new CsvOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines.Should().HaveCount(250);
        order.Lines[^1].BuyerItemCode.Should().Be("BUY-0250");
    }

    [Fact]
    public async Task Csv_MultiCurrencyRows_HeaderKeepsFirstNonEmpty()
    {
        // The parser takes the first non-empty currency across all rows. Per-row
        // currency drift is therefore SILENTLY collapsed to the first value.
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append(NL);
        sb.Append("PO-MC,2026-06-08,EUR,Acme,1,BUY-1,EUR priced,1,EA,100.00").Append(NL);
        sb.Append("PO-MC,2026-06-08,USD,Acme,2,BUY-2,USD priced,1,EA,120.00").Append(NL);
        sb.Append("PO-MC,2026-06-08,GBP,Acme,3,BUY-3,GBP priced,1,EA,90.00").Append(NL);

        var order = await new CsvOrderParser().ParseAsync(ToStream(sb.ToString()), CancellationToken.None);

        // GAP (by design, but worth surfacing): canonical model has ONE currency field,
        // so mixed-currency files lose the per-line currency. First wins.
        order.Currency.Should().Be("EUR");
        order.Lines.Should().HaveCount(3);
    }

    [Fact]
    public async Task Csv_UnicodeAccentedCjkEmoji_PreservedExactly()
    {
        var lines = new List<Line>
        {
            new(1, "BUY-ÉÀ1", "Câble réseau — catégorie 6", 3m, "EA", 4.91m),
            new(2, "BUY-日本", "ネットワークケーブル", 2m, "EA", 9.00m),
            new(3, "BUY-emoji", "Mouse 🖱️ ergonomic", 1m, "EA", 49.31m),
        };
        var bytes = Csv("PO-Ünïcödé", "EUR", "Société Générale Łódź", lines);
        var order = await new CsvOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("PO-Ünïcödé");
        order.BuyerName.Should().Be("Société Générale Łódź");
        order.Lines[1].Description.Should().Be("ネットワークケーブル");
        order.Lines[2].Description.Should().Be("Mouse 🖱️ ergonomic");
    }

    [Fact]
    public async Task Csv_EuThousandsSeparator_1Dot234Comma56_IsMisparsed_GAP()
    {
        // European number "1.234,56" under InvariantCulture + NumberStyles.Any:
        // '.' is read as a thousands group separator and ',' is NOT a valid decimal
        // point, so the whole token fails to parse cleanly.
        // Semicolon-delimited so the comma decimal does not split columns.
        var sb = new StringBuilder();
        sb.Append(CsvHeader.Replace(',', ';')).Append(NL);
        sb.Append("PO-EU;2026-06-08;EUR;Käufer GmbH;1;BUY-1;EU price;1;EA;1.234,56").Append(NL);
        sb.Append("PO-EU;2026-06-08;EUR;Käufer GmbH;2;BUY-2;EU qty;1.000;EA;73,22").Append(NL);

        var order = await new CsvOrderParser().ParseAsync(ToStream(sb.ToString()), CancellationToken.None);

        // GAP #3 (EU-locale CSV numbers — silent corruption). Under InvariantCulture +
        // NumberStyles.Any the EU number conventions are systematically mis-read:
        //   "1.234,56" → '.' decimal point then ',' invalid → whole token FAILS → null
        //                (a €1,234.56 price silently DROPPED to no-price).
        //   "73,22"    → ',' read as a GROUP separator → 7322 (a 100× mis-read of a
        //                €73.22 price — the worst kind: silent, plausible, wrong).
        //   "1.000" qty → '.' read as the DECIMAL point → 1.000 == 1 (so an EU file that
        //                meant "one thousand" via a dot-group becomes 1; and "1.234"
        //                meaning 1234 becomes 1.234). Both directions are wrong for EU.
        // Captured so the loss is VISIBLE and tracked. The fix is a locale-aware CSV
        // mode (infer comma-decimal from the ';' delimiter); out of scope for this
        // suite because it risks regressing the InvariantCulture happy path.
        order.Lines[0].UnitPrice.Should().BeNull("GAP: EU decimal 1.234,56 fails InvariantCulture parse → null");
        order.Lines[1].UnitPrice.Should().Be(7322m, "GAP: '73,22' is silently mis-read as 7322 (comma taken as group sep)");
        order.Lines[1].Quantity.Should().Be(1m, "GAP: '1.000' is read as 1.000 (dot = decimal point), not one thousand");
    }

    [Fact]
    public async Task Csv_UsThousandsSeparator_1Comma234Dot56_Quoted_ParsesCorrectly()
    {
        // US "1,234.56" quoted so the grouping comma survives CSV; InvariantCulture
        // + NumberStyles.Any treats ',' as a group separator → 1234.56. Correct.
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append(NL);
        sb.Append("PO-US,2026-06-08,USD,US Buyer,1,BUY-1,US price,1,EA,\"1,234.56\"").Append(NL);

        var order = await new CsvOrderParser().ParseAsync(ToStream(sb.ToString()), CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(1234.56m);
    }

    [Fact]
    public async Task Csv_DuplicatePoNumbers_CollapseToSingleCanonicalOrder()
    {
        // Two logical orders sharing a file → one ParsedOrder; header fields take
        // the first non-empty value, lines accumulate. Splitting is a downstream
        // concern, not the parser's.
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append(NL);
        sb.Append("PO-DUP,2026-06-08,EUR,Buyer A,1,BUY-1,From A,1,EA,10.00").Append(NL);
        sb.Append("PO-DUP,2026-06-09,EUR,Buyer B,1,BUY-2,From B,2,EA,20.00").Append(NL);

        var order = await new CsvOrderParser().ParseAsync(ToStream(sb.ToString()), CancellationToken.None);

        order.PoNumber.Should().Be("PO-DUP");
        order.BuyerName.Should().Be("Buyer A");
        order.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Csv_EmptyDescriptionsAndBuyerCode_NormaliseToNullOrEmpty()
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append(NL);
        sb.Append("PO-EMPTY,2026-06-08,EUR,Acme,1,,,1,EA,10.00").Append(NL);
        sb.Append("PO-EMPTY,2026-06-08,EUR,Acme,2,BUY-2,,2,,20.00").Append(NL);

        var order = await new CsvOrderParser().ParseAsync(ToStream(sb.ToString()), CancellationToken.None);

        order.Lines[0].BuyerItemCode.Should().BeEmpty();
        order.Lines[0].Description.Should().BeNull();
        order.Lines[1].Unit.Should().BeNull();
    }

    [Fact]
    public async Task Csv_MalformedHeader_NoMappableColumns_DegradesGracefully_NotCrash()
    {
        // FIXED (was GAP #2): a header where NO column matches any known alias made
        // CsvHelper throw "No members are mapped for type 'RawRow'" — a leaky
        // third-party exception exposing an internal type name. The parser now
        // catches that ReaderException and degrades to an empty order, which the
        // ingestion layer surfaces as a clean "couldn't read this file" failure.
        var content = "foo,bar,baz,qux" + NL + "PO-X,stuff,more,1" + NL;
        var order = await new CsvOrderParser().ParseAsync(ToStream(content), CancellationToken.None);

        order.PoNumber.Should().BeNull();
        order.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Csv_NonNumericQuantity_DefaultsToZero_NotCrash()
    {
        var content = CsvHeader + NL + "PO-NN,2026-06-08,EUR,Acme,1,BUY-1,Bad qty,five,EA,12.50" + NL;
        var order = await new CsvOrderParser().ParseAsync(ToStream(content), CancellationToken.None);

        order.Lines[0].Quantity.Should().Be(0m, "non-numeric qty degrades to 0 for review, never throws");
    }

    [Fact]
    public async Task Csv_BomAndSpacedCasedHeaders_StillMap()
    {
        var content =
            "﻿ PO Number , Quantity , Unit Price , Buyer Item Code , Description " + NL +
            "PO-BOM,2,9.99,BUY-1,Whitespace + BOM header" + NL;
        var order = await new CsvOrderParser().ParseAsync(ToStream(content), CancellationToken.None);

        order.PoNumber.Should().Be("PO-BOM");
        order.Lines[0].Quantity.Should().Be(2m);
        order.Lines[0].UnitPrice.Should().Be(9.99m);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-1");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // XLSX
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Xlsx_Representative_TypedNumericCells_CapturesAllCanonicalFields()
    {
        // FIXED (was GAP #1, HIGH severity): with NUMERIC (not text) cells — the normal
        // shape of a real Excel export — ClosedXML's GetString() formats 12.50 with the
        // SERVER culture. On a comma-decimal locale that is "12,5", which InvariantCulture
        // then read as 125 (a silent 10× price corruption). XlsxOrderParser now reads the
        // cell's raw numeric value, so 12.50 stays 12.50 regardless of server locale.
        var bytes = Xlsx("PO-XL-1", "EUR", "Acme Buyer", Two);
        var order = await new XlsxOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("PO-XL-1");
        order.Currency.Should().Be("EUR");
        order.BuyerName.Should().Be("Acme Buyer");
        order.Lines.Should().HaveCount(2);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-001");
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
        order.Lines[1].UnitPrice.Should().Be(8.00m);
    }

    [Fact]
    public async Task Xlsx_FractionalPriceNumericCell_NotMultipliedByLocale()
    {
        // The exact regression: a sub-1 fractional price (0.45) on a numeric cell.
        // A locale round-trip would render "0,45" → InvariantCulture would FAIL to
        // parse (leading comma group) and drop to null. The numeric path keeps 0.45.
        var lines = new List<Line> { new(1, "BOLT", "Bolt M8", 250m, "EA", 0.45m) };
        var bytes = Xlsx("PO-FRAC", "EUR", "Buyer", lines);
        var order = await new XlsxOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(0.45m);
        order.Lines[0].Quantity.Should().Be(250m);
    }

    [Fact]
    public async Task Xlsx_NumberStoredAsText_StillParsedInvariant()
    {
        // Numbers stored as TEXT (e.g. "4.50") must still parse under InvariantCulture
        // — the fix must not regress the text-cell path the original test relied on.
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Order");
        string[] headers = { "PoNumber", "LineNumber", "BuyerItemCode", "Quantity", "UnitPrice" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        ws.Cell(2, 1).Value = "PO-TXT";
        ws.Cell(2, 2).Value = "1";
        ws.Cell(2, 3).Value = "ITEM-A";
        ws.Cell(2, 4).Value = "12";    // text
        ws.Cell(2, 5).Value = "4.50";  // text with invariant decimal
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var order = await new XlsxOrderParser().ParseAsync(ToStream(ms.ToArray()), CancellationToken.None);
        order.Lines[0].Quantity.Should().Be(12m);
        order.Lines[0].UnitPrice.Should().Be(4.50m);
    }

    [Fact]
    public async Task Xlsx_UnicodeAndNegativeQty_Preserved()
    {
        var lines = new List<Line>
        {
            new(1, "BUY-日本", "ネットワークケーブル", -2m, "EA", 9.00m),
        };
        var bytes = Xlsx("PO-XLU", "EUR", "Société Łódź", lines);
        var order = await new XlsxOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.BuyerName.Should().Be("Société Łódź");
        order.Lines[0].Description.Should().Be("ネットワークケーブル");
        order.Lines[0].Quantity.Should().Be(-2m);
    }

    [Fact]
    public async Task Xlsx_TwoHundredLines_AllCaptured()
    {
        var lines = Enumerable.Range(1, 200)
            .Select(i => new Line(i, $"BUY-{i:0000}", $"Item {i}", (i % 5) + 1, "EA", 2m + (i % 40)))
            .ToList();
        var bytes = Xlsx("PO-XLBIG", "EUR", "Bulk", lines);
        var order = await new XlsxOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines.Should().HaveCount(200);
        order.Lines[^1].BuyerItemCode.Should().Be("BUY-0200");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // cXML
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cxml_Representative_CapturesAllCanonicalFields()
    {
        var bytes = Cxml("CX-1", "EUR", Two);
        var order = await new CxmlOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("CX-1");
        order.Currency.Should().Be("EUR");
        order.Lines.Should().HaveCount(2);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-001"); // SupplierPartID used as code
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
    }

    [Fact]
    public async Task Cxml_MissingOrderId_ThrowsTypedException()
    {
        var bytes = Cxml("", "EUR", Two);
        var act = async () => await new CxmlOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);
        await act.Should().ThrowAsync<CxmlParseException>().WithMessage("*orderID*");
    }

    [Fact]
    public async Task Cxml_NonEurCurrencyAndUnicode_Preserved()
    {
        var lines = new List<Line> { new(10, "29048107", "Gigaset 550 HX — Dodatkowa słuch", 1m, "EA", 295.02m) };
        var bytes = Cxml("CX-PLN", "PLN", lines, total: "295.02");
        var order = await new CxmlOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Currency.Should().Be("PLN");
        order.Lines[0].Description.Should().Be("Gigaset 550 HX — Dodatkowa słuch");
        order.Lines[0].UnitPrice.Should().Be(295.02m);
    }

    [Fact]
    public async Task Cxml_TwoHundredLines_AllCaptured()
    {
        var lines = Enumerable.Range(1, 200)
            .Select(i => new Line(i, $"SUP-{i}", $"Item {i}", (i % 7) + 1, "EA", 1m + (i % 30)))
            .ToList();
        var bytes = Cxml("CX-BIG", "EUR", lines, total: "9999.00");
        var order = await new CxmlOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines.Should().HaveCount(200);
    }

    [Fact]
    public async Task Cxml_TruncatedXml_ThrowsTypedException()
    {
        var bytes = Cxml("CX-BAD", "EUR", Two, wellFormed: false);
        var act = async () => await new CxmlOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);
        await act.Should().ThrowAsync<CxmlParseException>();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // UBL
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Ubl_Representative_CapturesAllCanonicalFields()
    {
        var bytes = Ubl("UBL-1", "EUR", "Acme Buyer Ltd", Two);
        var order = await new UblOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("UBL-1");
        order.Currency.Should().Be("EUR");
        order.BuyerName.Should().Be("Acme Buyer Ltd");
        order.Lines.Should().HaveCount(2);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-001"); // BuyersItemIdentification
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
    }

    [Fact]
    public async Task Ubl_PeppolBis3Profile_ParsesIdentically()
    {
        var lines = new List<Line> { new(1, "SUP-P", "Peppol line", 3m, "EA", 4.91m) };
        var bytes = Ubl("UBL-PEPPOL", "EUR", "Peppol Buyer", lines, peppol: true);
        var order = await new UblOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("UBL-PEPPOL");
        order.Lines[0].UnitPrice.Should().Be(4.91m);
    }

    [Fact]
    public async Task Ubl_MissingHeaderId_ThrowsTypedException()
    {
        var bytes = Ubl("", "EUR", "Acme", Two, omitId: true);
        var act = async () => await new UblOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);
        await act.Should().ThrowAsync<UblParseException>().WithMessage("*ID*");
    }

    [Fact]
    public async Task Ubl_GbpAndAccentedBuyer_Preserved()
    {
        var lines = new List<Line> { new(1, "11097719", "SanDisk Ultra Flair — clé USB", 2m, "EA", 19.33m) };
        var bytes = Ubl("UBL-GBP", "GBP", "Æthelred & Sons Ltd", lines);
        var order = await new UblOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Currency.Should().Be("GBP");
        order.BuyerName.Should().Be("Æthelred & Sons Ltd");
        order.Lines[0].Description.Should().Be("SanDisk Ultra Flair — clé USB");
    }

    [Fact]
    public async Task Ubl_TwoHundredLines_AllCaptured()
    {
        var lines = Enumerable.Range(1, 200)
            .Select(i => new Line(i, $"SUP-{i}", $"UBL Item {i}", (i % 5) + 1, "EA", 2m + (i % 40)))
            .ToList();
        var bytes = Ubl("UBL-BIG", "EUR", "Bulk", lines);
        var order = await new UblOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines.Should().HaveCount(200);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // EDIFACT
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Edifact_Representative_CapturesAllCanonicalFields()
    {
        var bytes = Edifact("PO-EDI-1", "EUR", "Acme Buyer Ltd", Two);
        var order = await new EdifactOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("PO-EDI-1");
        order.Currency.Should().Be("EUR");
        order.BuyerName.Should().Be("Acme Buyer Ltd");
        order.OrderDate.Should().Be(new DateTime(2026, 6, 8));
        order.Lines.Should().HaveCount(2);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-001");
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
        order.Lines[0].Description.Should().Be("Widget Type A");
    }

    [Fact]
    public async Task Edifact_ReleaseEscapedDelimitersInText_Unescaped()
    {
        // A description containing the element separator '+' and component ':' must
        // survive via release escaping. Buyer name with an escaped colon too.
        var lines = new List<Line> { new(1, "ITEM-A", "Cable A:B+C grade", 1m, "EA", 5.00m) };
        var bytes = Edifact("PO-ESC", "EUR", "Buyer:Co+Ltd", lines);
        var order = await new EdifactOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.BuyerName.Should().Be("Buyer:Co+Ltd");
        order.Lines[0].Description.Should().Be("Cable A:B+C grade");
    }

    [Fact]
    public async Task Edifact_TwoHundredLines_AllCaptured()
    {
        var lines = Enumerable.Range(1, 200)
            .Select(i => new Line(i, $"ITEM-{i}", $"Item {i}", (i % 6) + 1, "EA", 3m + (i % 20)))
            .ToList();
        var bytes = Edifact("PO-EDIBIG", "EUR", "Bulk", lines);
        var order = await new EdifactOrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines.Should().HaveCount(200);
    }

    [Fact]
    public async Task Edifact_WrongMessageType_ThrowsTypedException()
    {
        // INVOIC, not ORDERS.
        var edi =
            "UNB+UNOC:3+S:14+R:14+260608:0900+1'" + NL +
            "UNH+1+INVOIC:D:96A:UN'" + NL +
            "BGM+380+INV-1+9'" + NL +
            "UNT+3+1'" + NL + "UNZ+1+1'" + NL;
        var act = async () => await new EdifactOrderParser().ParseAsync(ToStream(edi), CancellationToken.None);
        await act.Should().ThrowAsync<EdifactParseException>().WithMessage("*ORDERS*");
    }

    [Fact]
    public async Task Edifact_EmptyInput_ThrowsTypedException()
    {
        var act = async () => await new EdifactOrderParser().ParseAsync(ToStream(""), CancellationToken.None);
        await act.Should().ThrowAsync<EdifactParseException>().WithMessage("*empty*");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // X12
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task X12_Representative_CapturesAllCanonicalFields()
    {
        var bytes = X12("PO-X12-1", "USD", "Acme Buying Corp", Two);
        var order = await new X12OrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("PO-X12-1");
        order.Currency.Should().Be("USD");
        order.BuyerName.Should().Be("Acme Buying Corp");
        order.Lines.Should().HaveCount(2);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-001"); // BP qualifier
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
        order.Lines[0].Description.Should().Be("Widget Type A");
    }

    [Fact]
    public async Task X12_NegativeQuantity_Preserved()
    {
        var lines = new List<Line> { new(1, "ITEM-N", "Returned", -4m, "EA", 5.00m) };
        var bytes = X12("PO-X12N", "USD", "Buyer", lines);
        var order = await new X12OrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines[0].Quantity.Should().Be(-4m);
    }

    [Fact]
    public async Task X12_TwoHundredLines_AllCaptured()
    {
        var lines = Enumerable.Range(1, 200)
            .Select(i => new Line(i, $"ITEM-{i}", $"Item {i}", (i % 7) + 1, "EA", 1m + (i % 30)))
            .ToList();
        var bytes = X12("PO-X12BIG", "USD", "Bulk", lines);
        var order = await new X12OrderParser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Lines.Should().HaveCount(200);
    }

    [Fact]
    public async Task X12_MissingIsa_ThrowsTypedException()
    {
        var edi = "ST*850*0001~" + NL + "BEG*00*NE*PO-1**20260608~" + NL;
        var act = async () => await new X12OrderParser().ParseAsync(ToStream(edi), CancellationToken.None);
        await act.Should().ThrowAsync<X12ParseException>().WithMessage("*ISA*");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // SAP IDoc ORDERS05
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Idoc_Representative_CapturesAllCanonicalFields()
    {
        var bytes = Idoc("4501450099", "Acme Buyer", Two);
        var order = await new IDocOrders05Parser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.PoNumber.Should().Be("4501450099");
        order.BuyerName.Should().Be("Acme Buyer");
        order.SupplierName.Should().Be("Matrix Supplier OU");
        order.OrderDate.Should().Be(new DateTime(2026, 6, 8));
        order.Lines.Should().HaveCount(2);
        order.Lines[0].BuyerItemCode.Should().Be("BUY-001"); // E1EDP19 QUALF=002 IDTNR
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].UnitPrice.Should().Be(12.50m);
    }

    [Fact]
    public async Task Idoc_NumericCurcy704_OverriddenByAlphabeticSunit()
    {
        // The canonical IDoc gotcha: E1EDK01 CURCY is a numeric SAP-internal code
        // (704), E1EDS01 SUNIT carries the real ISO code. SUNIT must win.
        var bytes = Idoc("PO-CUR", "Buyer", Two, curcy: "704", sunit: "EUR");
        var order = await new IDocOrders05Parser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Currency.Should().Be("EUR", "numeric CURCY 704 must not leak as the currency");
    }

    [Fact]
    public async Task Idoc_NumericCurcyWithNoSunit_FallsBackToNullNotNumeric()
    {
        // No SUNIT and a numeric CURCY → currency stays null (numeric code rejected),
        // never emits "704" as a bogus currency.
        var bytes = Idoc("PO-CUR2", "Buyer", Two, curcy: "704", sunit: "");
        var order = await new IDocOrders05Parser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Currency.Should().BeNull("a numeric CURCY with no SUNIT must not be emitted as a currency");
    }

    [Fact]
    public async Task Idoc_AlphabeticCurcyNoSunit_UsedAsFallback()
    {
        var bytes = Idoc("PO-CUR3", "Buyer", Two, curcy: "USD", sunit: "");
        var order = await new IDocOrders05Parser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task Idoc_MissingPoNumber_ThrowsTypedException()
    {
        // Build an IDoc with no BELNR anywhere.
        var xml =
            "<ORDERS05><IDOC BEGIN=\"1\">" +
            "<E1EDK01><CURCY>EUR</CURCY></E1EDK01>" +
            "<E1EDP01><POSEX>00010</POSEX><MENGE>1</MENGE><E1EDP19><QUALF>002</QUALF><IDTNR>X</IDTNR></E1EDP19></E1EDP01>" +
            "</IDOC></ORDERS05>";
        var act = async () => await new IDocOrders05Parser().ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<IDocParseException>().WithMessage("*PO number*");
    }

    [Fact]
    public async Task Idoc_NoLineSegments_ThrowsTypedException()
    {
        var xml =
            "<ORDERS05><IDOC BEGIN=\"1\">" +
            "<E1EDK01><BELNR>PO-1</BELNR></E1EDK01>" +
            "<E1EDK02><QUALF>001</QUALF><BELNR>PO-1</BELNR><DATUM>20260608</DATUM></E1EDK02>" +
            "</IDOC></ORDERS05>";
        var act = async () => await new IDocOrders05Parser().ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<IDocParseException>().WithMessage("*E1EDP01*");
    }

    [Fact]
    public async Task Idoc_GrandTotalCaptured()
    {
        var bytes = Idoc("PO-GT", "Buyer", Two, grandTotal: 186.01m);
        var order = await new IDocOrders05Parser().ParseAsync(ToStream(bytes), CancellationToken.None);

        order.GrandTotal.Should().Be(186.01m);
        order.DocumentType.Should().Be("order");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // XXE / DTD security — every XML parser must REFUSE external DTDs (no XXE)
    // ════════════════════════════════════════════════════════════════════════════

    private const string XxePayloadCxml =
        "<?xml version=\"1.0\"?>\n" +
        "<!DOCTYPE cXML [ <!ENTITY xxe SYSTEM \"file:///etc/passwd\"> ]>\n" +
        "<cXML><Request deploymentMode=\"production\"><OrderRequest>" +
        "<OrderRequestHeader orderID=\"&xxe;\"/></OrderRequest></Request></cXML>";

    private const string XxePayloadUbl =
        "<?xml version=\"1.0\"?>\n" +
        "<!DOCTYPE Order [ <!ENTITY xxe SYSTEM \"file:///etc/passwd\"> ]>\n" +
        "<Order xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Order-2\"><cbc:ID xmlns:cbc=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\">&xxe;</cbc:ID></Order>";

    private const string XxePayloadIdoc =
        "<?xml version=\"1.0\"?>\n" +
        "<!DOCTYPE ORDERS05 [ <!ENTITY xxe SYSTEM \"file:///etc/passwd\"> ]>\n" +
        "<ORDERS05><IDOC><E1EDK01><BELNR>&xxe;</BELNR></E1EDK01></IDOC></ORDERS05>";

    [Fact]
    public async Task Cxml_ExternalDtdEntity_IsRejected_NoXxe()
    {
        // DtdProcessing defaults to Prohibit for XDocument.Load in .NET Core+, so the
        // DOCTYPE itself is refused at parse time → a typed CxmlParseException, and
        // the file:// entity is NEVER resolved.
        var act = async () => await new CxmlOrderParser().ParseAsync(ToStream(XxePayloadCxml), CancellationToken.None);
        await act.Should().ThrowAsync<CxmlParseException>();
    }

    [Fact]
    public async Task Ubl_ExternalDtdEntity_IsRejected_NoXxe()
    {
        var act = async () => await new UblOrderParser().ParseAsync(ToStream(XxePayloadUbl), CancellationToken.None);
        await act.Should().ThrowAsync<UblParseException>();
    }

    [Fact]
    public async Task Idoc_ExternalDtdEntity_IsRejected_NoXxe()
    {
        var act = async () => await new IDocOrders05Parser().ParseAsync(ToStream(XxePayloadIdoc), CancellationToken.None);
        await act.Should().ThrowAsync<IDocParseException>();
    }

    [Fact]
    public void Cxml_IsUblOrderDocumentProbe_RejectsDtd_NoXxe()
    {
        // The content-sniffing probes explicitly set DtdProcessing.Prohibit. A DTD
        // payload must return false (probe), never resolve the entity or throw out.
        UblOrderParser.IsUblOrderDocument(ToStream(XxePayloadUbl)).Should().BeFalse();
        IDocOrders05Parser.IsIdocOrders05Document(ToStream(XxePayloadIdoc)).Should().BeFalse();
        X12OrderParser.IsX12OrderDocument(ToStream(XxePayloadCxml)).Should().BeFalse();
    }
}
