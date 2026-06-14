using System.Globalization;

namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Phase 2 connection-level price-variance guard. When enabled, a PO line whose unit price
/// drifts from the catalog price by more than <see cref="ThresholdPercent"/> is a breach: the
/// caller marks the line NeedsReview and HOLDs the order (pending_review). Catalog price is a
/// SUGGESTION — the guard NEVER mutates the PO price. Pure + deterministic so it is unit-tested
/// without a DB.
///
/// <para>EU comma-decimal safety: the catalog price may arrive as a raw locale string ("1.234,56");
/// we parse it with the same last-separator-is-decimal heuristic the CSV/XLSX parsers use
/// (<c>CsvOrderParser.ParseDecimalFlexible</c>), NOT <c>InvariantCulture</c> on the raw string
/// (which would misread "1.234,56" as 1.234 and silently corrupt the variance by 1000×). The
/// heuristic is duplicated here deliberately: Core cannot reference Transform.</para>
/// </summary>
public sealed record PriceVarianceGuard(bool Enabled, decimal ThresholdPercent)
{
    /// <summary>Outcome of a single line check: whether the threshold is breached, plus the SIGNED variance %.</summary>
    public readonly record struct Result(bool Breached, decimal VariancePercent);

    /// <summary>
    /// Compare a PO line unit price against a raw catalog price string. Returns a breach (and the
    /// signed variance percent) only when the guard is enabled, both prices are usable positive
    /// numbers, and |Δ|/catalog × 100 strictly exceeds <see cref="ThresholdPercent"/>. A missing,
    /// unparseable, zero, or negative catalog price NEVER breaches (catalog is advisory).
    /// </summary>
    public Result Breaches(decimal poUnitPrice, string? catalogPriceRaw)
    {
        if (!Enabled) return new Result(false, 0m);
        if (poUnitPrice <= 0m) return new Result(false, 0m);

        var catalog = ParseEuAware(catalogPriceRaw);
        if (catalog is null or <= 0m) return new Result(false, 0m);

        var absVariance    = Math.Abs(poUnitPrice - catalog.Value) / catalog.Value * 100m;
        var signedVariance = (poUnitPrice - catalog.Value) / catalog.Value * 100m;
        return new Result(absVariance > ThresholdPercent, signedVariance);
    }

    /// <summary>
    /// Parse a decimal that may be US ("1,234.56") or EU ("1.234,56" / "73,22"). Mirrors the
    /// <c>CsvOrderParser.ParseDecimalFlexible</c> heuristic (context-free variant — no file-level
    /// "european" hint): both separators present → the LAST one is the decimal; only ',' → decimal
    /// UNLESS it is a single comma with exactly 3 trailing digits (then a thousands group); only '.'
    /// → decimal. Never feeds the raw string to InvariantCulture blindly. A token containing any
    /// non-numeric character other than separators / whitespace / a currency symbol is refused
    /// (returns null) rather than silently corrupted (e.g. "1.5e2" must NOT become 1.52).
    /// </summary>
    internal static decimal? ParseEuAware(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Refuse genuinely ambiguous tokens: only digits, separators, sign, whitespace, and
        // currency symbols may be present. Any other character (a stray letter, 'e' exponent, '%')
        // means we must NOT guess — return null so the caller treats it as "no catalog price".
        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '.' or ',' or '-' or '+') continue;
            if (char.IsWhiteSpace(c)) continue;
            if (char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol) continue;
            return null;
        }

        var s = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (s.Length == 0 || s == "-") return null;

        var lastComma = s.LastIndexOf(',');
        var lastDot   = s.LastIndexOf('.');

        char? decimalSep;
        if (lastComma >= 0 && lastDot >= 0)
        {
            decimalSep = lastComma > lastDot ? ',' : '.';     // both → last wins
        }
        else if (lastComma >= 0)
        {
            var trailing = s.Length - lastComma - 1;
            var single   = s.IndexOf(',') == lastComma;
            decimalSep = (single && trailing == 3) ? null : ',';   // single comma + 3 digits → thousands group
        }
        else if (lastDot >= 0)
        {
            decimalSep = '.';
        }
        else
        {
            decimalSep = null;                                 // pure integer
        }

        var normalized = decimalSep is char ds
            ? s.Replace(ds == '.' ? "," : ".", "").Replace(ds, '.')   // strip groups, decimal → '.'
            : s.Replace(",", "").Replace(".", "");                    // integer / thousands-only

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : (decimal?)null;
    }
}
