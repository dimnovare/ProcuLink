using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// Locale + decimal-fidelity guards for the file parsers.
///
/// <para>Every test here is fed the ORIGINAL defect verbatim, because each of these
/// values was silently corrupted at some point and nothing in the suite noticed:</para>
/// <list type="bullet">
///   <item>a tab- or comma-delimited European CSV read "1.000" as 1.0 — the EU-locale
///     signal was hard-wired to the ';' delimiter alone, and the only EU-locale tests in
///     the suite were semicolon-delimited, so the other two delimiters were never
///     exercised;</item>
///   <item>an XLSX text cell containing "73,22" was read as 7322 — a hundredfold error on
///     a purchase-order price — and <see cref="XlsxOrderParser"/> was the one parser that
///     never set <c>NeedsReview</c>, so no human ever saw it.</item>
/// </list>
///
/// <para>The locale policy these tests pin is: a document's own declaration wins, then
/// whole-corpus evidence, and where a value is <i>genuinely</i> undecidable the answer is
/// <c>NeedsReview</c> — never a guess. "1.000" is one thousand in Germany and one in the
/// UK; a flagged line a person confirms is a good outcome, a silent hundredfold error is
/// the worst one.</para>
/// </summary>
public class ParserLocaleFidelityTests
{
    private const string NL = "\r\n";

    private static Stream ToStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    /// <summary>Builds one CSV row, quoting any field that contains the delimiter.</summary>
    private static string Row(string delimiter, params string[] fields) =>
        string.Join(delimiter, fields.Select(f =>
            f.Contains(delimiter, StringComparison.Ordinal) ? $"\"{f}\"" : f)) + NL;

    private static string Csv(string delimiter, string quantity, string unitPrice) =>
          Row(delimiter, "ponumber", "orderdate", "currency", "buyername", "linenumber",
                         "buyeritemcode", "description", "quantity", "unit", "unitprice")
        + Row(delimiter, "PO-LOC", "2026-06-08", "EUR", "Buyer", "1",
                         "BUY-1", "Item", quantity, "EA", unitPrice);

    // ════════════════════════════════════════════════════════════════════════
    // Defect 1 — the EU-locale signal was hard-wired to the ';' delimiter
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every delimiter <c>CsvOrderParser.DetectDelimiter</c> can return. The EU-locale
    /// coverage in the suite was semicolon-only, which is EXACTLY how the tab and comma
    /// cases survived — so the matrix below must stay pinned to the full set.
    /// </summary>
    public static readonly string[] AllDetectableDelimiters = { ",", ";", "\t" };

    public static IEnumerable<object[]> DelimiterMatrix() =>
        AllDetectableDelimiters.Select(d => new object[] { d });

    [Fact]
    public void DelimiterMatrix_CoversEveryDelimiterTheParserCanDetect()
    {
        // ANTI-VACUITY: a Theory that silently degenerates to one case is how the
        // original defect hid. Pin both the count and the exact membership.
        DelimiterMatrix().Should().HaveCount(3,
            "the EU-locale matrix must exercise comma, semicolon AND tab — semicolon-only " +
            "coverage is what let the tab/comma locale defect survive");
        AllDetectableDelimiters.Should().BeEquivalentTo(new[] { ",", ";", "\t" },
            "DetectDelimiter returns exactly these three; if it learns a fourth, this " +
            "matrix must grow with it");
    }

