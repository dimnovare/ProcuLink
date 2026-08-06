namespace ProcuLink.Core.Entities;

/// <summary>
/// ONE recorded auto-send decision for ONE order, made while auto-send is in its DRY-RUN stage
/// (WP-33 stage 1): the row says what would have been sent, over which channel, and why the order
/// was — or was not — considered clean. Nothing is dispatched. A week of these rows is the evidence
/// the founder reads before authorising stage 2, where the same decision starts moving real orders.
///
/// <para><b>Why a typed table rather than an <see cref="AuditEvent"/> payload.</b> The founder's
/// question is aggregate, not per-order: "how many would have gone out, over which channels, and
/// what held the rest back?". Answering that from <c>audit_events.payload</c> means parsing 500
/// jsonb blobs with no index. Here it is one <c>GROUP BY</c> over
/// <see cref="WouldHaveSent"/> / <see cref="Decision"/> / <see cref="Channel"/>, which is the
/// difference between the data being readable and being theoretically present.</para>
///
/// <para><b>One row per order, forever.</b> The unique index on
/// (<see cref="OrgId"/>, <see cref="OrderId"/>) is what makes a Hangfire refetch harmless: the
/// re-run recomputes the same decision and the insert is refused, so a replayed job can neither
/// double-count a would-be send nor (in stage 2) double-send one.</para>
/// </summary>
public class AutoSendDryRun
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation. Every read of this table is scoped by it.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The order the decision was made about.</summary>
    public Guid OrderId { get; set; }

    /// <summary>
    /// The supplier the order would have been sent to. Never null in a recorded row — an order with
    /// no supplier cannot reach a per-supplier opt-in, so it is never evaluated. Nullable only so a
    /// later supplier deletion does not have to erase the evidence.
    /// </summary>
    public Guid? SupplierId { get; set; }

    /// <summary>
    /// The headline. True = every condition held and stage 2 would have transmitted this order with
    /// no human involved. False = something held it back, and <see cref="Decision"/> says what.
    /// </summary>
    public bool WouldHaveSent { get; set; }

    /// <summary>
    /// Machine-readable outcome — one of <c>AutoSendDecision</c>'s constants. Branch on this, never
    /// on <see cref="Evidence"/>'s prose.
    /// </summary>
    public string Decision { get; set; } = string.Empty;

    /// <summary>
    /// The delivery protocol the order would have gone out over ('http', 'sftp', 'email', …),
    /// resolved through the SAME revision-pin-aware precedence dispatch itself uses. Null when the
    /// decision was reached before a channel could be resolved.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>The output format the document would have been rendered as. Null when unresolved.</summary>
    public string? OutputFormat { get; set; }

    /// <summary>
    /// SHA-256 over the ordered set of inputs that DETERMINE the outgoing document — not a hash of
    /// the document itself, because stage 1 deliberately never renders one (see
    /// <c>IAutoSendDryRunEvaluator</c> for why rendering would have to either duplicate the
    /// transform's branch selection or run the real transform, and why the second is a live send).
    /// Two rows sharing a digest would have produced byte-identical documents under identical
    /// config; a digest that changes between the dry run and the eventual real send means something
    /// moved underneath the decision.
    /// </summary>
    public string? DecisionDigest { get; set; }

    /// <summary>
    /// How many supplier acceptance rules refused this order at evaluation time. Zero is a
    /// precondition of <see cref="WouldHaveSent"/> — an operator override makes an order sendable
    /// BY HAND without making it clean.
    /// </summary>
    public int BlockerCount { get; set; }

    /// <summary>
    /// Structured supporting detail for one row: the order status, line counts, the resolved config
    /// source, and — when something refused — the blocking rules in the operator's own words. This
    /// is what a human reads after the aggregate query narrows 500 rows to the interesting ten.
    /// </summary>
    public string? Evidence { get; set; }

    public DateTime EvaluatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public PurchaseOrderEntity Order { get; set; } = null!;
}
