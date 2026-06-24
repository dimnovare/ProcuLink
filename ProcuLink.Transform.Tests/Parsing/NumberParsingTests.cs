using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// Truth table for the single shared locale-aware decimal reader used by every order parser
/// (CSV, EDIFACT, X12, UBL, cXML, mapping-template). The "last separator is the decimal;
/// refuse-and-flag genuine ambiguity" contract is the load-bearing defence against silent
/// 10×/100×/1000× mis-pricing, so it is exercised directly here in addition to the per-parser
/// integration tests.
/// </summary>
public class NumberParsingTests
{
    // ── Invariant / US locale (european = false) ────────────────────────────────

    [Theory]
    [InlineData("73.22", 73.22)]        // plain decimal
    [InlineData("1234.56", 1234.56)]    // no grouping
    [InlineData("1,234.56", 1234.56)]   // US thousands group + decimal
    [InlineData("1,234,567.89", 1234567.89)]
    [InlineData("125.00", 125.00)]
    [InlineData("0.45", 0.45)]
    [InlineData("12", 12)]              // bare integer
    [InlineData("1,000", 1000)]        // single comma, 3 trailing → US thousands group
    public void Invariant_inputs_read_exactly(string raw, double expected)
    {
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal(raw, european: false);
        ambiguous.Should().BeFalse();
        value.Should().Be((decimal)expected);
    }

    // ── European locale (european = true) ───────────────────────────────────────

    [Theory]
    [InlineData("73,22", 73.22)]        // EU comma decimal — the 100×-overcharge trap
    [InlineData("1.234,56", 1234.56)]   // EU thousands group + comma decimal
    [InlineData("1.000", 1000)]        // EU thousands group, no decimal
    [InlineData("1.234.567,89", 1234567.89)]
    [InlineData("125,00", 125.00)]
    public void European_inputs_read_exactly(string raw, double expected)
    {
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal(raw, european: true);
        ambiguous.Should().BeFalse();
        value.Should().Be((decimal)expected);
    }

    // ── "Both separators present → last wins" works regardless of locale flag ────

    [Theory]
    [InlineData("1.234,56", 1234.56)]   // comma is last → decimal
    [InlineData("1,234.56", 1234.56)]   // dot is last → decimal
    public void Both_separators_present_last_one_is_the_decimal(string raw, double expected)
    {
        NumberParsing.TryParseFlexibleDecimal(raw, european: false).Value.Should().Be((decimal)expected);
        NumberParsing.TryParseFlexibleDecimal(raw, european: true).Value.Should().Be((decimal)expected);
    }

    // ── EU comma with a non-3-trailing-digit count is a decimal even when NOT EU ─

    [Theory]
    [InlineData("73,22")]   // 2 trailing → decimal, not a thousands group
    [InlineData("73,2")]    // 1 trailing → decimal
    public void Single_comma_with_non_three_trailing_digits_is_a_decimal_even_when_not_european(string raw)
    {
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal(raw, european: false);
        ambiguous.Should().BeFalse();
        // Must NOT be the integer interpretation (e.g. 7322 / 732).
        value.Should().BeLessThan(100m);
    }

    // ── Blank / empty → legitimately absent value, NOT ambiguous ────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_token_is_null_and_not_ambiguous(string? raw)
    {
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal(raw, european: false);
        value.Should().BeNull();
        ambiguous.Should().BeFalse();
    }

    // ── Garbage / genuinely ambiguous → flagged, never a silently-wrong value ────

    [Theory]
    [InlineData("1.5e2")]    // scientific notation — the canonical "1.52" silent-corruption trap
    [InlineData("12abc")]    // stray letters
    [InlineData("1-2-3")]    // numeric chars only but unparseable
    [InlineData("50%")]      // percent sign
    public void Garbage_token_is_flagged_ambiguous_with_no_value(string raw)
    {
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal(raw, european: false);
        ambiguous.Should().BeTrue();
        value.Should().BeNull();
    }

    [Fact]
    public void Currency_symbols_and_whitespace_are_tolerated_not_flagged()
    {
        // A currency symbol or NBSP thousands separator is numeric noise, not ambiguity.
        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal("€ 1.234,56", european: true);
        ambiguous.Should().BeFalse();
        value.Should().Be(1234.56m);
    }

    // ── BuildAmbiguityReason copy ───────────────────────────────────────────────

    [Fact]
    public void BuildAmbiguityReason_returns_null_when_nothing_flagged()
        => NumberParsing.BuildAmbiguityReason(false, false).Should().BeNull();

    [Theory]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    [InlineData(true,  true)]
    public void BuildAmbiguityReason_returns_a_message_when_flagged(bool qty, bool price)
        => NumberParsing.BuildAmbiguityReason(qty, price).Should().NotBeNullOrEmpty();
}
