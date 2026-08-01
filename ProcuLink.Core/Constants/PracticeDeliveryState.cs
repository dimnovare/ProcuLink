namespace ProcuLink.Core.Constants;

/// <summary>
/// What will actually happen when the operator presses "send" on a practice order.
///
/// <para><b>Why three states and not a bool.</b> The bool that came before answered "did the
/// seeder write a delivery config just now", while every consumer read it as "does this supplier
/// have a delivery target". Those diverge in exactly one situation, and WP-39 §4.5 is what it
/// looks like: the sample supplier already carried a hand-made HTTP target, the seeder's guards
/// returned early without ever looking at it, and the review screen told the operator the run
/// would "stop at no delivery is set up". It did not stop. It POSTed to the endpoint that was
/// sitting there — a dead request bin in that instance, and on another organisation it could as
/// easily be a real supplier, moments after the screen promised nothing would reach one.</para>
///
/// <para>Values are wire contract: the API returns them and the frontend mirrors them in
/// <c>src/hooks/useSampleOrder.ts</c>. Adding a fourth means deciding what the review screen says
/// for it — which is why <see cref="All"/> exists and is asserted against.</para>
/// </summary>
public static class PracticeDeliveryState
{
    /// <summary>
    /// The practice mailbox is set up: the finished supplier-ready file is emailed to the address
    /// the operator gave, from ProcuLink's own verified sender. Nothing reaches a real supplier.
    /// </summary>
    public const string EmailedToYou = "emailed_to_you";

    /// <summary>
    /// This supplier has no delivery target at all, so the run genuinely stops after transform.
    /// The honest pre-WP-27 ending: nothing is sent and nothing is promised.
    /// </summary>
    public const string NotSetUp = "not_set_up";

    /// <summary>
    /// A delivery target exists that this practice run did not set up — some other protocol, or an
    /// address ProcuLink did not choose. It is deliberately LEFT ALONE (a seeder that silently
    /// deletes an operator's delivery configuration is a worse surprise than the one being fixed),
    /// which means pressing send WILL dispatch through it. The operator has to be told that before
    /// they press it.
    /// </summary>
    public const string ExistingTarget = "existing_target";

    /// <summary>Every state, so a walk over them cannot silently miss one.</summary>
    public static readonly IReadOnlyList<string> All = [EmailedToYou, NotSetUp, ExistingTarget];
}
