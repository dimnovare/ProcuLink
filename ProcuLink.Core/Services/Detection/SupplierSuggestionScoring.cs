using System.Text;

namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// Company-name and email-domain normalisation, shared by flow sender matching
/// (<c>SenderIdentity</c>) and inbound email routing (<c>InboundEmailRouter</c>). The supplier
/// scoring and ranking that once lived here went with the order pipeline (plan 13b, 2026-11-13).
/// </summary>
public static class SupplierSuggestionScoring
{
    /// <summary>
    /// Legal-form tokens stripped from the END of a company name before comparison, repeatedly,
    /// so "Acme GmbH & Co KG" and "Acme" compare equal. Deliberately conservative: this only ever
    /// removes a trailing token, never anything from the middle of a name.
    /// </summary>
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.Ordinal)
    {
        "ou", "oü", "as", "ab", "oy", "oyj", "aps", "ans",
        "ltd", "limited", "inc", "incorporated", "llc", "llp", "plc",
        "gmbh", "mbh", "ag", "ug", "kg", "se",
        "bv", "nv", "cv",
        "sa", "sas", "sarl", "srl", "spa", "sl", "slu",
        "kft", "zrt", "doo", "dd", "sro",
        "sia", "uab",
        "co", "company", "corp", "corporation", "holding", "holdings",
        "pte", "pty", "kk", "gk", "ky",
    };

    /// <summary>Multi-token legal forms, matched as a trailing phrase before single tokens.</summary>
    private static readonly string[] LegalSuffixPhrases =
    {
        "sp z oo", "s r o", "z o o", "d o o", "gmbh co kg",
    };

    /// <summary>Punctuation deleted outright because it joins parts of ONE token ("B.V." → "bv", "A/S" → "as").</summary>
    private static readonly char[] JoiningPunctuation = { '.', '/', '\'', '’', '`' };

    // ── Normalisation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Canonical comparison key for a company name: case-folded, punctuation-normalised,
    /// whitespace-collapsed, legal form stripped. Returns null when nothing usable remains —
    /// notably for a bare legal form like "GmbH", which must never become a match key.
    /// </summary>
    public static string? NormalizeCompanyName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var lowered = raw.ToLowerInvariant();

        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (Array.IndexOf(JoiningPunctuation, ch) >= 0) continue;         // deleted: joins one token
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');                   // everything else separates
        }

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // Strip trailing legal forms repeatedly: "acme co ltd" → "acme co" → "acme".
        bool stripped;
        do
        {
            stripped = false;

            foreach (var phrase in LegalSuffixPhrases)
            {
                var parts = phrase.Split(' ');
                if (tokens.Count > parts.Length && tokens.TakeLast(parts.Length).SequenceEqual(parts))
                {
                    tokens.RemoveRange(tokens.Count - parts.Length, parts.Length);
                    stripped = true;
                    break;
                }
            }
            if (stripped) continue;

            // Never strip the LAST remaining token — "GmbH" alone must normalise to nothing
            // rather than to itself, and a company genuinely called "Holding" keeps its name.
            if (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1]))
            {
                tokens.RemoveAt(tokens.Count - 1);
                stripped = true;
            }
        } while (stripped);

        if (tokens.Count == 1 && LegalSuffixes.Contains(tokens[0])) return null;

        var normalized = string.Join(' ', tokens);
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// Canonical comparison key for an email domain: the part after any "@", lower-cased, with a
    /// leading "www." and a trailing dot removed. Returns null when nothing usable remains.
    /// </summary>
    public static string? NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim().ToLowerInvariant();

        var at = value.LastIndexOf('@');
        if (at >= 0) value = value[(at + 1)..];

        if (value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];

        value = value.TrimEnd('.').Trim();

        return value.Length == 0 ? null : value;
    }
}
