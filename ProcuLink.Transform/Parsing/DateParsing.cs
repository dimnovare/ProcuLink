using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Locale-aware date reader — the date-side sibling of <see cref="NumberParsing"/>. It is the
/// single source of truth for the "day-first vs month-first" resolution so a numeric date such
/// as "12.06.2026" is interpreted identically no matter which parser (or the LLM extractor's
/// verbatim printed date) fed it in.
///
/// <para>The bug this closes (F-21): a European PO printed "12.06.2026" (12 June) was read as
/// 6 December because the interpretation defaulted to US month-first. The rules here are:</para>
/// <list type="bullet">
///   <item>ISO (<c>yyyy-MM-dd</c>, <c>yyyy/MM/dd</c>, ISO-8601 timestamps) is read verbatim and
///     is never ambiguous — the ordering is fixed by the standard.</item>
///   <item>A textual month ("12 Jun 2026", "Jun 12, 2026") is unambiguous.</item>
///   <item>Numeric <c>d[./-]d[./-]yyyy</c>: if exactly one of the first two components is &gt; 12
///     it can only be the day, so the ordering is <b>forced</b> and unambiguous.</item>
///   <item>When both leading components are ≤ 12 the date is genuinely ambiguous; the caller's
///     <paramref name="preferDayFirst"/> locale signal decides (EU-first ⇒ day-first is the
///     product default). This is a documented policy, NOT a silent guess — the ambiguity is
///     reported back so the caller can flag the value for human review if it wants to.</item>
/// </list>
/// Returns <c>(value, ambiguous)</c>. <c>value</c> is null when the input cannot be read as a
/// date at all (empty, junk, or a contradictory pair like "13/13/2026"). <c>ambiguous</c> is
/// true only when a numeric date's ordering was resolved by policy rather than being forced by
/// the data or fixed by ISO/textual form.
/// </summary>
public static class DateParsing
{
    // A numeric date: two 1-2 digit components then a 2- or 4-digit year, separated by
    // '.', '/' or '-'. Anchored so partial matches inside longer strings don't leak.
    private static readonly Regex NumericDate = new(
        @"^\s*(?<a>\d{1,2})[./-](?<b>\d{1,2})[./-](?<y>\d{2,4})\s*$",
        RegexOptions.Compiled);

    // ISO / ISO-like (year first). Parsed with a fixed component order so it is never reordered.
    private static readonly Regex IsoDate = new(
        @"^\s*(?<y>\d{4})[-/](?<m>\d{1,2})[-/](?<d>\d{1,2})(?:[T ].*)?$",
        RegexOptions.Compiled);

    // Textual-month formats we accept verbatim (invariant culture), both day-first and month-first.
    private static readonly string[] TextualFormats =
    {
        "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy",
        "MMM d, yyyy", "MMM dd, yyyy", "MMMM d, yyyy", "MMMM dd, yyyy",
        "MMM d yyyy", "MMMM d yyyy",
    };

    public static (DateOnly? Value, bool Ambiguous) TryParseFlexibleDate(string? raw, bool preferDayFirst)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, false);
        var t = raw.Trim();

        // ── ISO / year-first: fixed ordering, never ambiguous ──
        var iso = IsoDate.Match(t);
        if (iso.Success
            && int.TryParse(iso.Groups["y"].Value, out var iy)
            && int.TryParse(iso.Groups["m"].Value, out var im)
            && int.TryParse(iso.Groups["d"].Value, out var id)
            && TryMakeDate(iy, im, id, out var isoDate))
        {
            return (isoDate, false);
        }

        // ── Textual month (e.g. "12 Jun 2026") — unambiguous ──
        if (DateTime.TryParseExact(t, TextualFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var textual))
        {
            return (DateOnly.FromDateTime(textual), false);
        }

        // ── Numeric dd/mm vs mm/dd ──
        var num = NumericDate.Match(t);
        if (num.Success
            && int.TryParse(num.Groups["a"].Value, out var a)
            && int.TryParse(num.Groups["b"].Value, out var b))
        {
            var year = ExpandYear(num.Groups["y"].Value);
            if (year is null) return (null, false);

            var aCanBeMonth = a is >= 1 and <= 12;
            var bCanBeMonth = b is >= 1 and <= 12;

            // Forced orderings: exactly one component can be a month.
            if (bCanBeMonth && !aCanBeMonth)
            {
                // a > 12 → a is the day, b is the month (day-first). Forced.
                return TryMakeDate(year.Value, b, a, out var d1) ? (d1, false) : (null, false);
            }
            if (aCanBeMonth && !bCanBeMonth)
            {
                // b > 12 → b is the day, a is the month (month-first). Forced.
                return TryMakeDate(year.Value, a, b, out var d2) ? (d2, false) : (null, false);
            }
            if (!aCanBeMonth && !bCanBeMonth)
            {
                // Neither can be a month (e.g. "13/13/2026") — not a real date.
                return (null, false);
            }

            // Both ≤ 12 → genuinely ambiguous; resolve by the locale policy and report it.
            var (month, day) = preferDayFirst ? (b, a) : (a, b);
            return TryMakeDate(year.Value, month, day, out var d3) ? (d3, true) : (null, false);
        }

        return (null, false);
    }

    private static int? ExpandYear(string raw)
    {
        if (!int.TryParse(raw, out var y)) return null;
        if (raw.Length == 4) return y;
        // 2-digit year: pivot on the invariant calendar's 2-digit-year window (00-99 → 2000-2099
        // for our near-future PO dates; this mirrors .NET's default TwoDigitYearMax of 2029+ but
        // is stable and locale-independent).
        return 2000 + y;
    }

    private static bool TryMakeDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1) return false;
        if (day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateOnly(year, month, day);
        return true;
    }
}
