using System.Globalization;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Which character a document uses as its decimal separator. <see cref="Unknown"/> is a
/// first-class answer, not a failure: "1.000" is one thousand in Germany and one in the
/// UK, and where nothing in the document settles that, the honest output is a flagged
/// line a human confirms — never a guess.
/// </summary>
public enum DecimalConvention
{
    /// <summary>Undetermined. Ambiguous tokens must be refused and flagged for review.</summary>
    Unknown = 0,
    /// <summary>'.' is the decimal separator, ',' groups thousands (UK/US: "1,234.56").</summary>
    Point,
    /// <summary>',' is the decimal separator, '.' groups thousands (EU: "1.234,56").</summary>
    Comma,
}

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
        => TryParseFlexibleDecimal(raw, european ? DecimalConvention.Comma : DecimalConvention.Point);

    /// <summary>
    /// Parse a decimal against a <paramref name="convention"/> established for the whole
    /// document (see <see cref="InferDecimalConvention"/>).
    ///
    /// <para>A token that is decisive on its own — "1.234,56", "73,22", "12.50" — is read
    /// from its own evidence and does not consult the convention at all. The convention is
    /// consulted only for the ONE genuinely undecidable shape: a single separator with
    /// exactly three trailing digits ("1.000" / "1,000"), which is a thousands group in one
    /// locale and a decimal in the other.</para>
    ///
    /// <para>When that shape appears and the convention is <see cref="DecimalConvention.Unknown"/>,
    /// the parser REFUSES rather than guesses: it returns <c>(null, ambiguous: true)</c> and the
    /// caller flags the line for human review. This is the whole point — a flagged line a person
    /// confirms is a good outcome; a silent hundredfold error delivered to a supplier is the
    /// worst one.</para>
    ///
    /// Returns <c>(value, ambiguous)</c>. A blank token is NOT ambiguous (it is a legitimately
    /// empty optional value → null).
    /// </summary>
    public static (decimal? Value, bool Ambiguous) TryParseFlexibleDecimal(string? raw, DecimalConvention convention)
    {
        if (!TryScrubToken(raw, out var s)) return (null, true);
        if (s is null) return (null, false);                             // legitimately blank

        // A token that proves its own convention outranks the document-level one: the digits
        // are the data, the document's declaration is only how its writer usually formats.
        var effective = FirstKnown(EvidenceIn(s), convention);

        if (effective == DecimalConvention.Unknown)
        {
            // Nothing decided the separator. A token with no separator at all is still a
            // perfectly good integer; anything else is the undecidable shape → refuse it.
            if (s.Contains('.') || s.Contains(','))
                return (null, true);
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var whole)
                ? (whole, false) : (null, true);
        }

        var (dec, grp) = effective == DecimalConvention.Comma ? (',', '.') : ('.', ',');
        var normalized = s.Replace(grp.ToString(), string.Empty).Replace(dec, '.');

        // The token contained only numeric characters but still didn't parse (e.g. "1-2-3",
        // "--5", or a lone separator) — treat as ambiguous so it surfaces for review rather
        // than being silently dropped to null.
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (d, false) : (null, true);
    }

    /// <summary>
    /// Infer the decimal convention from a CORPUS of raw tokens — a whole column, or a whole
    /// document — rather than from one cell, because one cell frequently cannot say.
    ///
    /// <para>Evidence per token: both separators present → the last one is the decimal; a
    /// separator that REPEATS can only be grouping thousands, so the other one is the decimal;
    /// a lone separator whose trailing-digit count is not exactly three is a decimal. A lone
    /// separator with exactly three trailing digits carries no evidence — unless the integer
    /// part rules a thousands group out ("0,500" and "1234.567" cannot be grouped).</para>
    ///
    /// <para>A corpus that contradicts itself returns <see cref="DecimalConvention.Unknown"/>:
    /// a document is not entitled to an answer it does not support.</para>
    /// </summary>
    public static DecimalConvention InferDecimalConvention(IEnumerable<string?> tokens)
    {
        bool point = false, comma = false;

        foreach (var token in tokens)
        {
            if (!TryScrubToken(token, out var s) || s is null) continue;

            switch (EvidenceIn(s))
            {
                case DecimalConvention.Point: point = true; break;
                case DecimalConvention.Comma: comma = true; break;
            }

            if (point && comma) return DecimalConvention.Unknown;        // self-contradictory
        }

        return point ? DecimalConvention.Point
             : comma ? DecimalConvention.Comma
             : DecimalConvention.Unknown;
    }

    /// <summary>
    /// The first candidate that actually decided something. Call sites read as a preference
    /// order, e.g. this column's evidence, then the whole document's, then whatever the file
    /// format itself declares.
    /// </summary>
    public static DecimalConvention FirstKnown(params DecimalConvention[] candidates)
    {
        foreach (var c in candidates)
            if (c != DecimalConvention.Unknown) return c;
        return DecimalConvention.Unknown;
    }

    /// <summary>What a single scrubbed token proves about the decimal separator, if anything.</summary>
    private static DecimalConvention EvidenceIn(string s)
    {
        int dots = s.Count(c => c == '.'), commas = s.Count(c => c == ',');

        if (dots > 0 && commas > 0)                                      // both → last one wins
            return s.LastIndexOf(',') > s.LastIndexOf('.')
                ? DecimalConvention.Comma : DecimalConvention.Point;

        if (dots == 0 && commas == 0) return DecimalConvention.Unknown;  // plain integer

        var sep = dots > 0 ? '.' : ',';
        var decidesIfDecimal = sep == '.' ? DecimalConvention.Point : DecimalConvention.Comma;
        var decidesIfGroup   = sep == '.' ? DecimalConvention.Comma : DecimalConvention.Point;

        // A separator appearing more than once can only be a thousands group ("1.234.567").
        if ((dots > 0 ? dots : commas) > 1) return decidesIfGroup;

        var last = s.LastIndexOf(sep);
        if (s.Length - last - 1 != 3) return decidesIfDecimal;           // "73,22", "12.5", "1.2345"

        // Exactly three trailing digits is the one shape a thousands group can take — so it
        // is the only shape that says nothing, unless the integer part cannot be a group.
        var head = s[..last].TrimStart('-', '+');
        return head.Length is 0 or > 3 || head[0] == '0'
            ? decidesIfDecimal                                           // "0,500", "1234.567"
            : DecimalConvention.Unknown;                                 // "1.000" — undecidable
    }

    /// <summary>
    /// Strips the noise a number is legitimately allowed to carry and rejects everything else.
    ///
    /// <para>Guards the silent-wrong-value class: a naive digit/separator filter deletes a
    /// stray letter and concatenates what is left (e.g. "1.5e2" → "1.52" — a plausible,
    /// catastrophic mis-price). Only whitespace (incl. NBSP/thin-space thousands separators)
    /// and currency symbols may be dropped silently. ANY other character means the token is
    /// ambiguous.</para>
    ///
    /// Returns false when the token must be refused; <paramref name="scrubbed"/> is null when
    /// the token is legitimately blank.
    /// </summary>
    private static bool TryScrubToken(string? raw, out string? scrubbed)
    {
        scrubbed = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '.' or ',' or '-' or '+') continue;
            if (char.IsWhiteSpace(c)) continue;
            if (char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol) continue;
            return false;
        }

        var s = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (s.Length == 0 || s == "-") return true;                      // e.g. a lone "€"

        scrubbed = s;
        return true;
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
