using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class TeachSpotsTests
{
    private static PageWord W(string t, double x, double y, double w = 0.08) => new(t, 1, new Box(x, y, w, 0.02));

    [Fact]
    public void Words_in_the_box_are_the_value_and_the_label_left_of_it_is_near()
    {
        var words = new[] { W("Total", 0.1, 0.5), W("due:", 0.19, 0.5), W("1", 0.3, 0.5, 0.02), W("234,50", 0.33, 0.5), W("EUR", 0.45, 0.5) };
        var spot = TeachSpots.FromWords(words, 1, new Box(0.29, 0.49, 0.15, 0.04));
        spot.Value.Should().Be("1 234,50");
        spot.Near.Should().Be("Total due:");
        spot.Location.Page.Should().Be(1);
    }

    [Fact]
    public void Label_above_when_nothing_is_left_of_it()
    {
        var words = new[] { W("Invoice", 0.1, 0.40), W("number", 0.19, 0.40), W("INV-77", 0.1, 0.43, 0.16) };
        var spot = TeachSpots.FromWords(words, 1, new Box(0.09, 0.425, 0.2, 0.03));
        spot.Value.Should().Be("INV-77");
        spot.Near.Should().Be("Invoice number");
    }

    [Fact]
    public void Cell_and_line_spots()
    {
        var a = TeachSpots.FromCells([new(2, 1, "Total"), new(2, 2, "99.50")], 2, 2);
        (a.Value, a.Near, a.Location.Address).Should().Be(("99.50", "Total", "cell:r2c2"));
        TeachSpots.FromCells([new(1, 2, "Amount"), new(3, 2, "5")], 3, 2).Near.Should().Be("Amount");

        var l = TeachSpots.FromLine("a\r\nTotal: 5 EUR\rz", 2, "5 EUR");
        l.Near.Should().Be("Total:");
        l.Location.Address.Should().Be("line:2");
        TeachSpots.FromLine("a\r\nTotal: 5 EUR\rz", 3, "z").Location.Address.Should().Be("line:3");
        TeachSpots.Read("a.docx", "Total: 5 EUR"u8.ToArray(), null, null, "line:1", "5 EUR").Should().BeNull();
    }
}
