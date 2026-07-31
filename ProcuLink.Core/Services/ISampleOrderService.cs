namespace ProcuLink.Core.Services;

/// <summary>
/// Outcome of creating an onboarding practice order.
/// </summary>
/// <param name="OrderId">The new sample order's id.</param>
/// <param name="DeliveryConfigured">
/// True when the sample supplier now has a delivery setup, so pressing "send" on the practice order
/// really reaches <c>delivered</c>. False when the caller supplied no address, supplied an invalid
/// one, or this deployment has no email provider configured — in which case the practice order
/// still parses and transforms but stops at "no delivery is set up", exactly as it did before
/// WP-27. Callers surface this so the UI never promises a delivery the deployment cannot make.
/// </param>
public readonly record struct SampleOrderResult(Guid OrderId, bool DeliveryConfigured);

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
    /// analytics event.
    /// </summary>
    /// <param name="deliverToEmail">
    /// Where the finished, supplier-ready file should be emailed when the user presses send —
    /// normally the signed-in user's own address. Supplying it seeds an <c>email</c> delivery setup
    /// on the sample supplier, which is what lets a brand-new account reach a DELIVERED file without
    /// any cooperation from a real supplier. Null/blank/invalid skips the seeding.
    /// </param>
    Task<SampleOrderResult> CreateAndEnqueueAsync(
        Guid organisationId,
        string? createdByUserId,
        string? deliverToEmail,
        CancellationToken ct);
}
