using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;

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
}
