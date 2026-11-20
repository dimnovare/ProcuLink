using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Parsing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ProcuLink.Transform.Tests.Documents;

public class ValueLocatorTests
{
    [Fact]
    public void Ocr_line_splits_into_word_boxes_by_character_offset()
    {
        var words = OcrWords.FromLine("PO 12345", 100, 50, 900, 70, 1000, 200, 1);
        words.Should().HaveCount(2);
        words[0].Text.Should().Be("PO");
        words[0].Box.X.Should().BeApproximately(0.1, 1e-3);
        words[0].Box.Width.Should().BeApproximately(0.8 * 2 / 8, 1e-3);
        words[1].Text.Should().Be("12345");
        words[1].Box.X.Should().BeApproximately(0.1 + 0.8 * 3 / 8, 1e-3);
        words.Should().OnlyContain(w => Math.Abs(w.Box.Y - 0.25) < 1e-3 && Math.Abs(w.Box.Height - 0.1) < 1e-3);
    }

    [Fact]
    public void Locator_matches_across_diacritics()
    {
        var words = new List<PageWord>
        {
            new("Tonis", 1, new Box(0.1, 0.1, 0.1, 0.02)),
            new("Aar", 1, new Box(0.22, 0.1, 0.1, 0.02)),
        };
        ValueLocator.Locate("Tõnis Äär", words)!.Page.Should().Be(1);
    }

    [Fact]
    public void Locate_spans_multiword_value_and_matches_number_formats()
    {
        var words = new List<PageWord>
        {
            new("Acme", 1, new Box(0.10, 0.10, 0.10, 0.02)),
            new("Components", 1, new Box(0.22, 0.10, 0.20, 0.02)),
            new("Ltd", 1, new Box(0.44, 0.10, 0.05, 0.02)),
            new("2,380.00", 1, new Box(0.60, 0.50, 0.12, 0.02)),
        };

        var name = ValueLocator.Locate("Acme Components Ltd", words)!;
        name.Page.Should().Be(1);
        name.Box!.X.Should().BeApproximately(0.10, 1e-9);
        name.Box.Width.Should().BeApproximately(0.39, 1e-9);

        ValueLocator.Locate("2380.00", words)!.Box!.Y.Should().BeApproximately(0.50, 1e-9);
        ValueLocator.Locate("Nowhere Inc", words).Should().BeNull();
    }

    [Fact]
    public void LocateInSpans_prefers_a_whole_span_then_containment()
    {
        var spans = new List<TextSpan> { new("a", "PO-9 total"), new("b", "po-9") };
        ValueLocator.LocateInSpans("PO-9", spans)!.Address.Should().Be("b");
        ValueLocator.LocateInSpans("total", spans)!.Address.Should().Be("a");
        ValueLocator.LocateInSpans("zzz", spans).Should().BeNull();
    }

    [Fact]
    public void Locates_thousands_and_trailing_punctuation()
    {
        var words = new List<PageWord>
        {
            new("1,500", 1, new Box(0.1, 0.1, 0.1, 0.02)),
            new("4471,", 1, new Box(0.1, 0.3, 0.1, 0.02)),
        };

        ValueLocator.Locate("1500", words)!.Box!.Y.Should().BeApproximately(0.1, 1e-9);
        ValueLocator.Locate("4471", words)!.Box!.Y.Should().BeApproximately(0.3, 1e-9);
    }

    private static List<PageWord> WrappedCell(double mustX, double mustY = 0.43) =>
    [
        new("Kontori", 1, new Box(0.10, 0.40, 0.08, 0.02)),
        new("tool", 1, new Box(0.19, 0.40, 0.05, 0.02)),
        new("2", 1, new Box(0.60, 0.40, 0.02, 0.02)),
        new("49,90", 1, new Box(0.80, 0.40, 0.06, 0.02)),
        new("must", 1, new Box(mustX, mustY, 0.05, 0.02)),
    ];

    [Fact]
    public void A_value_wrapped_onto_the_next_line_of_its_cell_is_found()
    {
        var hit = ValueLocator.Locate("Kontori tool must", WrappedCell(0.10))!;
        hit.Page.Should().Be(1);
        hit.Box!.X.Should().BeApproximately(0.10, 1e-9);
        hit.Box.Y.Should().BeApproximately(0.40, 1e-9);
        hit.Box.Width.Should().BeApproximately(0.14, 1e-9);
        hit.Box.Height.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public void A_word_under_another_column_does_not_continue_a_value()
    {
        ValueLocator.Locate("Kontori tool must", WrappedCell(0.60)).Should().BeNull();
    }

    [Fact]
    public void A_word_in_the_same_column_rows_further_down_does_not_continue_a_value()
    {
        ValueLocator.Locate("Kontori tool must", WrappedCell(0.10, 0.49)).Should().BeNull();
    }

    [Fact]
    public void A_wrapped_cell_in_a_real_pdf_is_found_even_when_its_words_are_short()
    {
        // Helvetica 10pt on 12pt lines: "case" and "mm" have no capitals or ascenders, so their boxes are low.
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        foreach (var (text, x, y) in new[] { ("Paper", 60, 700), ("case", 92, 700), ("2", 300, 700), ("49,90", 400, 700), ("mm", 60, 688), ("Kontori", 60, 676) })
            page.AddText(text, 10, new PdfPoint(x, y), font);
        var words = PdfWordExtractor.Extract(builder.Build()).Words;

        var hit = ValueLocator.Locate("Paper case mm", words)!;

        hit.Box!.Y.Should().BeLessThan(words.Single(w => w.Text == "case").Box.Y + 1e-9);
        (hit.Box.Y + hit.Box.Height).Should().BeApproximately(words.Single(w => w.Text == "mm").Box.Y + words.Single(w => w.Text == "mm").Box.Height, 1e-9);
        ValueLocator.Locate("Paper case Kontori", words).Should().BeNull(); // two lines down is the next row
    }
}
