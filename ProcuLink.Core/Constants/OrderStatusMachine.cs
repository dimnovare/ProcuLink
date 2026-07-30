using static ProcuLink.Core.Constants.OrderStatusConstants;

namespace ProcuLink.Core.Constants;

/// <summary>
/// The explicit order-status state machine — the single source of truth for
/// "which status may follow which" and for the operation-specific entry guards
/// that were previously scattered as ad-hoc <c>if (status is not …)</c> checks
/// (audit d-2 / W2).
///
/// <para><b>Design note (no behaviour change):</b> the live order flow is
/// deliberately permissive — a manual "mark rejected" can fire from almost any
/// state, resolve recomputes pending_review↔ready, and a dead-letter requeue
/// rescues a dead-lettered order. <see cref="Transitions"/> is therefore a
/// <i>superset</i> of every transition the code actually performs, so
/// <see cref="IsAllowed"/> never rejects a real flow; it exists to document the
/// machine and to catch genuinely-impossible moves (e.g. delivered→parsing).
/// Hard <i>operation</i> guards live in the named sets below (e.g.
/// <see cref="RedeliverableFrom"/>) so a controller/service references one
/// canonical set instead of a hand-written literal.</para>
///
/// <para><b>Relationship to <c>OrderStatusTransitionObserver.AllowedTransitions</c>:</b> that map
/// is the codebase's other hand-maintained inventory of the same flows, and the two drift (the A5
/// delivery_held work in d4d6eac, and delivering→delivery_dead_letter, were each registered in only
/// one of them). Both maps are supersets of the real flows, but neither strictly contains the other:
/// the observer only logs, so it is generous ON PURPOSE, and it lists edges no call site performs.
/// This map is the stricter one — calling impossible moves impossible is its entire value, so it
/// does NOT simply mirror the observer. <c>OrderStatusMachineTests</c> pins the overlap
/// structurally: every observer edge must be allowed here unless it is exempted there with the
/// call-site evidence that it cannot happen.</para>
/// </summary>
public static class OrderStatusMachine
{
    /// <summary>from-status → the set of statuses the flow can move it to.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Transitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [PendingParse]       = Set(Parsing, Unrouted, RejectedBySupplier),
            [Parsing]            = Set(PendingReview, Ready, Failed, PendingParse, Unrouted, RejectedBySupplier),
            // Routing hold: parked awaiting a supplier; assigning one re-enqueues parse.
            [Unrouted]           = Set(Parsing, PendingParse, Failed, RejectedBySupplier),
            [PendingReview]      = Set(Ready, PendingReview, RejectedBySupplier),
            [Ready]              = Set(Transforming, PendingReview, RejectedBySupplier),
            // transforming → transform_failed: a TERMINAL transform failure (a template that would not
            // render, or an output mapping that could not be applied) — neither is fixable by retrying
            // the same inputs, so the order is parked visibly rather than reverted to 'ready'.
            [Transforming]       = Set(ReadyToDeliver, Ready, TransformFailed, Failed, RejectedBySupplier),
            // ready_to_deliver/delivered → ready: a mapping edit after transform (MV-1) invalidates
            // the artifact and resets the order so the next Send re-transforms.
            // ready_to_deliver → delivery_held: a billing flip pauses (not fails) delivery. This fires
            // BEFORE the 'delivering' claim, so it moves a still-idle, NEVER-dispatched order.
            // → delivered is GONE (WP-09). It was listed for the retired inbound supplier-status
            // webhook, and no other writer can perform it: OrdersController.MarkDelivered is gated on
            // ManuallyDeliverableFrom (delivery_unconfirmed only, checked twice), and DeliveryService
            // only writes 'delivered' AFTER its claim has moved the row to 'delivering'. Pinned by
            // OrderStatusMachineTests.EveryInboundDeliveredEdge_HasAProductionWriter.
            [ReadyToDeliver]     = Set(Delivering, DeliveryFailed, DeliveryHeld, Ready, RejectedBySupplier),
            // Billing hold → released back to ready_to_deliver when the org returns to good standing.
            // delivery_held → delivery_unconfirmed: the release RESTORES a held PARK instead of
            // re-driving it — HoldForBillingAsync records the origin (HeldFromStatus) and
            // ReleaseBillingHeldOrdersAsync branches on it, because an automatic re-send of an
            // unknown-outcome PO on a channel that cannot de-duplicate is the duplicate the park
            // exists to prevent.
            // → delivered is GONE (WP-09) — same reason as ready_to_deliver above. A held order is
            // settled by RELEASING it (→ ready_to_deliver, or → delivery_unconfirmed for a held
            // park) and settling it from there; it cannot be marked delivered in place.
            [DeliveryHeld]       = Set(ReadyToDeliver, DeliveryUnconfirmed, Ready, RejectedBySupplier),
            // delivering → delivery_unconfirmed: the park — a crash-recovery re-drive on a channel
            // that cannot de-duplicate stops rather than risk a duplicate PO.
            // delivering → delivery_dead_letter: StuckDeliveryDetectionService dead-letters an order
            // that kept stranding in 'delivering' after its re-drive budget was spent.
            [Delivering]         = Set(Delivered, DeliveryFailed, DeliveryUnconfirmed, DeliveryDeadLetter, RejectedBySupplier),
            [Delivered]          = Set(DeliveryFailed, Ready, RejectedBySupplier),
            // delivery_failed/delivery_dead_letter → ready: the MV-1 sibling — a mapping edit after a
            // failed/dead-lettered delivery invalidates the stored artifact (Retry/requeue would ship it
            // un-re-transformed), so the order resets and the next Send re-transforms.
            // delivery_failed → delivery_held: A5 — a backoff retry for an org that lapsed to
            // read_only/past_due since the first attempt is held (not delivered) via HoldForBillingAsync.
            // → delivered is GONE (WP-09) — same reason as ready_to_deliver above. An operator who
            // learns out-of-band that the supplier DID receive a failed-looking send cannot settle it
            // from here: MarkDelivered admits delivery_unconfirmed only. That is a real product gap,
            // named in PR #75 as a follow-up, not something to paper over by listing an edge no code
            // can perform.
            [DeliveryFailed]     = Set(Delivering, DeliveryDeadLetter, DeliveryHeld, Ready, RejectedBySupplier),
            // Unknown-outcome park. The operator decides: send again (→ delivering) or confirm the
            // supplier got it (→ delivered). A mapping edit invalidates the artifact (→ ready, the
            // MV-1 sibling). Dead-letter/failed remain reachable if a later re-send exhausts retries.
            // → delivery_held: the delivery_failed sibling — a "Send again" for an org that lapsed
            // since the park is held (not delivered) via HoldForBillingAsync.
            // → delivered/rejected_by_supplier: the operator answers the question the park is waiting
            // on — did the PO arrive? — after checking with the supplier out-of-band. The park keeps
            // the IdempotencyKey the pre-send commit stamped (ParkUnconfirmedAsync finalises the
            // re-adopted in-flight row), so the evidence that a send BEGAN survives for that call.
            [DeliveryUnconfirmed] = Set(Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, DeliveryHeld, Ready, RejectedBySupplier),
            // dead_letter → delivery_failed: an ops requeue that fails again.
            // → delivered is GONE (WP-09) — same reason as delivery_failed above.
            [DeliveryDeadLetter] = Set(Delivering, DeliveryFailed, Ready, RejectedBySupplier),
            [RejectedBySupplier] = Set(),
            [Failed]             = Set(),
            // transform_failed is a FAILURE state, not a terminal one — unlike 'failed' (a bad source
            // file: recovery is a new order row), a failed transform holds a perfectly good order whose
            // TEMPLATE/MAPPING is broken. Fixing that and re-transforming is the intended cure, so the
            // transform claim accepts transform_failed and re-enters 'transforming'
            // (OrderTransformService / OrdersController.Transform). It also holds NO artifact, so
            // nothing stale can be re-shipped. → ready: the compensating release when the re-transform
            // enqueue itself fails. → pending_review: a re-resolve that reopens the review loop.
            [TransformFailed]    = Set(Transforming, Ready, PendingReview, RejectedBySupplier),
        };

    /// <summary>Every known order status (the keys of the machine).</summary>
    public static IReadOnlyCollection<string> AllStatuses => (IReadOnlyCollection<string>)Transitions.Keys;

    /// <summary>True when <paramref name="to"/> is a documented successor of <paramref name="from"/>.</summary>
    public static bool IsAllowed(string from, string to) =>
        Transitions.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>The set of statuses that may follow <paramref name="from"/> (empty for unknown/terminal).</summary>
    public static IReadOnlySet<string> NextStatuses(string from) =>
        Transitions.TryGetValue(from, out var next) ? next : EmptySet;

    /// <summary>
    /// The statuses that are terminal BY DECLARATION — an order that reaches one is finished, and
    /// the product deliberately owes the operator no way out of it.
    ///
    /// <para><b>Declared, never derived — that distinction is the whole point.</b>
    /// <see cref="IsTerminal"/> computes terminality FROM an empty edge set, so using it to justify
    /// an empty edge set is circular, and that circle is exactly how
    /// <c>rejected_by_supplier</c> became an unintended dead end: <c>DeliveryService</c> routed
    /// every 400–499 there, the map gave it no successors, <see cref="RedeliverableFrom"/> excluded
    /// it, and each of those facts was justified by the others. An expired API key (401) or a moved
    /// endpoint (404) then parked a perfectly good PO in a state no control in the product could
    /// move — a database edit was the only recourse. Splitting this set out of the derivation is
    /// what lets <c>OrderStatusMachineTests.NoNonTerminalStatus_IsADeadEnd</c> ask the question at
    /// all: "does every status the product does NOT call finished have a way out?"</para>
    ///
    /// <para><c>failed</c> is the only member, and it earns it: a bad SOURCE FILE cannot be fixed
    /// in place. <c>ParseOrderJob</c> refuses to re-drive it and <c>OrdersController.Transform</c>
    /// answers "Upload a corrected file before transforming" — recovery is a NEW order row, which
    /// is a real exit, just not one that runs through this order. Adding a member here is a product
    /// decision ("this order is over"), not a way to silence the invariant.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> DeclaredTerminal = Set(Failed);

    /// <summary>
    /// A status with no outgoing transitions. <c>delivered</c> is NOT terminal, and the reasons are
    /// all human or local now that the inbound supplier-status webhook is retired (WP-09):
    /// <c>OrderResolutionService.MarkRejectedAsync</c> carries no from-status guard, so an operator
    /// can still reject a delivered order (HTTP 200 is transport success, not business acceptance);
    /// an MV-1 mapping edit resets it to <c>ready</c> for re-transform; and the
    /// <c>delivered → delivery_failed</c> entry survives for DeliveryService's PRE-CLAIM failure
    /// paths (<c>FailMissingConfigAsync</c> / <c>FailBeforeDispatchAsync</c>), which write
    /// <c>delivery_failed</c> with no status check and can race an enqueue-time guard.
    /// </summary>
    public static bool IsTerminal(string status) => NextStatuses(status).Count == 0;

    /// <summary>A status the UI renders as the red "Failed" pill (delegates to the canonical bucket).</summary>
    public static bool IsFailure(string status) => FailureBucket.Contains(status);

    // ── Operation-specific entry guards (centralised; match the prior literals) ──

    /// <summary>
    /// A manual "send again" (OrdersController.Redeliver) is valid only from a
    /// stalled-but-recoverable delivery state. (A dead-lettered order is rescued by
    /// the separate ops "requeue delivery" path, not by redeliver.)
    /// <para>
    /// delivery_unconfirmed is included: the park's entire purpose is to let a HUMAN choose to
    /// re-send, accepting the duplicate risk the automatic retry must not take on their behalf.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> RedeliverableFrom =
        Set(DeliveryFailed, ReadyToDeliver, DeliveryUnconfirmed);

    // ── The canonical delivery-claim sets (#36) ─────────────────────────────────────────────
    // DeliveryService claims an order for sending by atomically flipping it to 'delivering'; the
    // statuses that claim accepts used to be five hand-written literals (the dispatch claim's
    // relational + InMemory copies, the retry claim, the billing-hold gate, and the retry
    // endpoint's admission guard) that drifted apart four times, always silently: a status in one
    // list but not a sibling makes the claim match 0 rows, which the caller reads as "someone
    // else has it" and reports as SUCCESS having sent nothing. Every gate now derives from the
    // named sets below via ProcuLink.Core.Services.Delivery.DeliveryClaim, and the deltas
    // BETWEEN the sets — each one a product decision — are pinned exactly by
    // OrderStatusMachineTests, so widening or narrowing any of them is a deliberate edit.
    //
    // These sets hold IDLE statuses only. A STALE 'delivering' row is also claimable (crash
    // recovery), but that is status PLUS time, not set membership, so DeliveryClaim composes it
    // onto the set in the predicate rather than into it.

    /// <summary>
    /// The idle statuses <c>DispatchArtifactAsync</c>'s atomic claim accepts for an
    /// OPERATOR-DRIVEN activation (<c>requireAutoDeliver: false</c> — every such enqueue is
    /// <c>DeliverOrderJob.EnqueueRedeliver</c>: <c>OrdersController.Redeliver</c>, the park's
    /// "Send again", and <c>OpsController.RequeueDelivery</c>).
    ///
    /// <para><c>delivery_unconfirmed</c> is here and NOT in
    /// <see cref="ClaimableForAutomaticDispatchFrom"/>: the park exists so a HUMAN can choose to
    /// re-send, accepting the duplicate risk no automatic path may take on their behalf. The
    /// delta is pinned exactly by
    /// <c>OrderStatusMachineTests.OperatorAndAutomaticDispatchClaimSets_DifferExactlyBy_DeliveryUnconfirmed</c>.</para>
    ///
    /// <para><b>Invariant:</b> a superset of <see cref="RedeliverableFrom"/>. A status the
    /// controller accepts for redeliver but the claim cannot claim strands the order silently
    /// (52c6431): 0 rows claimed reads as a benign lost race, so the job logs success having
    /// sent nothing.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ClaimableForDispatchFrom =
        Set(ReadyToDeliver, DeliveryFailed, DeliveryUnconfirmed);

    /// <summary>
    /// The idle statuses the dispatch claim accepts for an AUTOMATIC activation
    /// (<c>requireAutoDeliver: true</c> — <c>TransformOrderJob</c>, the stranded-ready sweep, and
    /// any Hangfire refetch of those). Deliberately excludes <c>delivery_unconfirmed</c> (#42): a
    /// dead automatic activation is refetched ~30 min later, and if the stuck sweep parked the
    /// order in the meantime, an unconditional claim would re-open a fresh attempt and SEND the
    /// parked PO automatically — the exact duplicate the park exists to prevent. The claim
    /// predicate, not any pre-claim status read, is the enforcement (the sweep can park between a
    /// read and the claim). Pinned live on real Postgres by <c>AutomaticParkClaimPostgresTests</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> ClaimableForAutomaticDispatchFrom =
        Set(ReadyToDeliver, DeliveryFailed);

    /// <summary>
    /// <c>RetryDeliveryAsync</c>'s backoff-queue claim. Deliberately excludes
    /// <c>delivery_unconfirmed</c>: a parked order is re-driven only by a human "Send again",
    /// never by the retry queue. Equal to <see cref="ClaimableForAutomaticDispatchFrom"/> because
    /// both state the same product rule ("an automatic path never claims a park") at their two
    /// gates — the equality is asserted, so diverging them takes a new rule, not a typo.
    /// </summary>
    public static readonly IReadOnlySet<string> ClaimableForRetryFrom =
        Set(ReadyToDeliver, DeliveryFailed);

    /// <summary>
    /// <c>HoldForBillingAsync</c>'s holdable set — an idle, send-ready order that has NOT yet been
    /// claimed for this dispatch. The gate sits DOWNSTREAM of <c>DeliverOrderJob</c>'s billing
    /// check, on the path "Send again" takes whenever the org has lapsed, so it must accept every
    /// status <see cref="RedeliverableFrom"/> admits.
    /// <list type="bullet">
    /// <item><c>ready_to_deliver</c> — DeliverOrderJob's first-delivery billing gate (transform
    /// just done).</item>
    /// <item><c>delivery_failed</c> — RetryDeliveryAsync's billing gate (A5): a backoff retry for
    /// an order that previously failed, now blocked because the org lapsed.</item>
    /// <item><c>delivery_unconfirmed</c> — the same case reached from the park: an operator's
    /// "Send again" for an org that lapsed since the park, or a Hangfire-refetched automatic
    /// activation whose billing check runs BEFORE the dispatch claim that would refuse its park
    /// claim. Holding is safe for both: it pauses the nag without sending, and
    /// <c>ReleaseBillingHeldOrdersAsync</c> RESTORES a held park (<c>HeldFromStatus</c>) rather
    /// than re-driving it. Omitting the status would hold NOTHING and leave the order parked —
    /// invisible to the release sweep (it matches <c>delivery_held</c> only), so billing settling
    /// would never rescue it. Permanent strand, no self-heal (hand-fixed once in 392b5a4).</item>
    /// </list>
    /// Any other status (delivering / delivered / dead-letter / already held) is a benign no-op —
    /// the billing gate returns without holding, and never delivers.
    /// </summary>
    public static readonly IReadOnlySet<string> HoldableForBillingFrom =
        Set(ReadyToDeliver, DeliveryFailed, DeliveryUnconfirmed);

    /// <summary>
    /// <c>OrdersController.RetryDelivery</c>'s admission guard — <see cref="RedeliverableFrom"/>'s
    /// twin for the retry leg, and the one place a user-facing 400 was minted from a hardcoded
    /// status name. Correct today (a subset of <see cref="ClaimableForRetryFrom"/>, pinned), so
    /// naming it is drift PREVENTION, not a bug fix: it is the exact shape RedeliverableFrom had
    /// before it was named — and naming that one is what made 52c6431 findable.
    /// </summary>
    public static readonly IReadOnlySet<string> RetryableFrom =
        Set(DeliveryFailed);

    /// <summary>
    /// <c>OrderTransformService</c>'s atomic transform claim — the statuses it flips to
    /// <c>transforming</c>. Written TWICE at that call site (a relational <c>ExecuteUpdateAsync</c>
    /// predicate and its EF-InMemory emulation), which is the same two-copies-of-one-rule shape
    /// that made the five delivery-claim lists drift apart four times, always silently. Named here
    /// so both branches read one declaration.
    ///
    /// <list type="bullet">
    /// <item><c>ready</c> — the normal entry.</item>
    /// <item><c>transforming</c> — a Hangfire retry re-running a crashed attempt.</item>
    /// <item><c>transform_failed</c> — the recovery door after a broken template/mapping is fixed.
    ///   It holds NO artifact, so nothing stale can be re-shipped.</item>
    /// <item><c>rejected_by_supplier</c> — the SAME recovery door for the other kind of correction
    ///   (WP-19). A genuine business rejection means the supplier read the document and refused it;
    ///   the cure is a corrected document, and re-transforming is how one is produced. Its stored
    ///   artifact can never be re-shipped in place — no delivery claim set admits the status — so
    ///   admitting it here cannot duplicate a send. Without this the status had no exit at all.</item>
    /// </list>
    ///
    /// <para>Everything PAST transform (<c>ready_to_deliver</c> and beyond) must stay out: claiming
    /// one would upload a duplicate artifact and re-enqueue delivery, double-sending the same PO. A
    /// mapping edit resets such an order to <c>ready</c> first (the MV-1 edges).</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ClaimableForTransformFrom =
        Set(Ready, Transforming, TransformFailed);

    /// <summary>
    /// <c>OrdersController.MarkDelivered</c>'s admission guard — the ONLY statuses from which a
    /// human may settle an order as <c>delivered</c> without sending it. Named for the same reason
    /// as <see cref="RetryableFrom"/>: the endpoint gates on it TWICE (once against the detached
    /// read, once as a TOCTOU re-check against the tracked row), and two hand-written literals of
    /// the same rule is how the four claim lists drifted apart four times.
    ///
    /// <para>It is also the load-bearing half of
    /// <c>OrderStatusMachineTests.EveryInboundDeliveredEdge_HasAProductionWriter</c>: together with
    /// the delivery claim's single automatic writer (<c>delivering</c> — <c>DeliveryService</c>
    /// resyncs the claimed row's tracked value AND <c>OriginalValue</c> to <c>delivering</c> before
    /// <c>PersistAttemptAsync</c> writes the outcome), this set IS the complete inbound edge list
    /// for <c>delivered</c>. Widening it here without widening the endpoint — or vice versa — is
    /// what that test catches.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ManuallyDeliverableFrom =
        Set(DeliveryUnconfirmed);

    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> Set(params string[] s) =>
        new HashSet<string>(s, StringComparer.Ordinal);
}
