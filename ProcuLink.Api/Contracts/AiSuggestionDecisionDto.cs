namespace ProcuLink.Api.Contracts;

/// <summary>
/// One durable AI-suggestion decision for an order line, returned by
/// <c>GET /api/orders/{id}/ai-decisions</c>. This is the persisted accept/reject history
/// the live resolve flow would otherwise discard (the line's Ai* fields are cleared on
/// resolution) — it lets confidence be calibrated and AI provenance be audited later.
/// </summary>
public record AiSuggestionDecisionDto(
    Guid Id,
    int LineNumber,
    string SuggestedSupplierItemCode,
    string? ChosenSupplierItemCode,
    string? CandidateSetJson,
    double? Confidence,
    string? ModelVersion,
    /// <summary>One of: accepted | rejected | superseded | manual.</summary>
    string Decision,
    /// <summary>Clerk user id for a human decision, or "ai" for the bulk-accept path.</summary>
    string? DecidedBy,
    DateTime DecidedAt
);
