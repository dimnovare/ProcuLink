namespace ProcuLink.Core.Services;

/// <summary>
/// Machine-readable outcomes of one auto-send evaluation. These strings are the durable record and
/// the thing a reader groups 500 rows by, so they are contract, not cosmetics.
/// </summary>
public static class AutoSendDecision
{
    /// <summary>Every condition held. Stage 2 would have sent this order with no human involved.</summary>
    public const string Clean = "clean";

    // ── Not recorded: the order never reached the opt-in ──────────────────────

    /// <summary>No such order for this organisation.</summary>
    public const string OrderNotFound = "order_not_found";

    /// <summary>The order has no supplier, so there is no per-supplier switch to consult.</summary>
    public const string NoSupplier = "no_supplier";

    /// <summary>
    /// The supplier has no delivery configuration. Because the opt-in LIVES on that row, this is the
    /// same state as "not opted in" — which is why the packet's "the supplier has a delivery config"
    /// condition is structural here rather than a check that could be forgotten.
    /// </summary>
    public const string NoDeliveryConfig = "no_delivery_config";

    /// <summary>The supplier's auto-send switch is off. The ordinary case; nothing is recorded.</summary>
    public const string AutoTransformOff = "auto_transform_off";

    // ── Recorded refusals: opted in, but held back ────────────────────────────

    /// <summary>The order is not in <c>ready</c> — it is still parsing, needs review, failed, or has already moved on.</summary>
    public const string StatusNotReady = "status_not_ready";

    /// <summary>At least one line still needs a human. Checked independently of the status, not inferred from it.</summary>
    public const string UnresolvedLines = "unresolved_lines";

    /// <summary>
    /// Another order in this workspace already carries this PO number, and the resulting
    /// <c>duplicate_po_number</c> exception is still open — nobody has looked at it yet.
    ///
    /// <para>Duplicate detection is deliberately advisory: it opens a warning and blocks nothing,
    /// because suppliers legitimately reuse PO numbers and a hard block would refuse genuine
    /// orders. That trade-off is sound while a human is reading the warning before clicking Send.
    /// It stops being sound the moment a machine is doing the clicking, because the failure it
    /// guards against — the same PO going out twice — is precisely the one nobody can take back.
    /// So an unresolved duplicate warning is a human decision, and auto-send declines it.</para>
    ///
    /// <para>Only <c>open</c> counts. An operator who resolved or ignored the warning has already
    /// made that decision, and re-asking them would make the flag impossible to clear.</para>
    /// </summary>
    public const string PossibleDuplicate = "possible_duplicate";

    /// <summary>The supplier is opted in but no delivery channel resolves, so there is nowhere to send.</summary>
    public const string NoDeliveryChannel = "no_delivery_channel";

    /// <summary>The supplier's own acceptance rules refuse this order.</summary>
    public const string AcceptanceBlocked = "acceptance_blocked";

    /// <summary>
    /// The acceptance rules refuse this order and an operator override excuses them. A human
    /// authorising ONE send is not a licence to send unattended, so this is never clean.
    /// </summary>
    public const string AcceptanceOverridden = "acceptance_overridden";

    /// <summary>
    /// The acceptance gate could not be evaluated (a DB blip, a malformed pin). An order nobody
    /// could check is not one to send — recorded distinctly so the trail never claims the supplier
    /// refused it.
    /// </summary>
    public const string AcceptanceGateUnavailable = "acceptance_gate_unavailable";
}

/// <summary>
/// The result of evaluating one order for auto-send.
/// </summary>
/// <param name="Recorded">True when a row was written. False when the order never reached the
/// per-supplier opt-in (no supplier, no delivery config, switch off) — those are the ordinary case
/// and would otherwise bury the signal under a row per parsed order.</param>
/// <param name="WouldHaveSent">True only for <see cref="AutoSendDecision.Clean"/>.</param>
/// <param name="Decision">One of <see cref="AutoSendDecision"/>'s constants.</param>
/// <param name="Channel">Resolved delivery protocol, or null.</param>
/// <param name="OutputFormat">Resolved output format, or null.</param>
/// <param name="AlreadyEvaluated">True when this order had already been recorded and this run was a
/// no-op — the Hangfire-refetch case.</param>
public sealed record AutoSendDryRunOutcome(
    bool    Recorded,
    bool    WouldHaveSent,
    string  Decision,
    string? Channel          = null,
    string? OutputFormat     = null,
    bool    AlreadyEvaluated = false);

/// <summary>
/// WP-33 stage 1 — <b>auto-send when clean, in dry run</b>.
///
/// <para><b>What this does, and the one thing it must never do.</b> It builds the entire decision a
/// real auto-send would make — is the supplier opted in, is the order <c>ready</c>, is every line
/// resolved, does the supplier's acceptance profile allow it, which channel and format would carry
/// it — and then writes an <see cref="Entities.AutoSendDryRun"/> row and STOPS. Nothing is
/// transformed, nothing is dispatched, no order status changes. The founder's standing ruling is
/// that no purchase order reaches a real supplier without a human click, and stage 1 is how that
/// ruling survives contact with the automation being built for stage 2.</para>
///
/// <para><b>Why "clean" is not defined here.</b> It is read off the code that already decides it:
/// the order's own <c>ready</c> status, its lines' <c>NeedsReview</c> flags, and
/// <see cref="IAcceptanceGate.EvaluateAsync"/> — WP-17's server-side gate, the single authority on
/// whether a supplier's rules permit an order. A second definition of "clean" living here would
/// drift from the one the real transform enforces, and the day they disagreed, this table would be
/// certifying orders the transform door refuses (or worse, the reverse).</para>
///
/// <para><b>Why there is no artifact hash.</b> The obvious way to hash the document that would have
/// been sent is to render it. Both routes to that are refused:</para>
/// <list type="bullet">
///   <item><description>Re-implementing the render means reproducing <c>OrderTransformService</c>'s
///     six-branch selection over per-order overrides, promoted supplier output, revision-pinned
///     output trees and the fixed transformer — a second, divergent definition of the outgoing
///     document, which is the same defect class as a second definition of "clean".</description></item>
///   <item><description>Calling the real transform is a LIVE SEND. It claims the order into
///     <c>transforming</c>, uploads an artifact, and commits <c>ready_to_deliver</c> — at which
///     point the order matches <c>StrandedReadyOrderDetectionService</c>'s signature exactly
///     (ready_to_deliver, artifact present, no attempt against it) and the sweep enqueues delivery
///     within its aged window. The dry run would never have called a delivery primitive, and the PO
///     would still go out.</description></item>
/// </list>
/// <para>So the row records <c>DecisionDigest</c> — a hash of the inputs that determine the
/// document — under its own honest name, and stage 2 records the real
/// <c>OutboundArtifact.ArtifactSha256</c> at the moment a document actually exists.</para>
/// </summary>
public interface IAutoSendDryRunEvaluator
{
    /// <summary>
    /// Evaluate one order and, when its supplier is opted in, record the decision. Org-scoped.
    /// Idempotent: a second call for the same order is a no-op, enforced by a unique index rather
    /// than by the pre-check that usually avoids reaching it.
    /// </summary>
    Task<AutoSendDryRunOutcome> EvaluateAsync(Guid orgId, Guid orderId, CancellationToken ct);
}
