namespace ProcuLink.Core.Entities;

/// <summary>
/// A durable, append-only record of what happened to ONE AI mapping suggestion on ONE
/// order line: was the suggested supplier item code accepted, rejected (a different code
/// was chosen), superseded, or resolved manually with no AI suggestion present.
///
/// <para>Why this exists:</para> the live resolve / bulk-accept paths CLEAR the transient
/// <c>AiSuggested*</c> fields on <see cref="PurchaseOrderLineEntity"/> once a line is
/// resolved, so the suggestion + the human's decision were previously unrecoverable (the
/// PO Passport explicitly noted the missing history). Persisting each decision lets us
/// later calibrate model confidence (how often a 0.92-confidence suggestion is actually
/// accepted) and audit AI provenance.
///
/// <para>Tenancy:</para> every row carries <see cref="OrgId"/> and every query is scoped
/// to it — never cross-tenant. The (OrgId, OrderId, LineNumber, SuggestedSupplierItemCode,
/// Decision) unique index makes the write idempotent across Hangfire retries / double-clicks.
/// </summary>
public class AiSuggestionDecision
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation (tenant scope key). Required.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The purchase order this decision belongs to. Required.</summary>
    public Guid OrderId { get; set; }

    /// <summary>The 1-based line number within the order the suggestion was for.</summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// The supplier item code the AI suggested. May be empty when the line was resolved
    /// manually with no AI suggestion present (<c>Decision == "manual"</c>).
    /// </summary>
    public string SuggestedSupplierItemCode { get; set; } = string.Empty;

    /// <summary>
    /// The supplier item code that was ACTUALLY chosen for the line (what the order now
    /// carries). Equal to <see cref="SuggestedSupplierItemCode"/> on accept; different on
    /// reject; null when nothing was chosen.
    /// </summary>
    public string? ChosenSupplierItemCode { get; set; }

    /// <summary>The full candidate set considered, captured as JSON for later analysis. Optional.</summary>
    public string? CandidateSetJson { get; set; }

    /// <summary>The AI's stated confidence for the suggestion (0..1), when one was present.</summary>
    public double? Confidence { get; set; }

    /// <summary>Model + prompt version identifier (e.g. "gpt-5-mini"), for calibration grouping. Optional.</summary>
    public string? ModelVersion { get; set; }

    /// <summary>
    /// Outcome: one of the <see cref="AiSuggestionDecisionKind"/> values —
    /// <c>accepted</c> | <c>rejected</c> | <c>superseded</c> | <c>manual</c>.
    /// </summary>
    public string Decision { get; set; } = string.Empty;

    /// <summary>Who decided — Clerk user id for a human, or "ai" for the bulk-accept path. Optional.</summary>
    public string? DecidedBy { get; set; }

    /// <summary>When the decision was recorded (UTC).</summary>
    public DateTime DecidedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}

/// <summary>Canonical decision outcome values for <see cref="AiSuggestionDecision.Decision"/>.</summary>
public static class AiSuggestionDecisionKind
{
    /// <summary>The user/system kept the AI's suggested code verbatim.</summary>
    public const string Accepted = "accepted";

    /// <summary>An AI suggestion existed but a DIFFERENT code was chosen.</summary>
    public const string Rejected = "rejected";

    /// <summary>The suggestion was replaced by a newer suggestion or re-resolution.</summary>
    public const string Superseded = "superseded";

    /// <summary>The line was resolved manually with no AI suggestion present.</summary>
    public const string Manual = "manual";
}
