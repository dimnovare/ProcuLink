namespace ProcuLink.Core.Services;

/// <summary>
/// GDPR / right-to-erasure primitive: hard-deletes everything stored for a single
/// order — the R2 source file + outbound artifacts AND every DB row tied to the
/// order (lines, delivery attempts, artifacts, exceptions, validation results,
/// passport events, AI suggestion decisions, idempotency keys, the IMAP-ingest
/// ledger row, and the order's audit events) — strictly org-scoped.
/// Closes audit d-5 ("no delete-my-data path"). Idempotent: erasing an unknown or
/// already-erased order is a no-op (<see cref="OrderErasureResult.Found"/> = false).
/// </summary>
public interface IDataErasureService
{
    Task<OrderErasureResult> EraseOrderAsync(Guid organisationId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// Bulk variant of <see cref="EraseOrderAsync"/>: hard-erases every order in
    /// <paramref name="organisationId"/> that matches <paramref name="filter"/>, in a
    /// single server-side batch (no per-order HTTP round-trip — purpose-built for
    /// cleaning a large test org without tripping the per-request rate limiter).
    /// Strictly org-scoped: the org id is ANDed into every match, so a filter can
    /// never reach across tenants — even <see cref="BulkEraseFilter.Ids"/> belonging
    /// to another org are ignored. An empty filter (no active criterion) matches
    /// NOTHING and erases nothing — it can never mass-wipe the org. Idempotent:
    /// re-running over already-erased orders is a no-op.
    /// </summary>
    Task<BulkOrderErasureResult> BulkEraseOrdersAsync(
        Guid organisationId, BulkEraseFilter filter, CancellationToken ct);
}

/// <summary>
/// Match criteria for <see cref="IDataErasureService.BulkEraseOrdersAsync"/>. All
/// fields are optional and combine with AND — only orders matching every supplied
/// criterion are erased. A blank/whitespace <see cref="PoNumberPrefix"/> or
/// <see cref="Status"/>, an empty <see cref="Ids"/> list, and an absent
/// <see cref="OlderThan"/> are treated as "not supplied" (so a blank prefix can
/// never <c>StartsWith</c>-match the whole org). If NO criterion is active the
/// result is zero — never a mass wipe.
/// </summary>
public sealed record BulkEraseFilter(
    string? PoNumberPrefix = null,
    string? Status = null,
    IReadOnlyList<Guid>? Ids = null,
    DateTime? OlderThan = null);

/// <summary>
/// Outcome of a bulk erase: how many orders were erased plus the summed per-table
/// row/blob counts across them (for the audit trail / API response).
/// </summary>
public sealed record BulkOrderErasureResult(
    int OrdersErased,
    int R2ObjectsDeleted,
    int LinesDeleted,
    int ArtifactsDeleted,
    int DeliveryAttemptsDeleted,
    int ExceptionsDeleted,
    int ValidationResultsDeleted,
    int PassportEventsDeleted,
    int AuditEventsDeleted,
    int ConfirmationsDeleted,
    int ConfirmationLinesDeleted,
    int AiSuggestionDecisionsDeleted = 0,
    int IdempotencyKeysDeleted = 0,
    int EmailImportRecordsDeleted = 0,
    int SupplierSuggestionsDeleted = 0);

/// <summary>Per-table counts of what an erase removed (for the audit trail / API response).</summary>
public sealed record OrderErasureResult(
    bool Found,
    int R2ObjectsDeleted,
    int LinesDeleted,
    int ArtifactsDeleted,
    int DeliveryAttemptsDeleted,
    int ExceptionsDeleted,
    int ValidationResultsDeleted,
    int PassportEventsDeleted,
    int AuditEventsDeleted,
    int ConfirmationsDeleted,
    int ConfirmationLinesDeleted,
    int AiSuggestionDecisionsDeleted = 0,
    int IdempotencyKeysDeleted = 0,
    int EmailImportRecordsDeleted = 0,
    int SupplierSuggestionsDeleted = 0);
