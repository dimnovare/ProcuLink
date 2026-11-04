namespace ProcuLink.Core.Services;

/// <summary>
/// Hard-erases a whole organisation: every row in every table carrying an <c>OrgId</c> /
/// <c>OrganisationId</c> column, the organisation row itself, and the blobs those rows reference.
/// Refusals are returned as an <see cref="OrgEraseOutcome"/>, never thrown.
/// </summary>
public interface IOrganisationEraser
{
    Task<OrgEraseReceipt> EraseAsync(Guid orgId, string actor, string reason, CancellationToken ct);
}

public enum OrgEraseOutcome
{
    Erased,
    NotFound,
    /// <summary>
    /// Money has moved, or could: a Stripe customer or subscription (ended or not — a missed webhook
    /// leaves the status stale) or any plan but Pilot (Enterprise is a manual contract).
    /// </summary>
    RefusedPaidSubscription,
    /// <summary>
    /// The org has <c>overage_billing_records</c>: money moved. Its FK is RESTRICT on purpose so an
    /// org delete cannot destroy the proof of what was billed; the eraser honours that decision.
    /// </summary>
    RefusedBillingEvidence,
}

/// <summary>
/// What an erase did. <see cref="RowsDeleted"/> is keyed by table name and counted before the
/// deletes run, so rows a cascade removed are still reported against their own table.
/// <see cref="FailedBlobKeys"/> names every stored file not confirmed deleted; the stored receipt
/// lists all of them until cleanup after the commit finishes.
/// </summary>
public sealed record OrgEraseReceipt(
    OrgEraseOutcome Outcome,
    Guid OrganisationId,
    string? Slug,
    string? ClerkOrgId,
    IReadOnlyDictionary<string, int> RowsDeleted,
    int BlobsDeleted,
    IReadOnlyList<string> FailedBlobKeys,
    string Actor,
    string Reason,
    DateTime At);
