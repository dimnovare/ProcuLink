namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Detects orders stranded in the transient <c>delivering</c> status past a timeout and
/// guarantees a delivering→terminal transition: it re-drives them through the delivery
/// retry queue up to a bounded requeue cap, then dead-letters them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>DeliveryService.DispatchArtifactAsync</c> commits
/// <c>status = delivering</c> to the database BEFORE the artifact download + dispatcher
/// call. If the process dies, the artifact download throws, or a dispatcher hangs past
/// Hangfire's invisibility timeout between that commit and the terminal
/// <c>PersistAttemptAsync</c>, the order is left in <c>delivering</c> forever:
/// <c>StuckOrderDetectionService</c> only sweeps <c>parsing</c>/<c>transforming</c>, and
/// <c>DeliverySlaService</c> only sets the <c>SlaBreached</c> flag — neither transitions the
/// order out of <c>delivering</c>. This sweep closes that gap.
/// </para>
/// <para>
/// Runs as a recurring cross-tenant Hangfire sweep; each requeue / dead-letter and its audit
/// event carry that order's own OrgId. Idempotent — a requeue bumps <c>UpdatedAt</c> so the
/// order leaves the stuck window until it stalls again, and a dead-lettered order no longer
/// matches.
/// </para>
/// <para>
/// <b>The re-drive does not deliver on its own, and this sweep cannot recover an order alone.</b>
/// The same <c>UpdatedAt</c> bump that gives idempotency also makes the row un-claimable for the
/// reclaim window, so the retry it enqueues loses the atomic claim and returns
/// <c>DeliveryOutcome.ClaimLost</c>. Delivery happens one SCHEDULED backoff step later (~30 min),
/// once the row has aged past that window — which is why <c>ClaimLost</c> must keep rescheduling.
/// Pinned end-to-end by <c>CrashedHolderRecoveryCompositionPostgresTests</c>.
/// </para>
/// </remarks>
public interface IStuckDeliveryDetectionService
{
    /// <summary>
    /// Re-drives every order older than <paramref name="stuckThreshold"/> still in the
    /// <c>delivering</c> status: under the requeue cap it enqueues a fresh delivery-retry
    /// job and writes a <c>StuckDeliveryRequeued</c> audit event; at/over the cap it moves the
    /// order to <c>delivery_dead_letter</c> with a <c>StuckDeliveryDeadLettered</c> audit event.
    /// Idempotent. Returns the number of orders acted on.
    /// </summary>
    Task<int> RunAsync(TimeSpan stuckThreshold, CancellationToken ct);
}
