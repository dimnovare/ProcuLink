namespace ProcuLink.Core.Services;

/// <summary>
/// Group V1 — turns each supplier's CURRENT scattered config (SupplierPoMapping +
/// SupplierDeliveryConfig.OutputFormat + ItemMapping rows + active SupplierAcceptanceProfile)
/// into a published "revision 1" under a <see cref="ProcuLink.Core.Entities.SupplierConnection"/>,
/// with the connection's active pointer set to it — with ZERO behaviour change.
///
/// <para>
/// Idempotent: a (org, supplier) that already has a connection is skipped, so the backfill can
/// run on every boot / be retried (Hangfire/restart culture) without creating duplicates.
/// </para>
/// </summary>
public interface IConnectionBackfillService
{
    /// <summary>
    /// Backfill every (org, supplier) that has any existing config but no connection yet.
    /// Returns the number of new connections (= new published rev-1s) created.
    /// </summary>
    Task<int> BackfillAllAsync(CancellationToken ct);

    /// <summary>
    /// Backfill a single supplier (idempotent). Returns the created/existing connection's
    /// active revision id, or null if the supplier has no config to snapshot.
    /// </summary>
    Task<Guid?> BackfillSupplierAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>
    /// Launch-batch-7 review fix: repair pass for backfilled revisions created BEFORE the
    /// backfill snapshotted the supplier-promoted Output section. Those rows carry a null
    /// <c>output_mapping_json</c> even when the supplier's live PoMappingConfig has a usable
    /// promoted Output — so flag-ON pinned orders would silently revert to the fixed transformer.
    /// Fills ONLY null snapshots on rows created by the backfill itself ("system:backfill");
    /// never overwrites a non-null snapshot; never touches user-authored revisions. Idempotent
    /// (safe on every boot); per-row failures are skipped + logged, which keeps the pass
    /// compatible with the published-row immutability DB trigger (migration
    /// AddReviewReasonAndPublishedRevisionImmutability). DEPLOY ORDER: this pass must run (one
    /// boot) against a database BEFORE that trigger migration is applied — or the trigger must
    /// exempt NULL→value fills of output_mapping_json — otherwise remaining null-output rows
    /// are skipped with a warning instead of repaired.
    /// Returns the number of revisions updated.
    /// </summary>
    Task<int> RebackfillPromotedOutputAsync(CancellationToken ct);
}
