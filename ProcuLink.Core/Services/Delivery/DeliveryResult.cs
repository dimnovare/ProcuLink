namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Whether a delivery call actually reached the supplier dispatcher — the distinction the
/// automatic retry queue needs, and which <see cref="DeliveryResult.Success"/> alone cannot make.
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
    /// Nothing was dispatched and NO attempt row was written — the order was not claimable (gone,
    /// terminal, held, dead-lettered, missing its artifact) or the atomic claim was lost to another
    /// worker that owns the in-flight send.
    ///
    /// <para>Retrying is NOT meaningful and must not be scheduled: with no attempt row the count is
    /// frozen, so <c>attemptsMade &gt;= maxAttempts</c> never becomes true and the same backoff step
    /// is chosen forever — an unbounded job loop against an order the retry cannot move. Whatever
    /// unblocks the order (the claim holder finishing, a billing reactivation re-drive, a re-transform,
    /// an operator) owns getting it moving again.</para>
    /// </summary>
    NotAttempted = 1,
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
/// Whether a dispatch was actually attempted. Callers deciding whether to RETRY must branch on this,
/// not on <paramref name="ErrorMessage"/> text: a lost claim and a transient network failure are both
/// <c>Success=false</c> with a null <paramref name="ResponseCode"/>, and only one of them is retryable.
/// </param>
public record DeliveryResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode = null,
    string? ResponseBody = null,
    DeliveryOutcome Outcome = DeliveryOutcome.Dispatched);
