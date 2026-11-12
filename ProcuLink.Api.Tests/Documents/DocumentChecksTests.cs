using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class DocumentChecksTests
{
    private static ReadField F(string key, string label, string? value, string type = FieldTypes.Text,
        double confidence = 0.95, bool notOnPage = false, string? meaning = null) =>
        new(key, label, value, type, meaning, confidence, null, notOnPage, false, false);

    private static FlowFieldList Required(params (string Key, string Label, string Type)[] f) =>
        new(f.Select(x => new FlowField(x.Key, x.Label, x.Type, null, true)).ToList());

    [Fact]
    public void Parser_accepts_locale_numbers_and_dates()
    {
        foreach (var (text, expected) in new[]
                 { ("1.234,56", 1234.56m), ("1,234.56", 1234.56m), ("2 380,00", 2380m), ("2 380,00", 2380m),
                   ("-12.5", -12.5m), ("EUR 10,50", 10.5m), ("€10.50", 10.5m) })
        {
            FieldValueParser.TryNumber(text, out var n).Should().BeTrue(text);
            n.Should().Be(expected, text);
        }
        FieldValueParser.TryNumber("12abc", out _).Should().BeFalse();
        FieldValueParser.NumberCandidates("1,500").Should().Equal(1.5m, 1500m);
        FieldValueParser.NumberCandidates("1.234,56").Should().Equal(1234.56m);

        foreach (var text in new[] { "03/04/2026", "03.04.2026", "2026-04-03", "3.4.26", "3/4/26", "3-4-2026", "3-4-26", "2026/4/3", "3. April 2026" })
        {
            FieldValueParser.TryDate(text, out var d).Should().BeTrue(text);
            d.Should().Be(new DateOnly(2026, 4, 3), text);
        }
        FieldValueParser.TryDate("03/04/2026", out var ambiguous);
        ambiguous.Should().Be(new DateOnly(2026, 4, 3)); // day first wins
        FieldValueParser.TryDate("3 Apr 2026", out _).Should().BeTrue();
        FieldValueParser.TryDate("31/31/2026", out _).Should().BeFalse();
    }

    [Fact]
    public void Run_fails_required_missing_low_confidence_and_not_on_page()
    {
        var fields = Required(("total", "Total", FieldTypes.Money), ("issue_date", "Invoice date", FieldTypes.Date),
            ("document_number", "Invoice number", FieldTypes.Text));
        var read = new ReadResult("Invoice", [
            F("total", "Total", null, FieldTypes.Money),
            F("issue_date", "Invoice date", "2026-04-03", FieldTypes.Date, confidence: 0.6),
            F("document_number", "Invoice number", null, notOnPage: true)]);

        var failed = DocumentChecks.Run(read, fields, fieldsConfirmed: true, duplicate: false);

        failed.Select(c => c.Code).Should().BeEquivalentTo(
            DocumentChecks.RequiredMissing, DocumentChecks.LowConfidence, DocumentChecks.NotOnPage);
        failed.Single(c => c.Code == DocumentChecks.RequiredMissing).Message.Should().Contain("Total");
        failed.Single(c => c.Code == DocumentChecks.LowConfidence).Message.Should().Contain("Invoice date");
        failed.Single(c => c.Code == DocumentChecks.NotOnPage).Message.Should().Contain("Invoice number");
        DocumentChecks.StatusFor(failed).Should().Be(DocumentStatus.NeedsReview);

        var badDate = new ReadResult("Invoice", [F("issue_date", "Invoice date", "soon", FieldTypes.Date)]);
        var listed = new FlowFieldList([new FlowField("issue_date", "Invoice date", FieldTypes.Date, null, false)]);
        DocumentChecks.Run(badDate, listed, true, false).Single()
            .Should().Match<DocumentCheck>(c => c.Code == DocumentChecks.TypeInvalid
                && c.Message == "Invoice date is not a date we can read.");
        // A field the flow does not list is never held up by the AI's guessed type.
        DocumentChecks.Run(badDate, FlowFieldList.Empty, true, false).Should().BeEmpty();
    }

    [Fact]
    public void Run_passes_clean_confirmed_document_and_flags_truncation_and_unconfirmed()
    {
        var fields = Required(("total", "Total", FieldTypes.Money));
        var read = new ReadResult("Invoice", [F("total", "Total", "1.234,56", FieldTypes.Money)]);

        var clean = DocumentChecks.Run(read, fields, fieldsConfirmed: true, duplicate: false);
        clean.Should().BeEmpty();
        DocumentChecks.StatusFor(clean).Should().Be(DocumentStatus.Ready);

        var cut = DocumentChecks.Run(read with { Truncated = true }, fields, fieldsConfirmed: false, duplicate: false);
        cut.Select(c => c.Code).Should().BeEquivalentTo(DocumentChecks.TextTruncated, DocumentChecks.FieldsUnconfirmed);

        var numbered = new ReadResult("Invoice", [F("n", "Number", " inv 1 ", meaning: FieldMeanings.DocumentNumber)]);
        DocumentChecks.DedupKeyOf(numbered).Should().Be("INV1");
        DocumentChecks.DedupKeyOf(read).Should().BeNull();
    }

    private static ReadField M(string key, string value, string meaning) => F(key, key, value, FieldTypes.Money, meaning: meaning);

    private static ReadTable Lines(params (string Qty, string Price, string Amount)[] rows) => new("lines", "Lines",
        [new ReadColumn("q", "Qty", FieldTypes.Number, FieldMeanings.LineQuantity),
         new ReadColumn("p", "Price", FieldTypes.Money, FieldMeanings.LineUnitPrice),
         new ReadColumn("a", "Amount", FieldTypes.Money, FieldMeanings.LineAmount)],
        rows.Select(r => new ReadRow(new Dictionary<string, ReadCell>
        {
            ["q"] = new(r.Qty, 0.9, null, false, false), ["p"] = new(r.Price, 0.9, null, false, false), ["a"] = new(r.Amount, 0.9, null, false, false),
        })).ToList(), true);

    private static IReadOnlyList<DocumentCheck> Arith(ReadResult read) =>
        DocumentChecks.Run(read, FlowFieldList.Empty, fieldsConfirmed: true, duplicate: false)
            .Where(c => c.Code == DocumentChecks.ArithmeticMismatch).ToList();

    private static IReadOnlyList<DocumentCheck> Money(string net, string tax, string gross, string? currency = null)
    {
        var fields = new List<ReadField>
        {
            M("net", net, FieldMeanings.TotalNet), M("tax", tax, FieldMeanings.TotalTax), M("gross", gross, FieldMeanings.TotalGross),
        };
        if (currency is not null) fields.Add(F("cur", "Currency", currency, meaning: FieldMeanings.Currency));
        return Arith(new ReadResult("I", fields));
    }

    [Fact]
    public void Arithmetic_overflow_goes_to_review_instead_of_throwing()
    {
        var big = "79000000000000000000000000000";
        var read = new ReadResult("I", [M("net", big, FieldMeanings.TotalNet), M("tax", big, FieldMeanings.TotalTax), M("g", "1", FieldMeanings.TotalGross)],
            [Lines(("100000000000000000000", "100000000000000000000", "5"), (big, big, big))]);
        IReadOnlyList<DocumentCheck>? result = null;
        var run = () => result = Arith(read);
        run.Should().NotThrow();
        result.Should().ContainSingle().Which.Should().Be(
            new DocumentCheck(DocumentChecks.ArithmeticMismatch, false, "g", "An amount is too large to check."));
    }

    [Fact]
    public void Arithmetic_checks_totals_lines_and_minor_units()
    {
        Money("100", "20", "121").Should().ContainSingle().Which.Message.Should().Be("Net plus tax doesn't equal the total.");
        Money("100", "20", "120").Should().BeEmpty();
        Money("100", "10", "110", "JPY").Should().BeEmpty();
        Money("1.234,50", "100", "1.334,50").Should().BeEmpty();

        var net = M("net", "100", FieldMeanings.TotalNet);
        Arith(new ReadResult("I", [net], [Lines(("1", "50", "50"), ("1", "50.01", "50.01"))])).Should().BeEmpty();
        Arith(new ReadResult("I", [net], [Lines(("1", "50", "50"), ("1", "49", "49"))])).Should().ContainSingle()
            .Which.Message.Should().Be("The lines don't add up to the net total.");

        Arith(new ReadResult("I", [], [Lines(("1", "5", "5"), ("3", "9.99", "29.97"))])).Should().BeEmpty();
        Arith(new ReadResult("I", [], [Lines(("3", "9.99", "30.00"))])).Should().ContainSingle()
            .Which.Message.Should().Be("Line 1: quantity × price doesn't equal the amount.");

        Arith(new ReadResult("I", [F("x", "X", "5"), M("net", "abc", FieldMeanings.TotalNet), M("tax", "1", FieldMeanings.TotalTax), M("g", "9", FieldMeanings.TotalGross)]))
            .Should().BeEmpty();
    }

    [Fact]
    public void Flow_meaning_drives_arithmetic_and_ambiguous_numbers_skip()
    {
        var read = new ReadResult("I", [M("net", "100", FieldMeanings.TotalNet), M("tax", "20", FieldMeanings.TotalTax), M("g", "999", FieldMeanings.TotalGross)]);
        var cleared = new FlowFieldList([new FlowField("net", "Net", FieldTypes.Money, FieldMeanings.TotalNet, false),
            new FlowField("tax", "Tax", FieldTypes.Money, FieldMeanings.TotalTax, false), new FlowField("g", "Gross", FieldTypes.Money, null, false)]);
        DocumentChecks.Run(read, cleared, true, false).Should().NotContain(c => c.Code == DocumentChecks.ArithmeticMismatch);
        Arith(read).Should().ContainSingle();

        Arith(new ReadResult("I", [F("cur", "Currency", "EUR", meaning: FieldMeanings.Currency)], [Lines(("1.000", "0,50", "500,00"))])).Should().BeEmpty();
    }
}
