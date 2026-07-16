using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Narrow order-stub creation seam used by the pull-ingress channels (SFTP / S3-R2 / IMAP email).
///
/// <para>Unlike <see cref="IOrderService.CreateStubAsync"/>, these overloads take the order id
/// <em>explicitly</em>. The pull-ingress channels pre-generate that id, store it on their
/// dedupe-ledger row at claim-insert time (so it survives a crash with no separate "backfill"
/// step), and then create the order under that exact primary key. On a retry after a transient
/// stub-creation failure they can therefore ask an unambiguous question — "does an order with
/// this id already exist?" — to decide RESUME (recreate the never-created order) vs SKIP (a real
/// duplicate). Passing the id in also makes creation idempotent on the order primary key: a
/// re-run for the same id is a find-or-create, never a second order.</para>
///
/// <para><see cref="OrderService"/> implements this alongside <see cref="IOrderService"/> and both
/// resolve to the same scoped instance, so the ingress sees orders committed through this seam on
/// its own shared <c>DbContext</c>.</para>
/// </summary>
public interface IStubOrderCreator
{
    /// <summary>
    /// Upload the raw file and create an order stub (status <c>parsing</c>) routed to
    /// <paramref name="supplierId"/>, using <paramref name="orderId"/> as the order's primary key.
    /// Idempotent on that key: if an order with <paramref name="orderId"/> already exists it is
    /// returned rather than duplicated. Does NOT parse inline — enqueue the parse job afterwards.
    /// </summary>
    Task<Result<PurchaseOrderEntity>> CreateStubAsync(
        Guid organisationId,
        Guid supplierId,
        Guid orderId,
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct);

    /// <summary>
    /// Like <see cref="CreateStubAsync"/> but with NO supplier yet (content-routed channels that
    /// have no default supplier). The order is parked <c>unrouted</c> by the parse job. Uses
    /// <paramref name="orderId"/> as the primary key with the same find-or-create idempotency.
    /// </summary>
    Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
        Guid organisationId,
        Guid orderId,
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct);
}
