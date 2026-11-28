using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class ReadMergeTests
{
    private static ReadField F(string key, string? value, double confidence = 0.9, bool located = false) =>
        new(key, key, value, FieldTypes.Text, null, confidence, located ? new SourceLocation(1, null, null) : null, false, false, false);
    private static ReadRow Row(string sku) =>
        new(new Dictionary<string, ReadCell> { ["sku"] = new ReadCell(sku, 0.9, null, false, false) });
    private static ReadTable Table(string key, string[] columns, params string[] skus) =>
        new(key, key, columns.Select(c => new ReadColumn(c, c, FieldTypes.Text, null)).ToList(), skus.Select(Row).ToList(), false);

    [Fact]
    public void A_listed_key_reads_under_the_flows_name()
    {
        var read = new ReadResult("Invoice", [F("sposob_platnosci", "Przelew"), F("other", "x")], [Table("lines", ["sku"], "A")]);
        var expected = new FlowFieldList([new FlowField("sposob_platnosci", "Sposób płatności", FieldTypes.Text, null, false)],
            [new FlowTable("lines", "Pozycje", [new FlowField("sku", "Kod towaru", FieldTypes.Text, null, false)])]);

        var named = ReadMerge.Named(read, expected);

        named.Fields.Select(f => f.Label).Should().Equal("Sposób płatności", "other");
        (named.Tables[0].Label, named.Tables[0].Columns[0].Label).Should().Be(("Pozycje", "Kod towaru"));
        ReadMerge.Named(read, null).Should().BeSameAs(read);
    }

    [Fact]
    public void One_part_comes_back_as_it_is()
    {
        var only = new ReadResult("Invoice", [F("a", "1")]);
        ReadMerge.Merge([only], null).Should().BeSameAs(only);
    }

    [Fact]
    public void Values_are_kept_once_rows_follow_page_order_and_a_disagreement_is_marked_for_a_look()
    {
        var expected = new FlowFieldList(
            [new FlowField("document_number", "Number", FieldTypes.Text, null, true),
             new FlowField("total_gross", "Total", FieldTypes.Money, null, true),
             new FlowField("iban", "IBAN", FieldTypes.Text, null, false)],
            [new FlowTable("lines", "Lines", [])]);
        var parts = new[]
        {
            new ReadResult("Invoice", [F("document_number", "INV-1", 0.95), F("total_gross", "900.00", 0.9), F("iban", null)],
                [Table("lines", ["sku", "qty"], "A", "B")]),
            new ReadResult(null, [F("document_number", "INV-1", 0.95, located: true), F("total_gross", null)],
                [Table("lines", ["sku", "price"], "C")], Truncated: true),
            new ReadResult("Statement", [F("total_gross", "1,250.00", 0.97), F("note", "Thanks", 0.8)],
                [Table("lines", ["sku"], "D"), Table("payments", ["sku"], "P")]),
        };

        var merged = ReadMerge.Merge(parts, expected);

        merged.KindLabel.Should().Be("Invoice");
        merged.Truncated.Should().BeTrue();
        merged.Fields.Select(f => f.Key).Should().Equal("document_number", "total_gross", "iban", "note");
        merged.Fields[0].Location.Should().NotBeNull("of two equal readings the one found on the page is kept");
        merged.Fields[0].Confidence.Should().Be(0.95, "the same value twice is not a disagreement");
        merged.Fields[1].Value.Should().Be("1,250.00", "the most confident reading wins");
        merged.Fields[1].Confidence.Should().Be(ReadMerge.ConflictConfidence).And.BeLessThan(DocumentChecks.ReviewConfidence);
        merged.Fields[2].Value.Should().BeNull("a listed field nobody found stays");
        merged.Fields.Select(f => f.Listed).Should().Equal(true, true, true, false);
        merged.Tables[0].Columns.Select(c => c.Key).Should().Equal("sku", "qty", "price");
        merged.Tables[0].Rows.Select(r => r.Cells["sku"].Value).Should().Equal("A", "B", "C", "D");
        merged.Tables.Select(t => (t.Key, t.Listed)).Should().Equal(("lines", true), ("payments", false));
    }

    [Fact]
    public void With_no_expected_list_everything_is_listed_blank_fields_go_and_the_keys_can_be_handed_on()
    {
        var parts = new[]
        {
            new ReadResult("Invoice", [F("invoice_no", "7"), F("ghost", null)], [Table("lines", ["sku"], "A")]),
            new ReadResult(null, [F("ghost", "  ")], []),
        };

        var merged = ReadMerge.Merge(parts, null);

        merged.Fields.Select(f => f.Key).Should().Equal("invoice_no");
        merged.Fields.Should().OnlyContain(f => f.Listed);
        merged.Tables.Should().OnlyContain(t => t.Listed);
        var keys = ReadMerge.KeysOf(merged);
        keys.Fields.Should().BeEquivalentTo(new[] { new FlowField("invoice_no", "invoice_no", FieldTypes.Text, null, false) });
        keys.Tables.Single().Columns.Select(c => c.Key).Should().Equal("sku");
    }
}
