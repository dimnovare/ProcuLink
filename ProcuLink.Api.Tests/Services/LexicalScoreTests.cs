using FluentAssertions;
using ProcuLink.Core.Catalog;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class LexicalScoreTests
{
    [Theory]
    [InlineData("SUP-1", "x", "SUP-1", null, null, 130)]           // exact code 100 + containment 25 + shared token "sup" 5
    [InlineData("4740001", "x", "SUP-9", null, "4740001", 100)]    // exact barcode
    [InlineData("ABC1", "x", "XABC12", null, null, 25)]            // containment
    [InlineData("Q", "pilsner bottle", "SUP-3", "Pilsner bottle 0.5L", null, 10)] // two shared tokens
    public void Score_keeps_the_order_paths_weights(string code, string text, string cand, string? candText, string? barcode, int expected)
        => LexicalScore.Score(code, LexicalScore.Tokenize($"{code} {text}"), cand, candText, barcode).Should().Be(expected);
}
