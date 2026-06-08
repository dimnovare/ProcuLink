using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;
using static ProcuLink.Transform.Tests.FormatMatrix.FormatFixtures;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// Numeric-token-safety leg of the FormatMatrix stress suite.
///
/// Pins the fix for the SILENT-WRONG-VALUE bug found in the live prod CSV stress run:
/// a unit_price written in scientific notation ("1.5e2", i.e. 150) was silently parsed
/// as 1.52 — the shared number-normalization helper stripped the 'e' and concatenated
/// the digits. A silently-wrong number is the worst failure mode (it delivers a corrupt
/// PO to a supplier), so the parser must instead REFUSE the value and flag the line for
/// review (it surfaces in /operations/exceptions via PurchaseOrderLineEntity.NeedsReview).
///
/// These tests also guard against regressing the locale logic that the same helper
/// (correctly) handles — EU comma-decimals, EU/US thousands groups, currency symbols,
/// whitespace padding — which must stay un-flagged (see HighVolumeMatrixTests for the
/// exhaustive locale stress).
/// </summary>
public class NumericTokenSafetyMatrixTests
{
    private static byte[] OneLineCsv(string quantity, string unitPrice, char delimiter = ',')
    {
        // Join fields with the delimiter DIRECTLY — never build with ',' then Replace, or a
        // ',' inside an EU decimal value (e.g. "1.234,56") would be corrupted into "1.234;56".
        var d = delimiter.ToString();
        var header = string.Join(d,
            "ponumber", "orderdate", "currency", "buyername", "linenumber",
            "buyeritemcode", "description", "quantity", "unit", "unitprice");
        var row = string.Join(d,
            "PO-NUM", "2026-06-08", "EUR", "Buyer", "1", "BUY-1", "Item", quantity, "EA", unitPrice);
        return Encoding.UTF8.GetBytes(header + NL + row + NL);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Scientific notation — the silent-wrong-value bug
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Csv_ScientificNotationUnitPrice_NotSilentlyWrong_FlaggedForReview()
    {
        // "1.5e2" means 150. The OLD bug stripped the 'e' → "1.52" (a silent, plausible,
        // catastrophically-wrong price). The parser must NOT emit 1.52: it refuses the
        // ambiguous token (null price) and flags the line for review.
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(OneLineCsv(quantity: "1", unitPrice: "1.5e2")), CancellationToken.None);

        order.Lines.Should().ContainSingle();
        order.Lines[0].UnitPrice.Should().NotBe(1.52m,
            "stripping the 'e' to produce 1.52 is the exact silent-wrong-value bug being fixed");
        order.Lines[0].UnitPrice.Should().BeNull(
            "an ambiguous/unparseable numeric token must not emit a guessed value");
        order.Lines[0].NeedsReview.Should().BeTrue(
            "a scientific-notation price the parser won't trust must surface for human review");
    }

    [Fact]
    public async Task Csv_ScientificNotationQuantity_NotSilentlyWrong_FlaggedForReview()
    {
        // "2e3" means 2000. The quantity column defaults a null to 0 (never throws), but
        // the line must still be flagged so the dropped value surfaces for review rather
        // than silently shipping a qty of 0.
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(OneLineCsv(quantity: "2e3", unitPrice: "10.00")), CancellationToken.None);

        order.Lines[0].Quantity.Should().Be(0m,
            "an unparseable quantity degrades to 0 (never throws) — but must be flagged");
        order.Lines[0].NeedsReview.Should().BeTrue(
            "a scientific-notation quantity the parser won't trust must surface for review");
    }

    [Fact]
    public async Task Csv_NonNumericPrice_FlaggedForReview_NotZeroedSilently()
    {
        // A garbage price token ("abc") must not be silently swallowed to null/0 without
        // a trace — flag it for review.
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(OneLineCsv(quantity: "5", unitPrice: "abc")), CancellationToken.None);

        order.Lines[0].UnitPrice.Should().BeNull();
        order.Lines[0].NeedsReview.Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Regression guard — legitimate numeric forms must stay un-flagged
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Csv_PlainInvariantPrice_ParsesAndNotFlagged()
    {
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(OneLineCsv(quantity: "10", unitPrice: "12.50")), CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(12.50m);
        order.Lines[0].Quantity.Should().Be(10m);
        order.Lines[0].NeedsReview.Should().BeFalse("a clean invariant decimal is not ambiguous");
    }

    [Fact]
    public async Task Csv_EuropeanDecimalPrice_ParsesAndNotFlagged()
    {
        // ';'-delimited EU locale: "1.234,56" → 1234.56, "73,22" qty stays clean.
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(OneLineCsv(quantity: "1.000", unitPrice: "1.234,56", delimiter: ';')),
            CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(1234.56m, "EU '1.234,56' → 1234.56 must not regress");
        order.Lines[0].Quantity.Should().Be(1000m, "EU '1.000' → 1000 must not regress");
        order.Lines[0].NeedsReview.Should().BeFalse("a valid EU-locale number is not ambiguous");
    }

    [Fact]
    public async Task Csv_CurrencySymbolAndWhitespacePrice_StrippedNotFlagged()
    {
        // Currency symbols and padding whitespace are legitimate noise the helper strips —
        // they must keep working and must NOT trip the ambiguity flag.
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(OneLineCsv(quantity: "3", unitPrice: "€12.50")), CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(12.50m, "a leading currency symbol must still be stripped");
        order.Lines[0].NeedsReview.Should().BeFalse("a currency symbol is expected noise, not ambiguity");

        var padded = await new CsvOrderParser().ParseAsync(
            ToStream(OneLineCsv(quantity: "3", unitPrice: "\" 12.50 \"")), CancellationToken.None);
        padded.Lines[0].UnitPrice.Should().Be(12.50m, "padding whitespace must still be stripped");
        padded.Lines[0].NeedsReview.Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // TSV (tab-delimited) auto-detection — coverage gap from the same stress run
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tsv_TabDelimited_AutoDetected_LinesParsed()
    {
        // Previously only ',' and ';' were sniffed, so a tab-delimited file collapsed to a
        // single unmapped column → "no line-table columns detected" (graceful but a gap).
        var header = CsvHeader.Replace(',', '\t');
        var row = string.Join("\t",
            "PO-TSV", "2026-06-08", "EUR", "Buyer", "1", "BUY-T", "Tab item", "7", "EA", "9.99");
        var content = header + NL + row + NL;

        var order = await new CsvOrderParser().ParseAsync(ToStream(content), CancellationToken.None);

        order.PoNumber.Should().Be("PO-TSV");
        order.Lines.Should().ContainSingle();
        order.Lines[0].BuyerItemCode.Should().Be("BUY-T");
        order.Lines[0].Quantity.Should().Be(7m);
        order.Lines[0].UnitPrice.Should().Be(9.99m);
    }
}
