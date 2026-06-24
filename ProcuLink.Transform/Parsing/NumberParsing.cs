using System.Globalization;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Locale-aware numeric reader shared by every order parser (CSV, EDIFACT, X12, UBL,
/// cXML, and the mapping-template CSV path). It is the single source of truth for the
/// "last separator is the decimal; refuse-and-flag genuine ambiguity" rule so the same
/// EU/US number is read identically no matter which format it arrived in.
///
/// <para>This used to be duplicated verbatim in <see cref="CsvOrderParser"/> and
/// <c>EdifactOrderParser</c>; extracting it here lets the other parse paths (mapping
/// template, X12, UBL, cXML) reuse the EXACT algorithm instead of their old naive
/// <c>","→"."</c> swap, which silently read EU "73,22" as 7322 (100× over) and
/// "1.234,56" as null/1.23456.</para>
/// </summary>
public static class NumberParsing
{
    /// <summary>
    /// Parse a decimal that may be US ("1,234.56", "73.22") or European
    /// ("1.234,56", "73,22", "1.000") notation. Rules:
    ///  • both separators present → the LAST one is the decimal separator;
    ///  • only ',' → decimal, UNLESS it's a single comma with exactly 3 trailing
    ///    digits and the input is NOT European (then it's a US thousands group);
    ///  • only '.' → decimal, UNLESS the input IS European AND it's a single dot
    ///    with exactly 3 trailing digits (then it's a European thousands group).
    /// <paramref name="european"/> is the locale signal (e.g. a ';' CSV delimiter, a
    /// ',' EDIFACT decimal mark). This prevents the silent 10×/100× corruption where
    /// "73,22" was read as 7322 under InvariantCulture.
    ///
    /// Returns <c>(value, ambiguous)</c>. <c>ambiguous</c> is true when the token
    /// could NOT be read unambiguously and the parser refuses to guess — the caller
    /// flags the line for review instead of emitting a silently-wrong number. A blank
    /// token is NOT ambiguous (it is a legitimately empty optional value → null).
    /// </summary>
    public static (decimal? Value, bool Ambiguous) TryParseFlexibleDecimal(string? raw, bool european)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, false);

        // Guard against the silent-wrong-value class: the digit/separator filter below
        // strips EVERY non-numeric character, so a stray letter would be deleted and the
        // remaining digits concatenated (e.g. "1.5e2" → "1.52" — a plausible, catastrophic
        // mis-price). Only genuine numeric noise may be silently stripped: whitespace
        // (incl. NBSP/thin-space thousands separators) and currency symbols. ANY other
        // character (letters such as an 'e' exponent, '%', etc.) means the token is
        // ambiguous → refuse it and let the line go to review.
        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '.' or ',' or '-' or '+') continue;
            if (char.IsWhiteSpace(c)) continue;
            if (char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol) continue;
            return (null, true);
        }

        var s = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (s.Length == 0 || s == "-") return (null, false);

        int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
        char? decimalSep;
        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalSep = lastComma > lastDot ? ',' : '.';                 // both → last wins
        }
        else if (lastComma >= 0)
        {
            bool single = s.IndexOf(',') == lastComma;
            int trailing = s.Length - lastComma - 1;
            decimalSep = (european || !(single && trailing == 3)) ? ',' : null;
        }
        else if (lastDot >= 0)
        {
            bool single = s.IndexOf('.') == lastDot;
            int trailing = s.Length - lastDot - 1;
            decimalSep = (european && single && trailing == 3) ? null : '.';
        }
        else
        {
            decimalSep = null;                                            // pure integer
        }

        string normalized = decimalSep is char ds
            ? s.Replace(ds == '.' ? "," : ".", "").Replace(ds, '.')      // strip groups, decimal → '.'
            : s.Replace(",", "").Replace(".", "");                       // integer / thousands-only

        // The token contained only numeric characters but still didn't parse (e.g. "1-2-3",
        // "--5", or a lone separator) — treat as ambiguous so it surfaces for review rather
        // than being silently dropped to null.
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (d, false) : (null, true);
    }

    /// <summary>
    /// Short "why was this flagged" string for the review UI. Null when nothing was
    /// ambiguous (the line is not parser-flagged). Shared so every parser produces the
    /// identical human-readable copy.
    /// </summary>
    public static string? BuildAmbiguityReason(bool qtyAmbiguous, bool priceAmbiguous) =>
        (qtyAmbiguous, priceAmbiguous) switch
        {
            (true,  true)  => "The quantity and unit price could not be read unambiguously from the source file.",
            (true,  false) => "The quantity could not be read unambiguously from the source file.",
            (false, true)  => "The unit price could not be read unambiguously from the source file.",
            _              => null,
        };
}
