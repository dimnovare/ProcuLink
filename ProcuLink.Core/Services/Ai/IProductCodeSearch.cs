namespace ProcuLink.Core.Services.Ai;

/// <summary>
/// Optional, provider-neutral external web/product search that finds a real manufacturer
/// part number (MPN) / SKU for a described product. It runs ONLY as a last resort — when the
/// source document states no code AND the supplier has no authoritative product catalog to
/// match against — so a clearly-described real product ("Apple iPhone 15 silicone case") can
/// surface its actual part number instead of a fuzzy guess.
///
/// <para>This is strictly a SUGGESTION source: results are plausible, not verified, so every
/// web-grounded line stays <c>NeedsReview</c> and is never auto-applied. Implementations MUST
/// return <c>null</c> when unconfigured (no provider/key, or the feature flag is off) so the
/// default deploy is byte-identical and no PO data leaves the environment.</para>
/// </summary>
public interface IProductCodeSearch
{
    /// <summary>
    /// Searches for the manufacturer part number / SKU of the product described by
    /// <paramref name="description"/>. <paramref name="brandHint"/> is an optional
    /// manufacturer/brand to narrow the search (e.g. a cXML <c>ManufacturerName</c>).
    /// Returns <c>null</c> when unconfigured, when there is no usable description, or when
    /// no plausible part number is found — callers treat <c>null</c> as "no suggestion".
    /// </summary>
    Task<ProductCodeMatch?> FindPartNumberAsync(
        string description, string? brandHint, CancellationToken ct = default);
}

/// <summary>
/// A single web/product-search hit: the found part number plus light provenance.
/// <see cref="Confidence"/> is the searcher's self-reported plausibility in [0,1] — never a
/// verification guarantee. <see cref="SourceUrl"/> is the page the code was read from, used to
/// build honest provenance ("web product search (unverified): &lt;url&gt;").
/// </summary>
public sealed record ProductCodeMatch(
    string PartNumber,
    string? Title,
    string? SourceUrl,
    float Confidence);
