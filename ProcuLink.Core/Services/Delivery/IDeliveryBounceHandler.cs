namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Handles an asynchronous delivery failure reported by the email provider AFTER the send was
/// accepted — a hard bounce or a spam complaint.
///
/// <para><b>Why this exists.</b> "Delivered" on the email channels means only that the first hop
/// accepted the handoff: a 250 from the relay (<c>SmtpDeliveryDispatcher</c>) or a Postmark 2xx
/// (<c>EmailApiDeliveryDispatcher</c>). A mistyped supplier address is accepted at that moment and
/// bounces seconds later, and until this existed nothing consumed that bounce — so the order read
/// <c>delivered</c> permanently. That inverts what this product sells, which is an auditable trail
/// of what really happened to a purchase order.</para>
///
/// <para><b>Correlation.</b> The provider's webhook does not know about orders, so the outbound
/// send stamps <see cref="DeliveryBounceMetadata.IdempotencyKeyField"/> into the provider's own
/// metadata bag, which the provider echoes back on the bounce. That value is the delivery
/// idempotency key, which is already persisted on <c>DeliveryAttempt.IdempotencyKey</c> — so the
/// bounce resolves to exactly the attempt that sent the message, and through it to the order and
/// the organisation. No column and no schema change: the key that already makes a re-send safe is
/// the key that makes a bounce attributable.</para>
///
/// <para>A bounce that carries no usable key, or whose key matches no attempt, is NOT silently
/// dropped — see <see cref="DeliveryBounceOutcome.Uncorrelated"/>. A webhook that cannot say it
/// failed to attribute something is the same defect in a new place.</para>
/// </summary>
public interface IDeliveryBounceHandler
{
    Task<DeliveryBounceResult> HandleAsync(DeliveryBounceNotification notification, CancellationToken ct);
}

/// <summary>
/// Provider-neutral bounce/complaint notification. Field names deliberately avoid Postmark's
/// vocabulary so a second provider maps onto the same handler.
/// </summary>
/// <param name="IdempotencyKey">
/// The value the provider echoed back from the outbound metadata bag — the delivery idempotency
/// key. Null when the message carried none (a test fire) or the provider dropped it.
/// </param>
/// <param name="Kind">What the provider reported.</param>
/// <param name="Recipient">The address that bounced, for the operator-facing reason string.</param>
/// <param name="Description">The provider's own description of the failure, verbatim.</param>
/// <param name="ProviderMessageId">The provider's message id, kept for forensics in the audit row.</param>
public sealed record DeliveryBounceNotification(
    string? IdempotencyKey,
    DeliveryBounceKind Kind,
    string? Recipient,
    string? Description,
    string? ProviderMessageId);

/// <summary>
/// What the provider reported. Only the terminal kinds reach the handler: a SOFT bounce is a
/// transient condition the provider retries by itself, and moving an order off <c>delivered</c> for
/// one would manufacture a failure the supplier never saw.
/// </summary>
public enum DeliveryBounceKind
{
    /// <summary>The address does not exist or permanently refuses mail. The supplier never got it.</summary>
    Hard,

    /// <summary>
    /// The recipient marked the message as spam. The message may have been seen, but the channel is
    /// no longer trustworthy for this supplier and the operator has to know.
    /// </summary>
    SpamComplaint,
}

/// <summary>Outcome of handling one notification.</summary>
public sealed record DeliveryBounceResult(DeliveryBounceOutcome Outcome, Guid? OrderId = null);

public enum DeliveryBounceOutcome
{
    /// <summary>The order was moved off <c>delivered</c> and an exception was opened.</summary>
    OrderMarkedFailed,

    /// <summary>
    /// The attempt was found, but the order is no longer in a state a bounce can contradict — it has
    /// already been re-sent, or already failed. Recorded, not re-applied.
    /// </summary>
    AlreadyResolved,

    /// <summary>
    /// No attempt could be resolved from the notification. The bounce is real and unattributed:
    /// logged at Warning, never treated as handled.
    /// </summary>
    Uncorrelated,
}

/// <summary>
/// The metadata bag ProcuLink stamps on outbound supplier email so the provider's asynchronous
/// failure webhooks can be attributed back to an order.
/// </summary>
public static class DeliveryBounceMetadata
{
    /// <summary>
    /// Metadata field carrying the delivery idempotency key. Prefixed so it cannot collide with a
    /// provider-reserved name. Postmark lower-cases metadata keys on the way back out, so the
    /// reader must compare case-insensitively — see the handler.
    /// </summary>
    public const string IdempotencyKeyField = "pl_delivery_key";
}
