using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>Locale-tolerant value parsing for read fields. Pure; shared by the checks and the value locator.</summary>
public static class FieldValueParser
{
    // Optional currency symbol or upper-case ISO code on either side of the digits ("12abc" is not one).
    private static readonly Regex NumberShape =
        new(@"^(?:[A-Z]{3}|[€$£])?\s*(-?[\d.,]+)\s*(?:[A-Z]{3}|[€$£])?$", RegexOptions.Compiled);
    private static readonly Regex VatShape = new("^[A-Z]{2}[A-Z0-9]{2,13}$", RegexOptions.Compiled);
    private static readonly Regex EmailShape = new(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$", RegexOptions.Compiled);

    // Order matters: an ambiguous 03/04/2026 matches d/M/yyyy first, so it reads day first.
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "d.M.yyyy", "d/M/yyyy", "M/d/yyyy", "d MMM yyyy", "d MMMM yyyy", "MMM d, yyyy", "MMMM d, yyyy",
    ];

    public static bool TryNumber(string? s, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = NumberShape.Match(s.Trim().Replace(' ', ' ').Replace(" ", ""));
        if (!m.Success) return false;

        var body = m.Groups[1].Value;
        // The right-most separator is the decimal one; a separator that repeats can only be a thousands mark.
        var decimalAt = Math.Max(body.LastIndexOf(','), body.LastIndexOf('.'));
        if (decimalAt >= 0 && body.Count(c => c == body[decimalAt]) > 1) decimalAt = -1;

        var digits = decimalAt < 0
            ? body.Replace(",", "").Replace(".", "")
            : body[..decimalAt].Replace(",", "").Replace(".", "") + "." + body[(decimalAt + 1)..];
        return decimal.TryParse(digits, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }

    public static bool TryDate(string? s, out DateOnly value) =>
        DateOnly.TryParseExact(s?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    /// <summary>ISO 13616: country letters, check digits, then mod-97 of the rearranged digits is 1.</summary>
    public static bool IsIban(string? iban)
    {
        if (iban is null || iban.Length is < 15 or > 34 || !char.IsAsciiLetter(iban[0]) || !char.IsAsciiLetter(iban[1])
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]) || !iban.All(char.IsAsciiLetterOrDigit))
            return false;

        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;
            remainder = (remainder * (value > 9 ? 100 : 10) + value) % 97;
        }
        return remainder == 1;
    }

    public static bool IsVatId(string? s) =>
        s is not null && VatShape.IsMatch(s.Replace(" ", "").ToUpperInvariant());

    public static bool IsCurrency(string? s) =>
        s is { Length: 3 } && s.All(char.IsAsciiLetter);

    public static bool IsEmail(string? s) => s is not null && EmailShape.IsMatch(s.Trim());
}
