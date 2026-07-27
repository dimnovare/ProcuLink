namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// A party as it appeared on the parsed document, reduced to the fields the supplier
/// scorer can actually match on. Deliberately a Core-local shape rather than
/// <c>ProcuLink.Transform.Parsing.ParsedParty</c>: <c>ProcuLink.Core</c> has no project
/// references at all, and the scoring contract must not be the thing that gives it one.
/// The parse hook maps <c>ParsedParty</c> onto this.
/// </summary>
/// <param name="Role">Lowercase role tag as the parsers emit it: "shipTo" | "billTo" | "remitTo" | "buyer" | "supplier".</param>
public sealed record SupplierSuggestionParty(
    string Role,
    string? Name = null,
    string? Vat = null,
    string? RegNr = null,
    string? EdiCode = null,
    string? Email = null);

/// <summary>
/// Everything the scorer is allowed to look at, gathered at the one point in the parse where
/// it is all in scope at once. Every field is optional — a real document rarely carries them
/// all, and a scorer that needs a particular field simply contributes nothing without it.
/// </summary>
/// <param name="OrderId">The order being scored. Excluded from its own sender-domain history.</param>
/// <param name="ColumnHeaders">Source column headers, for the layout-fingerprint lookup. Null for header-less formats (XML / EDIFACT / PDF).</param>
/// <param name="DocumentSupplierName">The supplier name AS PRINTED on the document (<c>ParsedOrder.SupplierName</c>).</param>
/// <param name="LineCodes">The item codes on the document's lines, for the catalog-overlap probe.</param>
/// <param name="InboundSenderDomain">Domain part of the inbound email sender, when the order arrived by email.</param>
public sealed record SupplierSuggestionInput(
    Guid OrganisationId,
    Guid OrderId,
    IReadOnlyList<string>? ColumnHeaders = null,
    string? DocumentSupplierName = null,
    IReadOnlyList<SupplierSuggestionParty>? Parties = null,
    IReadOnlyList<string>? LineCodes = null,
    string? InboundSenderDomain = null);

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

/// <summary>
/// Ranks an organisation's suppliers against a document that arrived with no supplier, so the
/// operator resolving the <c>unrouted</c> park gets candidates and a reason instead of a flat
/// list. Deterministic — no LLM in v1, therefore no <c>IAiUsageTracker</c> gate and no spend.
///
/// <para><b>Suggest, never assign.</b> Nothing here writes <c>PurchaseOrderEntity.SupplierId</c>.
/// The routing write stays exclusively in <c>POST /orders/{id}/assign-supplier</c>, at any score
/// (founder ruling D3, 2026-07-25). The rows this records are what would make a future
/// auto-assign threshold defensible; without them a threshold is a guess.</para>
///
/// <para>Every query is org-scoped, no exceptions.</para>
/// </summary>
public interface ISupplierSuggestionService
{
    /// <summary>
    /// Scores the org's active suppliers against <paramref name="input"/> and returns at most
    /// <see cref="SupplierSuggestionScoring.MaxSuggestions"/> candidates, best first. Read-only:
    /// writes nothing. Returns an empty list when the org has no suppliers or no signal fired.
    /// </summary>
    Task<IReadOnlyList<SupplierSuggestion>> SuggestAsync(
        SupplierSuggestionInput input, CancellationToken ct);

    /// <summary>
    /// Stages <paramref name="suggestions"/> for the order: supersedes any still-undecided rows
    /// from an earlier scoring of the same order, then adds the new set.
    ///
    /// <para><b>Does NOT call SaveChanges.</b> The caller owns the transaction — in the parse path
    /// these rows must commit with the order's status flip and lines or not at all, so this only
    /// mutates the change tracker and the caller's save decides their fate.</para>
    /// </summary>
    Task RecordAsync(
        Guid organisationId, Guid orderId, IReadOnlyList<SupplierSuggestion> suggestions, CancellationToken ct);
}
