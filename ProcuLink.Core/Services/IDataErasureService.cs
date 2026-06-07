namespace ProcuLink.Core.Services;

/// <summary>
/// GDPR / right-to-erasure primitive: hard-deletes everything stored for a single
/// order — the R2 source file + outbound artifacts AND every DB row tied to the
/// order (lines, delivery attempts, artifacts, exceptions, validation results,
/// passport events, and the order's audit events) — strictly org-scoped.
/// Closes audit d-5 ("no delete-my-data path"). Idempotent: erasing an unknown or
/// already-erased order is a no-op (<see cref="OrderErasureResult.Found"/> = false).
/// </summary>
public interface IDataErasureService
{
    Task<OrderErasureResult> EraseOrderAsync(Guid organisationId, Guid orderId, CancellationToken ct);
}

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
    int ConfirmationLinesDeleted);
