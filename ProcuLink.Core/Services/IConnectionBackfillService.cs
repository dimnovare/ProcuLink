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
}
