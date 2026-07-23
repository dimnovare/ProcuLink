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
            // ready_to_deliver → delivered/rejected_by_supplier: a supplier status webhook. LEGITIMATE
            // ONLY for an order that was already dispatched (MV-1 reset it to ready, the next Send
            // re-transformed it back here, and a late ACK for the ORIGINAL dispatch lands while it sits
            // in this state). Most orders resting here were never sent at all, so the webhook gates this
            // edge on dispatch evidence, not on the status — see WebhookReportableFrom.
            [ReadyToDeliver]     = Set(Delivering, Delivered, DeliveryFailed, DeliveryHeld, Ready, RejectedBySupplier),
            // Billing hold → released back to ready_to_deliver when the org returns to good standing.
            // delivery_held → delivery_unconfirmed: the release RESTORES a held PARK instead of
            // re-driving it — HoldForBillingAsync records the origin (HeldFromStatus) and
            // ReleaseBillingHeldOrdersAsync branches on it, because an automatic re-send of an
            // unknown-outcome PO on a channel that cannot de-duplicate is the duplicate the park
            // exists to prevent.
            // delivery_held → delivered/rejected_by_supplier: a late supplier ACK for an order sent
            // before the hold landed (delivery_failed → delivery_held is real, A5). A hold placed by the
            // PRE-CLAIM billing gate has NO dispatch behind it, so this edge too is gated on dispatch
            // evidence rather than on the status — see WebhookReportableFrom.
            [DeliveryHeld]       = Set(ReadyToDeliver, Delivered, DeliveryUnconfirmed, Ready, RejectedBySupplier),
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
            // delivery_failed/delivery_dead_letter → delivered: a late positive ACK from the supplier
            // status webhook. Both are gated by WebhookReportableFrom + dispatch evidence.
            [DeliveryFailed]     = Set(Delivering, Delivered, DeliveryDeadLetter, DeliveryHeld, Ready, RejectedBySupplier),
            // Unknown-outcome park. The operator decides: send again (→ delivering) or confirm the
            // supplier got it (→ delivered). A mapping edit invalidates the artifact (→ ready, the
            // MV-1 sibling). Dead-letter/failed remain reachable if a later re-send exhausts retries.
            // → delivery_held: the delivery_failed sibling — a "Send again" for an org that lapsed
            // since the park is held (not delivered) via HoldForBillingAsync.
            // → delivered/rejected_by_supplier also arrive via the supplier status webhook
            // (2026-07-23): a terminal callback answers exactly the question the park is waiting
            // on — did the PO arrive? — with the one thing the park lacks, the supplier's own
            // statement. The park always satisfied the guard's dispatch-evidence half (the park
            // finalises the in-flight row it re-adopted, and that row keeps the IdempotencyKey the
            // pre-send commit stamped), so admitting the status into WebhookReportableFrom was the
            // only missing half. See that set's doc for the billable-status trade.
            [DeliveryUnconfirmed] = Set(Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, DeliveryHeld, Ready, RejectedBySupplier),
            // dead_letter → delivery_failed: an ops requeue that fails again. (NOT a webhook: a supplier
            // status callback writes delivered or rejected_by_supplier only — never delivery_failed.)
            [DeliveryDeadLetter] = Set(Delivering, Delivered, DeliveryFailed, Ready, RejectedBySupplier),
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
    /// A status with no outgoing transitions. <c>delivered</c> is NOT terminal: a supplier status
    /// callback can still reject it (HTTP 200 is transport success, not business acceptance), and an
    /// MV-1 mapping edit resets it to <c>ready</c> for re-transform. (It can NOT be webhook-flipped to
    /// delivery_failed — that callback writes rejected_by_supplier. The delivered → delivery_failed
    /// entry survives for DeliveryService's pre-claim failure paths, which write delivery_failed with
    /// no status check.)
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
    /// A supplier status callback (<c>POST /api/webhook-ingress/{slug}/status</c>) may report a
    /// terminal outcome — <see cref="OrderStatusConstants.Delivered"/> or
    /// <see cref="OrderStatusConstants.RejectedBySupplier"/> — ONLY for an order that was
    /// genuinely dispatched. This set is the STATUS half of that guard.
    ///
    /// <para><b>Read the invariant precisely: membership means an order in this state MAY have been
    /// dispatched — NOT that it was.</b> The set is a PROXY for "this order has a DeliveryAttempt
    /// row", and the proxy is UNSOUND for exactly two members:</para>
    /// <list type="bullet">
    ///   <item><c>ready_to_deliver</c> is where EVERY transformed order rests BEFORE it is ever
    ///     sent — <c>AutoDeliver</c> defaults false, so the ordinary path in is
    ///     transform → wait for a human "Send" (<c>StrandedReadyOrderDetectionService</c> exists
    ///     solely because orders sit here un-sent). It is ALSO reachable post-dispatch, via MV-1:
    ///     a mapping edit resets a delivered/delivery_failed order to ready and the next Send
    ///     re-transforms it back here, where a late ACK for the ORIGINAL dispatch can land. Both
    ///     paths are real; the status cannot tell them apart.</item>
    ///   <item><c>delivery_held</c> is reachable AFTER an attempt (A5: <c>delivery_failed →
    ///     delivery_held</c> — refusing that order's late ACK would make the reactivation re-drive
    ///     send it a SECOND time) and BEFORE any attempt, via the PRE-CLAIM billing gate, which
    ///     moves a still-idle <c>ready_to_deliver</c> order straight here — "never a delivery"
    ///     (<c>DeliveryService.cs:822-825</c>).</item>
    /// </list>
    ///
    /// <para><b>Because the proxy is unsound, this set is NOT sufficient on its own.</b>
    /// <c>WebhookIngressController</c> pairs it with dispatch EVIDENCE and re-verifies BOTH inside
    /// its atomic claim. The evidence is NOT "a <c>DeliveryAttempt</c> row exists" — four
    /// pre-dispatch gates write an order-linked row with nothing sent — but a per-row marker only
    /// the dispatch sequence writes (<c>IdempotencyKey != null OR ArtifactSha256 != null</c>); see
    /// that controller for the full derivation. Do not "simplify" the controller down to this set
    /// alone: that would let a callback mark a never-dispatched order delivered AND disable its
    /// safety net (the stranded-ready sweep matches <c>Status == ready_to_deliver</c>; the billing
    /// release matches <c>Status == delivery_held</c> — overwrite either and the order is
    /// permanently lost, displayed as shipped, and billable).</para>
    ///
    /// <para><c>delivery_unconfirmed</c> (added 2026-07-23) is the opposite case: for this member
    /// the proxy is SOUND on its own. Its only writer is <c>DeliveryService.ParkUnconfirmedAsync</c>,
    /// which finalises the re-adopted in-flight row to <c>unconfirmed</c> without touching the
    /// <c>IdempotencyKey</c> the pre-send commit stamped (<c>OpenDispatchAttemptAsync</c>) — so a
    /// parked order always carries a marker row and the evidence half passes for every real park.
    /// It is admitted because a terminal callback answers exactly the question the park is waiting
    /// on — did the PO arrive? — with positive supplier evidence no operator guess can match;
    /// refusing it left even http-channel parks to be resolved by out-of-band guesswork. This
    /// deliberately lets an authenticated webhook move an order into a BILLABLE status; that trade
    /// was made in the 2026-07-23 open-queue handover (item 3), not silently here.</para>
    ///
    /// <para><c>rejected_by_supplier</c> is deliberately ABSENT: a supplier that rejected must not
    /// silently flip the order to delivered, because a human has likely already acted on the
    /// rejection. A genuine retraction is an operator re-drive, not an automatic write. A REPEATED
    /// rejection callback is still a 200 — the endpoint short-circuits when the reported status
    /// already matches the order's status.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> WebhookReportableFrom =
        Set(ReadyToDeliver, Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, DeliveryHeld,
            DeliveryUnconfirmed);

    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> Set(params string[] s) =>
        new HashSet<string>(s, StringComparer.Ordinal);
}
