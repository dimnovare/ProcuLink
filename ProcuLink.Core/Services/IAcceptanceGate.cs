using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Core.Services;

/// <summary>
/// ONE supplier acceptance rule that REFUSES this order. Produced by
/// <see cref="ISupplierAcceptanceService.GetBlockingFailuresAsync"/> and consumed by
/// <see cref="IAcceptanceGate"/>.
/// <para><see cref="Message"/> is already the plain-language sentence
/// <c>AcceptanceMessages.ForFail</c> composed (what failed, actual vs expected, how to fix) — it is
/// NEVER re-worded downstream, so the operator reads the same words in the workshop, in the error
/// message, and in the audit trail.</para>
/// </summary>
/// <param name="Code">The stored <c>{fieldPath}.{operator}</c> validation code.</param>
/// <param name="LineNumber">The line a line-scoped rule failed on; null for an order-scoped rule.</param>
/// <param name="Message">The plain-language sentence, shared verbatim with the workshop panel.</param>
/// <param name="RuleId">The acceptance rule that refused. Null only for a blocker built without a
/// rule behind it (which the gate never does — see <c>GetBlockingFailuresAsync</c>).</param>
/// <param name="ProfileVersion">The acceptance profile's <c>VersionNo</c> at evaluation time.</param>
/// <param name="ExpectedValue">What the rule demanded, verbatim from the rule row.</param>
/// <param name="ActualValue">What the order actually said — the value the rule judged.</param>
public sealed record AcceptanceBlocker(
    string  Code,
    int?    LineNumber,
    string  Message,
    Guid?   RuleId         = null,
    int?    ProfileVersion = null,
    string? ExpectedValue  = null,
    string? ActualValue    = null)
{
    /// <summary>
    /// Stable identity of ONE blocking failure — this is what an operator override excuses, and the
    /// ONLY thing it excuses.
    ///
    /// <para><b>Why it carries this much.</b> The key was once just
    /// <c>{fieldPath}.{operator}#{line}</c>, which named the rule and the line and nothing else. An
    /// override recorded against "line 3 is 50,001 over a 50,000 cap" therefore also excused line 3
    /// at 900,000 after a re-parse, and excused it again after the supplier lowered the cap to 100,
    /// and again under a newly published profile version — three different failures wearing one
    /// name. The interface promised the opposite in prose. So the identity now spans everything that
    /// makes a failure THIS failure:</para>
    /// <list type="bullet">
    ///   <item><description><b>which rule</b> — <see cref="RuleId"/>, so a different rule producing
    ///     the same code is a different failure;</description></item>
    ///   <item><description><b>which line</b> — excusing line 3 must never excuse line 7;</description></item>
    ///   <item><description><b>which version of the profile</b> — an override is consent to send
    ///     under the terms the operator read, not under every version published afterwards;</description></item>
    ///   <item><description><b>what was demanded and what was found</b> — digested together, so a
    ///     re-parse that changes the judged value, or an edit that tightens the threshold, produces a
    ///     key nobody has signed off.</description></item>
    /// </list>
    ///
    /// <para>The demanded/found pair is hashed rather than spelled out so the key stays a bounded,
    /// delimiter-safe token: order values are free text and would otherwise be able to forge a key
    /// boundary. Truncation to 16 hex characters is deliberate — this is a change detector inside an
    /// org-scoped, server-derived key, not a security boundary; an override cannot be forged in the
    /// first place, because the excused set is always recomputed server-side from the order's own
    /// current failures.</para>
    ///
    /// <para><b>Keys recorded before this shape existed no longer match anything.</b> Those
    /// overrides stop covering their orders, which blocks the order again until an operator
    /// re-signs. That is the safe direction — the alternative is honouring an authorisation whose
    /// subject we cannot identify.</para>
    /// </summary>
    public string Key
    {
        get
        {
            var line    = LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var rule    = RuleId?.ToString("N", CultureInfo.InvariantCulture) ?? "-";
            var version = ProfileVersion?.ToString(CultureInfo.InvariantCulture) ?? "-";
            return $"{Code}#{line}@{rule}.v{version}~{ValueDigest}";
        }
    }

    /// <summary>
    /// Short digest of (expected, actual). <c>U+001F</c> (ASCII unit separator) joins them: it is a
    /// control character, so it cannot occur in a rule's expected value or in a parsed order field,
    /// and two different pairs can therefore never digest the same input.
    /// </summary>
    private string ValueDigest
    {
        get
        {
            var payload = $"{ExpectedValue}\u001F{ActualValue}";
            var hash    = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }
}

/// <summary>
/// The gate's answer for one order: may it be transformed and sent, and if not, why not.
/// </summary>
/// <param name="Blocked">True when the order must NOT transform.</param>
/// <param name="Blockers">Every refusing rule (non-empty even when an override cleared them,
/// so the UI can show WHAT was overridden).</param>
/// <param name="Reason">The plain-language refusal sentence, or null when nothing blocks.</param>
/// <param name="Overridden">True when an operator override covers every current blocker.</param>
/// <param name="OverriddenBy">Who recorded the override (the Clerk user id).</param>
/// <param name="OverrideReason">Why they recorded it.</param>
public sealed record AcceptanceGateDecision(
    bool                             Blocked,
    IReadOnlyList<AcceptanceBlocker> Blockers,
    string?                          Reason,
    bool                             Overridden      = false,
    string?                          OverriddenBy    = null,
    string?                          OverrideReason  = null)
{
    /// <summary>Nothing blocks this order.</summary>
    public static AcceptanceGateDecision Clear { get; } =
        new(false, Array.Empty<AcceptanceBlocker>(), null);
}

/// <summary>Why an override was not recorded. Typed so callers map it to a status code without
/// matching on message text — a message is for a human to read, never for code to branch on.</summary>
public enum AcceptanceOverrideRefusal
{
    /// <summary>Recorded successfully.</summary>
    None = 0,

    /// <summary>No such order for this organisation.</summary>
    OrderNotFound,

    /// <summary>No reason given. An override without a stated reason is not an audit trail.</summary>
    ReasonMissing,

    /// <summary>Nothing to override — the supplier's rules do not refuse this order.</summary>
    NotBlocked,
}

/// <summary>Outcome of recording an operator override.</summary>
public sealed record AcceptanceOverrideResult(
    bool                             Recorded,
    AcceptanceOverrideRefusal        Refusal,
    string?                          Error,
    IReadOnlyList<AcceptanceBlocker> Excused);

/// <summary>
/// Audit vocabulary for the acceptance gate. These action strings are the durable record — the
/// override is state, not a log line, so the names are contract, not cosmetics.
/// </summary>
public static class AcceptanceGateAudit
{
    /// <summary>An operator authorised sending an order the supplier's rules refuse.</summary>
    public const string OverriddenAction = "AcceptanceOverridden";

    /// <summary>The gate refused a transform.</summary>
    public const string BlockedAction = "AcceptanceBlocked";

    /// <summary>A transform proceeded ONLY because a recorded override covered every blocker.</summary>
    public const string OverrideUsedAction = "AcceptanceOverrideUsed";

    /// <summary>Payload key holding the <see cref="AcceptanceBlocker.Key"/> list an override excuses.</summary>
    public const string ExcusedKey = "excused";

    /// <summary>Payload key holding the Clerk user id of the operator who recorded the override.</summary>
    public const string ActorKey = "by";

    /// <summary>Payload key holding the operator's stated reason.</summary>
    public const string ReasonKey = "reason";
}

/// <summary>
/// Composes the ONE human sentence the operator sees when the gate refuses. Lives here (not in the
/// Api layer) so the transform refusal, the API response and the audit payload cannot drift into
/// three different wordings.
/// </summary>
public static class AcceptanceGateMessage
{
    /// <summary>How many blocking sentences to spell out before summarising the rest.</summary>
    private const int MaxSpelledOut = 3;

    /// <summary>
    /// "This order wasn't sent because {supplier} doesn't accept it: {reason 1} {reason 2} … "
    /// plus the two ways out (fix it, or override with a reason). Each blocker's own message
    /// already states what failed, actual vs expected, and the fix.
    /// </summary>
    public static string Compose(IReadOnlyList<AcceptanceBlocker> blockers, string? supplierName)
    {
        if (blockers.Count == 0) return string.Empty;

        var who   = string.IsNullOrWhiteSpace(supplierName) ? "this supplier" : supplierName.Trim();
        var shown = blockers.Take(MaxSpelledOut).Select(b => Sentence(b.Message));
        var body  = string.Join(" ", shown);

        var more = blockers.Count > MaxSpelledOut
            ? $" And {blockers.Count - MaxSpelledOut} more like it."
            : string.Empty;

        return $"This order wasn't sent because it doesn't meet what {who} accepts. {body}{more} "
             + "Fix the order and send it again, or record an override saying why it should go anyway.";
    }

    /// <summary>
    /// One blocker message as a standalone sentence: capitalised and full-stopped.
    ///
    /// <para>The stored message is composed field-name-first and un-capitalised
    /// (<c>"currency must be EUR — it's “USD”. Set currency to EUR."</c>) because the UI renders it
    /// UNDER the rule's own headline, where a capital would read as a second title. Spliced into a
    /// prose paragraph here it is the sentence, so it gets sentence case. The stored message itself
    /// is untouched — the workshop's existing wording is pinned by <c>AcceptanceMessagesTests</c>
    /// and is not this work package's to change.</para>
    /// </summary>
    private static string Sentence(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (!trimmed.EndsWith('.')) trimmed += ".";
        return char.IsLower(trimmed[0]) ? char.ToUpperInvariant(trimmed[0]) + trimmed[1..] : trimmed;
    }
}

/// <summary>
/// The SERVER-SIDE acceptance gate: the single authority on whether an order may be transformed and
/// delivered under the supplier's acceptance profile.
///
/// <para><b>Why this exists.</b> Before WP-17, <see cref="ISupplierAcceptanceService.ValidateOrderAsync"/>
/// had exactly two production callers and both were HTTP controllers, so enforcement was
/// BROWSER-ONLY: an order that the supplier profile UI said would be blocked went out anyway
/// through any path that did not pass through those two endpoints. The product stated a guarantee
/// it did not keep. This interface is the one place that guarantee is now kept.</para>
///
/// <para><b>Where it is consulted — and why the transform alone was not enough.</b>
/// <list type="bullet">
///   <item><description><c>OrderTransformService.TransformAsync</c>, the single server-side
///     transform door, so browser send, REST ingress, inbound email and every auto-drive inherit one
///     answer before any document exists.</description></item>
///   <item><description>The MANUAL SEND endpoints, which dispatch a document that already exists:
///     <c>OrdersController.Redeliver</c> (the inbox's bulk "Send selected" and a parked order's
///     "Send again"), <c>OrdersController.RetryDelivery</c>, and <c>OpsController.RequeueDelivery</c>.
///     <c>Organisation.AutoDeliver</c> defaults to FALSE, so <c>ready_to_deliver</c> is the ordinary
///     resting state of a live order and those endpoints — not the transform — are how most orders
///     actually reach a supplier. Gating only the transform left the busiest send path open.</description></item>
/// </list></para>
///
/// <para><b>What is deliberately NOT gated.</b> The automatic continuations of a dispatch that has
/// already begun: <c>RetryDeliveryJob</c>'s backoff queue, <c>DeliverOrderJob</c>'s scheduled retry,
/// <c>StrandedReadyOrderDetectionService</c>, and <c>TransformOrderJob</c>'s inline stranded
/// recovery. Delivery is at-least-once, so by the time those run the supplier may already hold the
/// document; refusing there un-sends nothing and converts a transient failure — or a lost enqueue —
/// into a permanent strand. The line is: the gate answers "may a person send this?" wherever a
/// HUMAN decides to send, and it does not re-answer inside a send it already admitted.</para>
///
/// <para><b>Scope, deliberately narrow.</b> Only SUPPLIER ACCEPTANCE RULES block: a rule whose
/// severity is <c>error</c>, or which sets <c>BlockOnFail</c>. The mandatory invariants
/// (<c>invariant.*</c>) and the output-render checks (<c>output.*</c>) stay ADVISORY — they are
/// shown, not enforced. Promoting them here would start silently refusing orders that deliver fine
/// today, which is the opposite of keeping a promise. Because that is the server's real behaviour,
/// <c>GET /api/orders/{id}/validation</c> derives its <c>Blocking</c> badge from
/// <see cref="ISupplierAcceptanceService.GetBlockingFailuresAsync"/> — the same answer — rather than
/// from row severity, which used to badge a failing invariant as blocking while the gate sent the
/// order anyway.</para>
/// </summary>
public interface IAcceptanceGate
{
    /// <summary>
    /// The gate's decision for one order. Returns null when the order does not exist for this
    /// organisation (callers should 404). Never mutates the order.
    /// </summary>
    Task<AcceptanceGateDecision?> EvaluateAsync(Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// Records an operator override for the order's CURRENT blockers, with who and why, into the
    /// audit trail. The override excuses exactly the failures that exist right now, identified by
    /// <see cref="AcceptanceBlocker.Key"/> — so a blocker that appears later, a rule edited
    /// underneath it, a re-parse that changes the judged value, or a newly published profile version
    /// all produce failures the override does not cover, and the gate refuses again.
    /// </summary>
    Task<AcceptanceOverrideResult> RecordOverrideAsync(
        Guid orgId, Guid orderId, string actor, string reason, CancellationToken ct);
}
