namespace ProcuLink.Core.Catalog;

/// <summary>
/// Normalisation for product identifiers that are compared ACROSS parties — today the
/// manufacturer part number (MPN).
///
/// The problem it solves: the same manufacturer part is written differently by every system
/// that touches it. A punchout cXML says <c>REDACTED-ORDER-DATA</c>, a distributor's price feed
/// says <c>QBT2500BKBTK1</c>, an ERP export says <c>qbt2500 bk btk1</c>. Compared literally
/// these are three different strings and the line stays unmatched.
///
/// The rule (deliberately small, and the ONLY rule — nothing here is fuzzy):
///   1. null / whitespace-only → null (never an empty-string key, which would match every
///      product that also has no MPN);
///   2. every character that is not an ASCII letter or an ASCII digit is REMOVED — this covers
///      spaces, hyphens, dots, slashes, underscores and the non-breaking spaces that leak out
///      of spreadsheets;
///   3. the remainder is upper-cased with the invariant culture (never <c>ToUpper()</c>, whose
///      Turkish-locale <c>i</c> → <c>İ</c> would produce a key that does not round-trip);
///   4. if nothing survives (the input was all punctuation) → null.
///
/// Worked examples:
/// <code>
///   "REDACTED-ORDER-DATA"  → "QBT2500BKBTK1"
///   "qbt2500 bk btk1"  → "QBT2500BKBTK1"
///   "REDACTED-ORDER-DATA"     → "P1058930010"
///   "REDACTED-ORDER-DATA"         → "MWR23SA"
///   "  "               → null
///   "---"              → null
/// </code>
///
/// KNOWN, ACCEPTED collision: stripping separators makes <c>AB-123</c> and <c>AB123</c> the
/// same key. Two genuinely different manufacturer parts that differ ONLY by punctuation would
/// therefore collide. This is accepted because (a) it is vanishingly rare inside one
/// manufacturer's numbering scheme, and (b) an MPN match is only ever a SUGGESTION an operator
/// accepts — it never silently rewrites a line. Collisions are surfaced, not resolved: a lookup
/// that hits more than one catalog product returns no suggestion at all (see
/// <c>OrderIngestionService</c>), so an ambiguous match can never be presented as a confident one.
/// </summary>
public static class ProductKeyNormalizer
{
    /// <summary>
    /// Returns the comparison key for <paramref name="raw"/>, or <c>null</c> when the input
    /// carries no comparable characters. See the type remarks for the exact rule.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Upper bound on the result; most identifiers are far shorter, and stackalloc-free
        // simplicity matters more here than the last allocation (this runs per catalog row).
        Span<char> buffer = raw.Length <= 256 ? stackalloc char[raw.Length] : new char[raw.Length];
        var written = 0;

        foreach (var ch in raw)
        {
            if (ch is >= '0' and <= '9')
            {
                buffer[written++] = ch;
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                buffer[written++] = ch;
            }
            else if (ch is >= 'a' and <= 'z')
            {
                // Invariant ASCII upper-casing, done inline so no culture can influence it.
                buffer[written++] = (char)(ch - 32);
            }
            // Everything else (separators, punctuation, whitespace, non-ASCII) is dropped.
        }

        return written == 0 ? null : new string(buffer[..written]);
    }
}
