namespace ProcuLink.Core.Constants;

/// <summary>
/// The outbound integration (webhook) event catalogue — the ONE place an event type string is
/// written down.
///
/// <para>
/// This exists because the catalogue used to live in two unlinked places: a hand-typed
/// <c>validEvents</c> array inside <c>IntegrationController.Create</c>, and raw string literals at
/// each emit site. Nothing tied them together, and they had already drifted:
/// <c>DeliveryService</c> emitted <c>order.rejected</c>, which was absent from
/// <c>validEvents</c> — so no subscription for it could ever be created, and every
/// <c>order.rejected</c> emitted fanned out to zero subscribers. The emit site looked correct in
/// review; the event simply could not be received.
/// </para>
///
/// <para>
/// <b>Why the subscribe list and the emit sites must share these constants.</b>
/// <c>IntegrationTriggerService.EnqueueAsync</c> matches subscriptions by EXACT string equality
/// (<c>s.EventType == eventType</c>) with no wildcard or prefix handling, and
/// <c>IntegrationController.Create</c> is the only way a subscription row is ever inserted. So an
/// event that is emitted but not subscribable is not a degraded notification — it is silence that
/// reads like a working feature at the call site. Referencing these constants from both ends makes
/// a typo a compile error instead of a permanently undelivered event.
/// </para>
///
/// <para>
/// <b>Adding an event:</b> add the constant AND add it to <see cref="Subscribable"/>. The pairing is
/// enforced by reflection in <c>IntegrationEventTypesAreSubscribableTests</c>, which walks every
/// public const on this class — so a constant added without a <see cref="Subscribable"/> entry fails
/// the build rather than shipping as an unreceivable event.
/// </para>
/// </summary>
public static class IntegrationEventTypes
{
    /// <summary>An order was ingested. Emitted by <c>OrderIngestionService</c>.</summary>
    public const string OrderCreated = "order.created";

    /// <summary>An order reached the supplier. Emitted by <c>DeliveryService</c>.</summary>
    public const string OrderDelivered = "order.delivered";

    /// <summary>A single delivery attempt failed. Emitted per ATTEMPT by <c>DeliveryService</c>.</summary>
    public const string OrderFailed = "order.failed";

    /// <summary>The supplier actively rejected the order. Emitted by <c>DeliveryService</c>.</summary>
    public const string OrderRejected = "order.rejected";

    /// <summary>
    /// Delivery is over and the order never reached the supplier — the retry budget is spent and
    /// nothing further is scheduled.
    ///
    /// <para>
    /// Deliberately DISTINCT from <see cref="OrderFailed"/>, which fires once per failed attempt and
    /// therefore says "this try did not work" — a statement that is compatible with the order still
    /// arriving on the next retry. This event is the terminal one: it says the order is NOT coming.
    /// A subscriber cannot derive it from <see cref="OrderFailed"/>, because the attempt cap is
    /// server-side configuration the subscriber cannot see, so counting <see cref="OrderFailed"/>
    /// events can never tell it which failure was the last.
    /// </para>
    /// </summary>
    public const string OrderDeadLettered = "order.dead_lettered";

    /// <summary>
    /// Every event type a subscription may be created for. This is the allow-list enforced by
    /// <c>IntegrationController.Create</c>, and it must contain every constant above — an emitted
    /// event missing from here fans out to nobody.
    /// </summary>
    public static readonly IReadOnlyList<string> Subscribable = new[]
    {
        OrderCreated,
        OrderDelivered,
        OrderFailed,
        OrderRejected,
        OrderDeadLettered,
    };
}
