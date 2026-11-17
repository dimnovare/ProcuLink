namespace ProcuLink.Core.Services;

/// <summary>
/// Hard-erases a whole organisation: every row in every table carrying an <c>OrgId</c> /
/// <c>OrganisationId</c> column, the organisation row itself, and the blobs those rows reference.
/// Refusals are returned as an <see cref="OrgEraseOutcome"/>, never thrown.
/// </summary>
public interface IOrganisationEraser
{
    /// <summary>The daily sweep's call: refuses every workspace that has ever paid, and leaves sign-in accounts alone.</summary>
    Task<OrgEraseReceipt> EraseAsync(Guid orgId, string actor, string reason, CancellationToken ct);

    /// <summary>
    /// Staff erase on the customer's request. A paid workspace goes only once its agreement has ended:
    /// the operator says so (<paramref name="agreementEnded"/>), Stripe confirms live that nothing runs
    /// (when the workspace has Stripe ids), and no overage is left unbilled. Then its sign-in accounts go too.
    /// </summary>
    Task<OrgEraseReceipt> EraseOnRequestAsync(Guid orgId, string actor, string reason, bool agreementEnded, CancellationToken ct);
}

public enum OrgEraseOutcome
{
    Erased,
    NotFound,
    /// <summary>
    /// Money has moved, or could: a Stripe customer or subscription (ended or not — a missed webhook
    /// leaves the status stale) or any plan but Pilot (Enterprise is a manual contract). On request,
    /// it means the operator did not confirm the agreement has ended.
    /// </summary>
    RefusedPaidSubscription,
    /// <summary>
    /// The org has <c>overage_billing_records</c>: money moved. Its FK is RESTRICT on purpose so an
    /// org delete cannot destroy the proof of what was billed; the eraser honours that decision.
    /// </summary>
    RefusedBillingEvidence,
    /// <summary>On request: Stripe reports a subscription still running.</summary>
    RefusedRunningSubscription,
    /// <summary>On request: Stripe could not be asked, or could not rule a running subscription out.</summary>
    RefusedSubscriptionUnknown,
    /// <summary>On request: an overage row was never put on a Stripe invoice, so money is still owed.</summary>
    RefusedUnbilledOverage,
}

/// <summary>
/// What an erase did. <see cref="RowsDeleted"/> is keyed by table name and counted before the
/// deletes run, so rows a cascade removed are still reported against their own table.
/// <see cref="FailedBlobKeys"/> names every stored file not confirmed deleted; the stored receipt
/// lists all of them until cleanup after the commit finishes. <see cref="SignIn"/> is one of
/// <see cref="SignInSteps"/>; <see cref="FailedSignInAccounts"/> names what the sign-in step could not remove.
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
    DateTime At,
    string SignIn = SignInSteps.NotRequested,
    int SignInAccountsRemoved = 0,
    IReadOnlyList<string>? FailedSignInAccounts = null);
