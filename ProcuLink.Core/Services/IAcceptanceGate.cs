using System.Globalization;

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
public sealed record AcceptanceBlocker(string Code, int? LineNumber, string Message)
{
    /// <summary>
    /// Stable identity of ONE blocking failure — this is what an operator override excuses.
    /// A line-scoped rule blocks per line, so the line number is part of the identity: excusing
    /// "line 3 is over the price cap" must not silently excuse line 7 as well.
    /// </summary>
    public string Key => LineNumber is int ln
        ? $"{Code}#{ln.ToString(CultureInfo.InvariantCulture)}"
        : Code;
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
/// it did not keep. This interface is the one place that guarantee is now kept, and
/// <c>OrderTransformService.TransformAsync</c> — the single server-side transform door — consults
/// it, so every ingress channel inherits the same answer.</para>
///
/// <para><b>Scope, deliberately narrow.</b> Only SUPPLIER ACCEPTANCE RULES block: a rule whose
/// severity is <c>error</c>, or which sets <c>BlockOnFail</c>. The mandatory invariants
/// (<c>invariant.*</c>) and the output-render checks (<c>output.*</c>) stay ADVISORY — they are
/// shown, not enforced. Promoting them here would start silently refusing orders that deliver fine
/// today, which is the opposite of keeping a promise.</para>
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
    /// audit trail. The override excuses exactly the failures that exist right now — a blocker that
    /// appears later is NOT covered and blocks again.
    /// </summary>
    Task<AcceptanceOverrideResult> RecordOverrideAsync(
        Guid orgId, Guid orderId, string actor, string reason, CancellationToken ct);
}
