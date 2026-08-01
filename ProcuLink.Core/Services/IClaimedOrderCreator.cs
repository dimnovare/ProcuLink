using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Core.Services;

/// <summary>
/// The PUSH channels' twin of <see cref="IStubOrderCreator"/>: order creation under a
/// <em>pre-generated</em> order id, for the inbound-email webhook and the REST ingress.
///
/// <para>Both seams exist for the same reason — claim-first dedupe. The ingress writes its
/// dedupe-ledger row (carrying this order id) and commits it BEFORE creating the order, so a crash
/// in between is unambiguously resumable: "does an order with this id exist?" separates a real
/// duplicate (SKIP) from an abandoned claim (RESUME). Because creation is a find-or-create on the
/// supplied primary key, a resume can never produce a second order, and the order primary key is the
/// final backstop if two resumers ever race the same id. See
/// <c>ProcuLink.Infrastructure.Services.Ingress.IngressDedupe</c> for the full contract.</para>
///
/// <para>This is a separate interface from <see cref="IStubOrderCreator"/> rather than four more
/// methods on it for two reasons. The push channels need the <c>inboundSenderDomain</c> the pull
/// channels have no notion of (founder ruling D2 — the counterparty domain is the routing evidence a
/// supplier-less order is missing), and they need the already-parsed shape
/// (<see cref="CreateClaimedFromParsedOrderAsync"/>) that the file-only pull seam has no use for.
/// Keeping them apart also leaves the nine pull-channel test doubles untouched.</para>
///
/// <para><c>supplierId</c> is nullable on both methods because a push channel resolves routing at
/// run time: an inbound email with no configured default supplier is imported UNROUTED and held for
/// assignment rather than guessed at. <c>OrderService</c> implements this alongside
/// <see cref="IOrderService"/> and both resolve to the same scoped instance.</para>
/// </summary>
public interface IClaimedOrderCreator
{
    /// <summary>
    /// Upload the raw file and create an order stub (status <c>parsing</c>) under
    /// <paramref name="orderId"/>. Idempotent on that key: an order already present under it is
    /// returned rather than duplicated. Does NOT parse inline — enqueue the parse job afterwards.
    /// A null <paramref name="supplierId"/> parks the order <c>unrouted</c>.
    /// </summary>
    Task<Result<PurchaseOrderEntity>> CreateClaimedStubAsync(
        Guid organisationId,
        Guid? supplierId,
        Guid orderId,
        Stream fileStream,
        string filename,
        string contentType,
        string? inboundSenderDomain,
        CancellationToken ct);

    /// <summary>
    /// Persist an already-parsed order (email-body NLP, REST push) under <paramref name="orderId"/>,
    /// with the same find-or-create idempotency. There is no source file and no parse job.
    /// A null <paramref name="supplierId"/> parks the order <c>unrouted</c>.
    /// </summary>
    Task<Result<PurchaseOrderEntity>> CreateClaimedFromParsedOrderAsync(
        Guid organisationId,
        Guid? supplierId,
        Guid orderId,
        ExtractedOrder order,
        string source,
        string? inboundSenderDomain,
        CancellationToken ct);
}
