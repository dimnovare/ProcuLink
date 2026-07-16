namespace ProcuLink.Core.Services.Delivery;

/// <summary>Result of a single delivery dispatch attempt.</summary>
/// <param name="Success">True when the remote endpoint accepted the payload.</param>
/// <param name="ErrorMessage">Human-readable error; null on success.</param>
/// <param name="ResponseCode">HTTP response code for HTTP dispatches; null for SFTP/FTP.</param>
/// <param name="ResponseBody">
/// Raw supplier response/NACK body (rejection capture). Populated on a non-2xx HTTP response so the
/// full refusal reason is persisted on the delivery attempt; null when no body was received.
/// </param>
/// <param name="Parked">
/// True when the delivery was deliberately NOT sent because its outcome could not be known and the
/// channel cannot de-duplicate a re-send (the unknown-outcome park — see
/// <c>DeliveryService.ParkUnconfirmedAsync</c>). The order is now <c>delivery_unconfirmed</c>, a
/// status <c>RetryDeliveryAsync</c> refuses as non-retryable — its early return for that status
/// persists no new attempt row, so a caller that schedules an automatic retry anyway would see the
/// attempt count never advance: the backoff queue would reschedule itself at the SAME delay
/// forever, never re-sending, never dead-lettering, never resolving. Callers MUST check this flag
/// before scheduling a retry AND before dead-lettering on a failed result — a park is a deferral to
/// an operator, not a failure. Distinct from an ordinary failure, which stays retryable.
/// </param>
public record DeliveryResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode = null,
    string? ResponseBody = null,
    bool Parked = false);
