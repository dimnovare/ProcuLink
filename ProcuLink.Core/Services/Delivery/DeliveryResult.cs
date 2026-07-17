namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// What a delivery call actually DID — the distinction the automatic retry queue needs, and which
/// <see cref="DeliveryResult.Success"/> alone cannot make. The axis is "should this be retried?",
/// and it is orthogonal to <c>Success</c>.
///
/// <para><b>Why three members and not two.</b> The two non-dispatch cases need OPPOSITE answers, so
/// collapsing them is a real bug in either direction. A lost claim is TRANSIENT — the row becomes
/// claimable again once it goes stale — and the reschedule is the crash-recovery net (see
/// <see cref="ClaimLost"/>). The terminal cases can never be helped by a retry and loop forever if
/// scheduled (see <see cref="NotRetryable"/>). Pinned by
/// <c>CrashedHolderRecoveryCompositionPostgresTests</c>.</para>
/// </summary>
public enum DeliveryOutcome
{
    /// <summary>
    /// The payload was handed to a dispatcher (or failed inside one) and a terminal
    /// <c>DeliveryAttempt</c> row was written. <see cref="DeliveryResult.Success"/> says whether the
    /// endpoint accepted it. Retrying is meaningful: the attempt count advanced, so the backoff
    /// steps forward and the cap eventually dead-letters.
    ///
    /// <para>The DEFAULT, deliberately: an unmarked result keeps the retry queue running. A call
    /// site that forgets to mark itself degrades to noise (the old behaviour), never to silently
    /// abandoning an order that could still be delivered.</para>
    /// </summary>
    Dispatched = 0,

    /// <summary>
    /// Nothing was dispatched and no attempt row was written because the atomic claim matched 0 rows
    /// — another worker holds a FRESH <c>delivering</c> claim on this order.
    ///
    /// <para><b>TRANSIENT — keep retrying.</b> It is tempting to reason "the holder owns the send, so
    /// stay quiet"; that is wrong, and a real-Postgres test pins why. If the holder DIES,
    /// <c>StuckDeliveryDetectionService</c> re-drives the order — but it bumps <c>UpdatedAt</c> to
    /// now before enqueuing, so the re-driven retry finds a row that is 'delivering' and NOT yet
    /// stale, and bounces here again. The scheduled backoff is what carries the order past the
    /// reclaim window so a later attempt CAN claim it. Stop rescheduling and a crashed holder's PO is
    /// never sent — it is dead-lettered once the sweep burns its requeue budget.</para>
    ///
    /// <para>Bounded despite the frozen attempt count: the next run either claims the now-stale row
    /// (count advances) or finds the order terminal (<see cref="NotRetryable"/> → stop).</para>
    /// </summary>
    ClaimLost = 1,

    /// <summary>
    /// Nothing was dispatched, no attempt row was written, and no retry can ever change that: the
    /// order is gone, delivered, dead-lettered, held for billing, past the attempt cap, has no
    /// artifact, or auto-deliver is off.
    ///
    /// <para><b>TERMINAL — never reschedule.</b> With no attempt row the count is frozen, so
    /// <c>attemptsMade &gt;= maxAttempts</c> never becomes true and the same backoff step is chosen
    /// forever — an unbounded ~30-min job loop against an order the retry is powerless to move.
    /// Whatever unblocks it (a billing reactivation re-drive, a re-transform, an operator) owns
    /// restarting delivery.</para>
    /// </summary>
    NotRetryable = 2,
}

/// <summary>Result of a single delivery dispatch attempt.</summary>
/// <param name="Success">True when the remote endpoint accepted the payload.</param>
/// <param name="ErrorMessage">Human-readable error; null on success.</param>
/// <param name="ResponseCode">HTTP response code for HTTP dispatches; null for SFTP/FTP.</param>
/// <param name="ResponseBody">
/// Raw supplier response/NACK body (rejection capture). Populated on a non-2xx HTTP response so the
/// full refusal reason is persisted on the delivery attempt; null when no body was received.
/// </param>
/// <param name="Outcome">
/// What the call actually did, and therefore whether to RETRY. Callers must branch on this, not on
/// <paramref name="ErrorMessage"/> text: a lost claim, a terminal no-op, and a transient network
/// failure are all <c>Success=false</c> with a null <paramref name="ResponseCode"/>, yet each needs a
/// different answer.
/// </param>
public record DeliveryResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode = null,
    string? ResponseBody = null,
    DeliveryOutcome Outcome = DeliveryOutcome.Dispatched);