    [Theory]
    [MemberData(nameof(DelimiterMatrix))]
    public async Task EuCsv_AnyDelimiter_ThousandsGroupIsNotReadAsOne(string delimiter)
    {
        // THE DEFECT, VERBATIM: "1.000" is one thousand in a European document. With the
        // EU signal wired to ';' alone, a tab- or comma-delimited European file read it as
        // 1.0 — a thousandfold error on an order quantity. "73,22" in the same document is
        // decisive corpus evidence that the comma is this file's decimal separator.
        var order = await new CsvOrderParser()
            .ParseAsync(ToStream(Csv(delimiter, quantity: "1.000", unitPrice: "73,22")),
                        CancellationToken.None);

        order.Lines.Should().ContainSingle();
        order.Lines[0].Quantity.Should().Be(1000m,
            "'1.000' alongside a decisive '73,22' is a European thousands group — 1000, not 1.0");
        order.Lines[0].UnitPrice.Should().Be(73.22m);
        order.Lines[0].UnitPrice.Should().NotBe(7322m,
            "reading the decimal comma as a thousands separator is the hundredfold defect");
        order.Lines[0].NeedsReview.Should().BeFalse(
            "the document's own numbers decide the convention — nothing is being guessed");
    }

    [Theory]
    [MemberData(nameof(DelimiterMatrix))]
    public async Task EuCsv_AnyDelimiter_GroupedEuropeanPrice_ParsedNotCorrupted(string delimiter)
    {
        var order = await new CsvOrderParser()
            .ParseAsync(ToStream(Csv(delimiter, quantity: "2", unitPrice: "1.234,56")),
                        CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(1234.56m,
            "EU '1.234,56' — dot groups, comma decides — is 1234.56 under every delimiter");
        order.Lines[0].NeedsReview.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(DelimiterMatrix))]
    public async Task UsCsv_AnyDelimiter_GroupedThousandsAndPointDecimal_StillCorrect(string delimiter)
    {
        // DO NOT REGRESS THE WORKING CASE: a UK/US file must keep parsing correctly under
        // every delimiter, including the one that now also carries an EU signal.
        var order = await new CsvOrderParser()
            .ParseAsync(ToStream(Csv(delimiter, quantity: "1,000", unitPrice: "1,234.56")),
                        CancellationToken.None);

        order.Lines[0].Quantity.Should().Be(1000m, "US '1,000' is a thousands group");
        order.Lines[0].UnitPrice.Should().Be(1234.56m, "US '1,234.56' is 1234.56");
        order.Lines[0].NeedsReview.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(DelimiterMatrix))]
    public async Task Csv_UndecidableSeparator_IsFlagged_NotGuessed(string delimiter)
    {
        // "1.000" with NO other numeric evidence in the document is genuinely undecidable:
        // one thousand in Germany, one in the UK. Policy: flag it for a human.
        // A ';' delimiter IS a locale declaration (Excel writes it in comma-decimal
        // locales), so that one case stays decidable.
        var order = await new CsvOrderParser()
            .ParseAsync(ToStream(Csv(delimiter, quantity: "2", unitPrice: "1.000")),
                        CancellationToken.None);

        // Unconditional first, so the branch below cannot pass by never running: the row must
        // really have been read, and its unambiguous quantity must be right under every
        // delimiter. Only the price is the undecidable value under test.
        order.Lines.Should().ContainSingle();
        order.Lines[0].Quantity.Should().Be(2m,
            "an unambiguous quantity parses identically under every delimiter");

        if (delimiter == ";")
        {
            order.Lines[0].UnitPrice.Should().Be(1000m,
                "a ';'-delimited file declares a comma-decimal locale — that is a " +
                "declaration, not a guess");
            order.Lines[0].NeedsReview.Should().BeFalse();
        }
        else
        {
            order.Lines[0].UnitPrice.Should().BeNull(
                "an undecidable separator must never be resolved by guessing");
            order.Lines[0].NeedsReview.Should().BeTrue(
                "a human reviewing a flagged line is a good outcome; a silent " +
                "thousandfold error is the worst one");
            order.Lines[0].ReviewReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Defect 2 — XLSX read "73,22" as 7322 and never flagged anything
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a one-line XLSX whose Quantity/UnitPrice cells are TEXT, which is the cell
    /// type that took the corrupting <c>NumberStyles.Any</c> + InvariantCulture path.
    /// </summary>
    private static Stream XlsxWithTextNumbers(string quantity, string unitPrice)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");

        var headers = new[]
        {
            "PoNumber", "BuyerName", "Currency", "OrderDate",
            "LineNumber", "BuyerItemCode", "Description", "Quantity", "UnitPrice",
        };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        var row = new[]
        {
            "PO-XLSX", "Buyer", "EUR", "2026-06-08", "1", "BUY-1", "Item", quantity, unitPrice,
        };
        for (var c = 0; c < row.Length; c++) ws.Cell(2, c + 1).Value = row[c];

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Xlsx_TextCell_EuropeanDecimalComma_IsNotReadAsHundredfold()
    {
        // THE DEFECT, VERBATIM: "73,22" was read as 7322. A hundredfold error on a
        // purchase-order price, in the one parser that never set NeedsReview — so nothing
        // flagged it, no human saw it, and the wrong number flowed out to the supplier.
        await using var stream = XlsxWithTextNumbers(quantity: "3", unitPrice: "73,22");

        var order = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        order.Lines.Should().ContainSingle();
        order.Lines[0].UnitPrice.Should().NotBe(7322m,
            "reading '73,22' as 7322 is the hundredfold corruption being fixed");
        order.Lines[0].UnitPrice.Should().Be(73.22m,
            "a lone comma with two trailing digits is decisively a decimal separator");
        order.Lines[0].NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Xlsx_TextCell_GroupedEuropeanPrice_ParsedNotCorrupted()
    {
        await using var stream = XlsxWithTextNumbers(quantity: "1.000", unitPrice: "1.234,56");

        var order = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(1234.56m, "EU '1.234,56' is 1234.56");
        order.Lines[0].Quantity.Should().Be(1000m,
            "'1.000' is a thousands group once '1.234,56' has settled the convention");
        order.Lines[0].NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Xlsx_TextCell_UsGroupedPrice_StillCorrect()
    {
        // DO NOT REGRESS THE WORKING CASE.
        await using var stream = XlsxWithTextNumbers(quantity: "1,000", unitPrice: "1,234.56");

        var order = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(1234.56m);
        order.Lines[0].Quantity.Should().Be(1000m);
        order.Lines[0].NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Xlsx_TextCell_UndecidableSeparator_IsFlagged_NotGuessed()
    {
        // No other numeric evidence anywhere in the sheet, and a spreadsheet carries no
        // delimiter to declare a locale — so "1.000" is genuinely undecidable.
        await using var stream = XlsxWithTextNumbers(quantity: "2", unitPrice: "1.000");

        var order = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        order.Lines[0].UnitPrice.Should().BeNull();
        order.Lines[0].NeedsReview.Should().BeTrue(
            "XLSX was the only parser that never flagged anything — that is why the " +
            "hundredfold price error reached the supplier unseen");
        order.Lines[0].ReviewReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Xlsx_TextCell_NonNumericPrice_IsFlagged_NotSilentlyDropped()
    {
        await using var stream = XlsxWithTextNumbers(quantity: "5", unitPrice: "abc");

        var order = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        order.Lines[0].UnitPrice.Should().BeNull();
        order.Lines[0].NeedsReview.Should().BeTrue(
            "a garbage price must surface for review, not vanish to null");
    }

    [Fact]
    public async Task Xlsx_NumericTypedCells_StayExact_AndUnflagged()
    {
        // DO NOT REGRESS: a real numeric cell carries a locale-free double. It must keep
        // bypassing all string/locale logic and must never be flagged.
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");
        var headers = new[] { "PoNumber", "LineNumber", "BuyerItemCode", "Quantity", "UnitPrice" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        ws.Cell(2, 1).Value = "PO-NUM";
        ws.Cell(2, 2).Value = 1;
        ws.Cell(2, 3).Value = "BUY-1";
        ws.Cell(2, 4).Value = 12;
        ws.Cell(2, 5).Value = 4.5;

        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var order = await new XlsxOrderParser().ParseAsync(ms, CancellationToken.None);

        order.Lines[0].Quantity.Should().Be(12m);
        order.Lines[0].UnitPrice.Should().Be(4.5m);
        order.Lines[0].NeedsReview.Should().BeFalse(
            "a typed numeric cell is locale-free and unambiguous");
    }

    // ════════════════════════════════════════════════════════════════════════
    // The shared inference primitive
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    // Both separators present — the last one decides, decisively.
    [InlineData("1.234,56", DecimalConvention.Comma)]
    [InlineData("1,234.56", DecimalConvention.Point)]
    // A lone separator with a trailing-digit count that cannot be a thousands group.
    [InlineData("73,22", DecimalConvention.Comma)]
    [InlineData("12.50", DecimalConvention.Point)]
    // A repeated separator can only be a group separator, so the OTHER one decides.
    [InlineData("1.234.567", DecimalConvention.Comma)]
    [InlineData("1,234,567", DecimalConvention.Point)]
    // Three trailing digits, but an integer part that cannot be a thousands group.
    [InlineData("0,500", DecimalConvention.Comma)]
    [InlineData("1234.567", DecimalConvention.Point)]
    // Genuinely undecidable, and therefore refused.
    [InlineData("1.000", DecimalConvention.Unknown)]
    [InlineData("1,000", DecimalConvention.Unknown)]
    [InlineData("42", DecimalConvention.Unknown)]
    [InlineData("", DecimalConvention.Unknown)]
    public void InferDecimalConvention_ReadsTheEvidenceInOneToken(string token, DecimalConvention expected)
        => NumberParsing.InferDecimalConvention(new[] { token }).Should().Be(expected);

    [Fact]
    public void InferDecimalConvention_OneDecisiveToken_SettlesTheAmbiguousOnes()
        => NumberParsing.InferDecimalConvention(new[] { "1.000", "73,22" })
            .Should().Be(DecimalConvention.Comma,
                "corpus evidence — not a single cell — is what resolves '1.000'");

    [Fact]
    public void InferDecimalConvention_ContradictoryCorpus_RefusesToDecide()
        => NumberParsing.InferDecimalConvention(new[] { "1.234,56", "1,234.56" })
            .Should().Be(DecimalConvention.Unknown,
                "a document that contradicts itself gives no answer — it must fall back to " +
                "a declared locale, or be flagged");

    [Fact]
    public void TryParseFlexibleDecimal_UnknownConvention_RefusesTheAmbiguousToken()
    {
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal("1.000", DecimalConvention.Unknown);

        value.Should().BeNull();
        ambiguous.Should().BeTrue("with nothing to decide on, the parser must not guess");
    }

    [Fact]
    public void TryParseFlexibleDecimal_UnknownConvention_StillReadsAnUnambiguousToken()
    {
        // Refusing must stay narrow. A plain integer and a decisive decimal need no convention.
        NumberParsing.TryParseFlexibleDecimal("42", DecimalConvention.Unknown)
            .Should().Be(((decimal?)42m, false));
        NumberParsing.TryParseFlexibleDecimal("73,22", DecimalConvention.Unknown)
            .Should().Be(((decimal?)73.22m, false));
    }

    [Theory]
    [InlineData(DecimalConvention.Comma, 1000)]
    [InlineData(DecimalConvention.Point, 1)]
    public void TryParseFlexibleDecimal_KnownConvention_ResolvesTheAmbiguousToken(
        DecimalConvention convention, double expected)
    {
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal("1.000", convention);

        value.Should().Be((decimal)expected);
        ambiguous.Should().BeFalse();
    }

    [Fact]
    public void FirstKnown_PrefersTheEarlierEvidence_AndSurvivesHavingNone()
    {
        NumberParsing.FirstKnown(DecimalConvention.Unknown, DecimalConvention.Comma, DecimalConvention.Point)
            .Should().Be(DecimalConvention.Comma);
        NumberParsing.FirstKnown(DecimalConvention.Unknown, DecimalConvention.Unknown)
            .Should().Be(DecimalConvention.Unknown);
    }
}
