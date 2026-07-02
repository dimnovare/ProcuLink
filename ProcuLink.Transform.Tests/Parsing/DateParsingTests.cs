using ProcuLink.Transform.Parsing;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// Truth table for the shared locale-aware date reader. The load-bearing rule is the
/// day-first vs month-first resolution for numeric dd/mm/yyyy vs mm/dd/yyyy — the exact
/// class of bug where a European PO printed "12.06.2026" (12 June) was read as 6 December
/// (F-21). ISO stays ISO; a component &gt; 12 forces the ordering unambiguously; a genuinely
/// ambiguous pair (both ≤ 12) follows the caller's <c>preferDayFirst</c> locale signal and is
/// reported as ambiguous so the caller may flag it — never silently swapped.
/// </summary>
public class DateParsingTests
{
    // ── ISO — never ambiguous, never reordered ──────────────────────────────────

    [Theory]
    [InlineData("2026-06-12", 2026, 6, 12)]
    [InlineData("2026-12-06", 2026, 12, 6)]
    [InlineData("2026/06/12", 2026, 6, 12)]
    [InlineData("2026-06-12T00:00:00", 2026, 6, 12)]
    public void Iso_is_read_verbatim_and_unambiguous(string raw, int y, int m, int d)
    {
        var (value, ambiguous) = DateParsing.TryParseFlexibleDate(raw, preferDayFirst: true);
        ambiguous.Should().BeFalse();
        value.Should().Be(new DateOnly(y, m, d));
    }

    // ── A component > 12 forces the ordering unambiguously ──────────────────────

    [Theory]
    [InlineData("26/06/2026", 2026, 6, 26)]   // 26 can only be the day
    [InlineData("13.06.2026", 2026, 6, 13)]   // Rheinbahn-style; 13 forces day-first
    [InlineData("06/26/2026", 2026, 6, 26)]   // 26 in slot 2 → US month-first, still forced
    public void A_component_over_twelve_forces_the_ordering(string raw, int y, int m, int d)
    {
        // preferDayFirst is irrelevant when one component exceeds 12 — the ordering is forced.
        var (value, ambiguous) = DateParsing.TryParseFlexibleDate(raw, preferDayFirst: false);
        value.Should().Be(new DateOnly(y, m, d));
        ambiguous.Should().BeFalse("a component over 12 fixes day vs month with no guessing");
    }

    // ── Genuinely ambiguous (both ≤ 12): follow the locale signal ───────────────

    [Theory]
    [InlineData("12.06.2026", true, 2026, 6, 12)]   // EU: 12 June  (the F-21 Rheinbahn/Siemens bug)
    [InlineData("12.06.2026", false, 2026, 12, 6)]  // US: 6 December
    [InlineData("12/06/2026", true, 2026, 6, 12)]   // EU: 12 June  (EXEMPLAR SEAFOOD header)
    [InlineData("12-06-2026", true, 2026, 6, 12)]   // EU: 12 June  (REDACTED-PARTY approval date)
    [InlineData("12-06-2026", false, 2026, 12, 6)]  // US: 6 December
    [InlineData("08/10/2026", true, 2026, 10, 8)]   // EU: 8 October
    [InlineData("10.07.2026", true, 2026, 7, 10)]   // EU: 10 July  (Siemens delivery date)
    [InlineData("10.07.2026", false, 2026, 10, 7)]  // US: 7 October
    public void Ambiguous_pair_follows_prefer_day_first_and_is_reported_ambiguous(
        string raw, bool preferDayFirst, int y, int m, int d)
    {
        var (value, ambiguous) = DateParsing.TryParseFlexibleDate(raw, preferDayFirst);
        value.Should().Be(new DateOnly(y, m, d));
        ambiguous.Should().BeTrue("both components are ≤ 12 so a locale policy was applied, not a certainty");
    }

    // ── Textual month — unambiguous ─────────────────────────────────────────────

    [Theory]
    [InlineData("12 Jun 2026", 2026, 6, 12)]     // Danfoss "12 Jun 2026"
    [InlineData("15 Jun 2026", 2026, 6, 15)]
    [InlineData("Jun 12, 2026", 2026, 6, 12)]
    public void Textual_month_is_unambiguous(string raw, int y, int m, int d)
    {
        var (value, ambiguous) = DateParsing.TryParseFlexibleDate(raw, preferDayFirst: true);
        value.Should().Be(new DateOnly(y, m, d));
        ambiguous.Should().BeFalse();
    }

    // ── Two-digit years ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12.06.26", true, 2026, 6, 12)]
    public void Two_digit_years_are_expanded(string raw, bool preferDayFirst, int y, int m, int d)
    {
        var (value, _) = DateParsing.TryParseFlexibleDate(raw, preferDayFirst);
        value.Should().Be(new DateOnly(y, m, d));
    }

    // ── Junk / empty → null, not a throw ────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("13/13/2026")]   // neither slot can be a month
    public void Unparseable_input_returns_null(string? raw)
    {
        var (value, _) = DateParsing.TryParseFlexibleDate(raw, preferDayFirst: true);
        value.Should().BeNull();
    }
}
