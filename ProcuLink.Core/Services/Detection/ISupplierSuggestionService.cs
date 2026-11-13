namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// One signal's contribution to a supplier's score, kept so the operator can be told WHY a
/// supplier was suggested rather than just how confident we are. This is the provenance half
/// of the same contract AI mapping suggestions ship under.
/// </summary>
/// <param name="Signal">One of the <see cref="SupplierSignalKind"/> values.</param>
/// <param name="Contribution">How much this signal added to the score (never negative).</param>
/// <param name="Detail">One short human-readable clause, e.g. "8 of 10 item codes are in their catalog".</param>
public sealed record SupplierSignalContribution(string Signal, double Contribution, string Detail);

/// <summary>A ranked supplier candidate for an order that arrived without one.</summary>
/// <param name="Rank">1-based; 1 is the best candidate.</param>
/// <param name="Score">0..<see cref="FingerprintBoost.ConfidenceCeiling"/>. Never 1.0 — a heuristic does not get to claim certainty.</param>
/// <param name="Reason">One plain-language sentence an operator can act on.</param>
public sealed record SupplierSuggestion(
    Guid SupplierId,
    string SupplierName,
    int Rank,
    double Score,
    string Reason,
    IReadOnlyList<SupplierSignalContribution> Signals);

/// <summary>Canonical signal names recorded in <see cref="SupplierSignalContribution.Signal"/>.</summary>
public static class SupplierSignalKind
{
    /// <summary>A VAT number, registration number or EDI/GLN code on the document matched the supplier's.</summary>
    public const string Identity = "identity";

    /// <summary>The order arrived by email from the supplier's configured primary domain.</summary>
    public const string SenderDomain = "sender_domain";

    /// <summary>Earlier orders from the same sender domain were routed to this supplier.</summary>
    public const string SenderDomainHistory = "sender_domain_history";

    /// <summary>This org has parsed this exact column layout for this supplier before.</summary>
    public const string Layout = "layout_fingerprint";

    /// <summary>Some of the document's item codes are real codes in this supplier's catalog.</summary>
    public const string CatalogOverlap = "catalog_overlap";

    /// <summary>The supplier name printed on the document matched this supplier's name.</summary>
    public const string Name = "supplier_name";
}
