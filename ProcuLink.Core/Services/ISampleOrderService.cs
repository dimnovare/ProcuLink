namespace ProcuLink.Core.Services;

/// <summary>
/// Creates a sample purchase order from the embedded onboarding fixture and enqueues parsing.
/// Idempotent on the hidden <c>__sample__</c> supplier. Sample orders are flagged <c>IsSample = true</c>
/// so the billing quota guard in <c>StripeBillingService.CountOrdersAsync</c> excludes them.
/// </summary>
public interface ISampleOrderService
{
    /// <summary>
    /// Creates a sample PurchaseOrder stub (uploading the embedded sample CSV to file storage),
    /// enqueues the existing ParseOrderJob to process it, and emits a <c>sample_order_started</c>
    /// analytics event. Returns the new order id.
    /// </summary>
    Task<Guid> CreateAndEnqueueAsync(Guid organisationId, string? createdByUserId, CancellationToken ct);
}
