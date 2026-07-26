namespace ProcuLink.Core.Entities;

/// <summary>
/// One ranked supplier candidate offered for ONE order that arrived without a supplier, plus
/// what the operator eventually did about it.
///
/// <para><b>Why its own table:</b> <see cref="AiSuggestionDecision"/> is line-scoped — it is keyed
/// by <c>LineNumber</c> and <c>SuggestedSupplierItemCode</c>. Bending it to also mean
/// order-scoped supplier routing would be the same overloading mistake as stuffing new concepts
/// into <c>canonical_json</c>. A new concept gets a first-class table.</para>
///
/// <para><b>These rows never route anything.</b> They are advisory evidence shown next to an
/// <c>unrouted</c> order; the supplier write lives exclusively in
/// <c>POST /orders/{id}/assign-supplier</c>. What they are FOR is the decision history: how often
/// a 0.7-scored suggestion is accepted is the only thing that could ever make an auto-assign
/// threshold defensible, and without these rows such a threshold would be a guess.</para>
///
/// <para><b>Tenancy:</b> every row carries <see cref="OrgId"/> and every query is scoped to it.</para>
/// </summary>
public class OrderSupplierSuggestion
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation (tenant scope key). Required.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The order this candidate was suggested for. Required.</summary>
    public Guid OrderId { get; set; }

    /// <summary>The candidate supplier. Required.</summary>
    public Guid SupplierId { get; set; }

    /// <summary>1-based position in the ranked set at the time it was recorded; 1 is the best candidate.</summary>
    public int Rank { get; set; }

    /// <summary>Combined score, 0..0.99. Never 1.0 — a heuristic does not claim certainty.</summary>
    public double Score { get; set; }

    /// <summary>
    /// The per-signal contributions behind <see cref="Score"/>, as JSON. This is the provenance
    /// the operator is shown ("why this supplier?") and the raw material for later calibration.
    /// </summary>
    public string? SignalsJson { get; set; }

    /// <summary>
    /// Scoring ruleset identity (<c>SupplierSuggestionScoring.ModelVersion</c>), so a calibration
    /// pass can group decisions by the rules that produced them and never compares across changes.
    /// </summary>
    public string? ModelVersion { get; set; }

    /// <summary>
    /// What happened to this suggestion: one of the <see cref="OrderSupplierSuggestionDecision"/>
    /// values, or <c>null</c> while it is still UNDECIDED.
    ///
    /// <para>Null-means-undecided is deliberate: the four decision words already in the codebase
    /// (<c>accepted</c> / <c>rejected</c> / <c>superseded</c> / <c>manual</c>) all describe
    /// something that HAPPENED, and inventing a fifth "pending" would make the absence of a
    /// decision look like one. It also gives the idempotency index a natural filter — at most one
    /// live suggestion per (org, order, supplier), with decided rows accumulating freely as history.</para>
    /// </summary>
    public string? Decision { get; set; }

    /// <summary>Who decided — Clerk user id for a human, or "system" when a re-scoring superseded it.</summary>
    public string? DecidedBy { get; set; }

    /// <summary>When the decision was recorded (UTC). Null while undecided.</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>When the suggestion was produced (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}

/// <summary>
/// Canonical outcome values for <see cref="OrderSupplierSuggestion.Decision"/>. Same vocabulary
/// as <see cref="AiSuggestionDecisionKind"/> — one product should describe a human's verdict on a
/// machine's guess with one set of words.
/// </summary>
public static class OrderSupplierSuggestionDecision
{
    /// <summary>The operator routed the order to this suggested supplier.</summary>
    public const string Accepted = "accepted";

    /// <summary>This supplier was shown as a candidate and a DIFFERENT one was chosen.</summary>
    public const string Rejected = "rejected";

    /// <summary>A later re-scoring of the same order replaced this row before anyone decided.</summary>
    public const string Superseded = "superseded";

    /// <summary>
    /// The operator routed the order to a supplier that was NOT suggested. Recorded against the
    /// supplier they actually picked — the single most valuable row in this table, because it is
    /// the scorer being measurably wrong.
    /// </summary>
    public const string Manual = "manual";
}
