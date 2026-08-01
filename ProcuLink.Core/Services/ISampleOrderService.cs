using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Services;

/// <summary>
/// Outcome of creating an onboarding practice order.
/// </summary>
/// <param name="OrderId">The new sample order's id.</param>
/// <param name="PracticeDelivery">
/// What pressing "send" on this practice order will actually do — one of
/// <see cref="PracticeDeliveryState"/>. Callers surface it so the review screen never promises a
/// delivery the deployment cannot make, and never promises a STOP that will not happen (WP-39 §4.5).
/// </param>
public readonly record struct SampleOrderResult(Guid OrderId, string PracticeDelivery)
{
    /// <summary>
    /// True only when the practice mailbox was set up, so the finished file is emailed to the
    /// operator and nothing reaches a real supplier.
    ///
    /// <para>Kept because it is wire contract — the API still returns <c>deliveryConfigured</c> and
    /// pre-WP-39 clients still read it — but DERIVED, so it cannot drift from the state again. It
    /// used to be the whole answer, and it silently meant "the seeder wrote a config just now"
    /// rather than "this supplier has a delivery target", which is the gap §4.5 fell through.</para>
    /// </summary>
    public bool DeliveryConfigured => PracticeDelivery == PracticeDeliveryState.EmailedToYou;
}

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
