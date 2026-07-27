using System.Text;

namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// The pure, side-effect-free half of supplier auto-detect: normalisation, per-signal weights,
/// score combination and ranking. Kept out of the DB-backed service so the load-bearing
/// judgement — which supplier wins, and by how much — is unit-testable without a database.
///
/// <para>The weights below are a starting prior, not a measurement. They are ordered by how
/// hard the signal is to forge accidentally: a registration identifier is a near-unique key, a
/// sender domain is owned infrastructure, a layout is a habit, a catalog overlap is
/// circumstantial, a company name is the weakest because two orgs' suppliers can share one.
/// Nothing auto-assigns at any score (D3), so a mis-weighting costs an operator one extra
/// glance — it cannot mis-route an order. The decision rows this feature records are what will
/// eventually replace these numbers with evidence.</para>
/// </summary>
public static class SupplierSuggestionScoring
{
    /// <summary>Ruleset identity, recorded per suggestion so a later calibration pass can group by it.</summary>
    public const string ModelVersion = "rules-v1";

    /// <summary>Most candidates ever returned. Three is what the operator banner shows.</summary>
    public const int MaxSuggestions = 3;

    /// <summary>
    /// Cap on how many distinct line codes the catalog-overlap probe sends to the database.
    /// A 500-line order must not turn one suggestion into a 500-term IN clause.
    /// </summary>
    public const int MaxProbedLineCodes = 50;

    public const double IdentityMatchWeight = 0.45;
    public const double SenderDomainWeight = 0.35;
    public const double LayoutWeight = 0.30;
    public const double CatalogOverlapWeight = 0.25;
    public const double NameMatchWeight = 0.20;
    public const double SenderDomainHistoryWeight = 0.20;

    /// <summary>
    /// Below this a candidate is not shown at all. A supplier that nothing really pointed at is
    /// noise in the operator's face, and noise next to a real hint devalues the real hint.
    /// </summary>
    public const double MinimumScore = 0.10;

    /// <summary>Shortest identifier we will compare. Below this, collisions stop being unlikely.</summary>
    private const int MinIdentifierLength = 4;

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
    /// Canonical comparison key for a VAT number / registration number / EDI-GLN code:
    /// upper-cased, every separator removed. Returns null when nothing usable remains.
    /// </summary>
    public static string? NormalizeIdentifier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToUpperInvariant(ch));

        return sb.Length == 0 ? null : sb.ToString();
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

    /// <summary>
    /// Whether two identifiers denote the same registration, after normalisation.
    ///
    /// <para>Exact equality, plus ONE tolerance: an ISO-style two-letter country prefix present on
    /// exactly one side ("EE101234567" vs "101234567"). That is the common shape of the same
    /// registration written as a VAT number on a document and as a bare registry code in a
    /// supplier profile. It is not fuzziness — every remaining character must still match
    /// exactly, and two different countries' prefixes never collapse together.</para>
    /// </summary>
    public static bool IdentifiersMatch(string? left, string? right)
    {
        var a = NormalizeIdentifier(left);
        var b = NormalizeIdentifier(right);

        if (a is null || b is null) return false;
        if (a.Length < MinIdentifierLength || b.Length < MinIdentifierLength) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        return DiffersOnlyByCountryPrefix(a, b) || DiffersOnlyByCountryPrefix(b, a);
    }

    private static bool DiffersOnlyByCountryPrefix(string prefixed, string bare) =>
        prefixed.Length == bare.Length + 2
        && char.IsLetter(prefixed[0]) && char.IsLetter(prefixed[1])
        && string.Equals(prefixed[2..], bare, StringComparison.Ordinal);

    // ── Combination + ranking ─────────────────────────────────────────────────

    /// <summary>
    /// Sums the signals into a 0..<see cref="FingerprintBoost.ConfidenceCeiling"/> score. The
    /// ceiling is the same one the fingerprint boost respects and for the same stated reason:
    /// a heuristic should never claim certainty.
    /// </summary>
    public static double Combine(IEnumerable<SupplierSignalContribution> signals)
    {
        var total = signals.Sum(s => s.Contribution);
        if (total <= 0) return 0;
        return Math.Min(FingerprintBoost.ConfidenceCeiling, total);
    }

    /// <summary>
    /// One plain-language sentence naming the supplier and the evidence, for the operator. The
    /// signal slugs are dev-rule strings and stay out of it — they travel in the provenance list.
    /// </summary>
    public static string BuildReason(string supplierName, IEnumerable<SupplierSignalContribution> signals)
    {
        var clauses = signals
            .Where(s => !string.IsNullOrWhiteSpace(s.Detail))
            .OrderByDescending(s => s.Contribution)
            .Select(s => s.Detail.Trim().TrimEnd('.'))
            .ToList();

        if (clauses.Count == 0)
            return $"{supplierName} — no matching details were found.";

        var evidence = clauses.Count == 1
            ? clauses[0]
            : $"{string.Join(", ", clauses.Take(clauses.Count - 1))}, and {clauses[^1]}";

        return $"{supplierName} — {evidence}.";
    }

    /// <summary>
    /// Turns scored candidates into the ranked list the operator sees: drops anything below
    /// <see cref="MinimumScore"/>, orders by score then name, keeps at most
    /// <see cref="MaxSuggestions"/>, and numbers them from 1.
    ///
    /// <para>The name tiebreak is what keeps a SHARED layout honest. When one layout is bound to
    /// several suppliers each of them receives the identical contribution, so they arrive here
    /// tied and LEAVE here tied — the ordering between them is alphabetical, never evidential.
    /// A layout collision must not silently pick one supplier, and this is where that holds.</para>
    /// </summary>
    public static IReadOnlyList<SupplierSuggestion> Rank(
        IEnumerable<(Guid SupplierId, string SupplierName, IReadOnlyList<SupplierSignalContribution> Signals)> scored) =>
        scored
            .Select(c => (c.SupplierId, c.SupplierName, c.Signals, Score: Combine(c.Signals)))
            .Where(c => c.Score >= MinimumScore)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SupplierName, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select((c, i) => new SupplierSuggestion(
                SupplierId:   c.SupplierId,
                SupplierName: c.SupplierName,
                Rank:         i + 1,
                Score:        c.Score,
                Reason:       BuildReason(c.SupplierName, c.Signals),
                Signals:      c.Signals))
            .ToList();
}
