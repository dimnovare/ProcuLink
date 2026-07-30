namespace ProcuLink.Core.Catalog;

/// <summary>
/// The ONE case rule for comparing ITEM CODES — the buyer item code on a learned
/// <c>ItemMapping</c> and the supplier <c>Code</c> on a catalog <c>SupplierProduct</c>.
///
/// <para><b>The bug this exists to prevent.</b> These two lookups were written years apart and
/// disagreed: the learned mapping was matched with an ordinal <c>==</c> (case-SENSITIVE, and on
/// Postgres's default collation genuinely case-sensitive at the database), while the catalog
/// dictionary was keyed with <see cref="StringComparer.OrdinalIgnoreCase"/>. The same code
/// therefore resolved through one path and not the other. A buyer whose ERP exported
/// <c>b-1</c> one week and <c>B-1</c> the next silently lost every mapping their operators had
/// taught the system — the lines dropped to review with no explanation, and nothing logged a
/// reason, because from each path's own point of view it behaved correctly.</para>
///
/// <para><b>The rule: trim, then fold case. Nothing else.</b> Separators are deliberately NOT
/// stripped. A supplier item code is a namespace the SUPPLIER controls, and collapsing
/// <c>AB-123</c> into <c>AB123</c> inside it risks matching a genuinely different SKU. That is the
/// opposite of <see cref="ProductKeyNormalizer"/>, which DOES strip separators — but only for
/// MANUFACTURER part numbers, where the punctuation is noise added by whoever re-typed the number
/// rather than meaningful structure. The two normalisers are different on purpose; do not merge
/// them.</para>
///
/// <para><b>Why <see cref="Key"/> uses <c>ToLower()</c> and not <c>ToLowerInvariant()</c>.</b> The
/// key is compared against <c>m.BuyerItemCode.ToLower()</c> inside an EF query, which Npgsql
/// translates to SQL <c>lower()</c>. Lower-casing the two sides by different rules would let an
/// exotic code fall out of the fetch — fail-safe, but silently. This mirrors
/// <c>CatalogRetrievalService</c>'s exact-match pass, which lower-cases both sides the same way.</para>
/// </summary>
public static class ItemCodeComparison
{
    /// <summary>
    /// The comparer for dictionaries and sets keyed by item code. Both the learned-mapping resolver
    /// and <c>OrderServiceShared.BuildCatalogLookupAsync</c> use THIS member rather than naming a
    /// comparer of their own, so the two cannot drift apart again by editing one file.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>The <see cref="StringComparison"/> twin of <see cref="Comparer"/>.</summary>
    public static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The SQL-comparable form of an item code: trimmed, then lower-cased with <c>ToLower()</c> so
    /// it matches what Npgsql's translated <c>lower()</c> produces for the stored column.
    /// Returns an empty string for null/blank input (never null — a null key would silently drop
    /// out of a <c>Contains</c> set instead of simply matching nothing).
    /// </summary>
    public static string Key(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToLower();

    /// <summary>True when two item codes name the same code under this rule.</summary>
    public static bool Matches(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), Comparison);
}
