using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Input describing one AI-suggestion decision to persist. The suggested code + candidate
/// set + confidence + model version come from the transient line metadata (which is cleared
/// once the line is resolved); the chosen code + decision + decider are the outcome.
/// </summary>
public sealed record AiSuggestionDecisionRecord(
    int LineNumber,
    string SuggestedSupplierItemCode,
    string? ChosenSupplierItemCode,
    string? CandidateSetJson,
    double? Confidence,
    string? ModelVersion,
    string Decision,
    string? DecidedBy);

/// <summary>
/// Persists and reads the durable accept/reject history of AI mapping suggestions.
/// Every operation is org-scoped (never cross-tenant). Writes are idempotent across
/// Hangfire retries / double-submits via the (org, order, line, suggested code, decision)
/// unique index — a replay updates the existing row instead of duplicating it.
/// </summary>
public interface IAiSuggestionDecisionService
{
    /// <summary>
    /// Record one decision. Idempotent: a row matching (orgId, orderId, line, suggested code,
    /// decision) is refreshed in place; otherwise a new row is inserted. Best-effort — the
    /// caller is the resolve/accept path and must not fail the resolution if history write fails.
    /// </summary>
    Task RecordAsync(Guid orgId, Guid orderId, AiSuggestionDecisionRecord record, CancellationToken ct);

    /// <summary>
    /// Record many decisions for one order in a single save. Same idempotency semantics as
    /// <see cref="RecordAsync"/>; an empty input is a no-op.
    /// </summary>
    Task RecordManyAsync(Guid orgId, Guid orderId, IEnumerable<AiSuggestionDecisionRecord> records, CancellationToken ct);

    /// <summary>List the decision history for one order, newest first. Org-scoped.</summary>
    Task<IReadOnlyList<AiSuggestionDecision>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct);
}
