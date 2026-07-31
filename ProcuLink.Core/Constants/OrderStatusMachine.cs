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
    // ── The resolve recompute ("+ RR" below) ─────────────────────────────────────────────────
    // OrderResolutionService.ResolveAsync and AcceptAiSuggestionsAsync both END with the same
    // unconditional write:
    //     entity.Status = entity.Lines.Any(l => l.NeedsReview) ? "pending_review" : "ready";
    // (OrderResolutionService.cs:242 and :393). Neither carries a from-status guard, and neither
    // endpoint adds one — OrdersController.Resolve (:483) validates the REQUEST BODY only, and
    // AcceptAiSuggestions (:1737) validates nothing at all. The single guard on the path is
    // OrderResolutionService.IsFinished, which refuses exactly DeclaredTerminal = {failed}.
    //
    // The reachable from-states for those two writers are therefore EVERY status except 'failed'
    // and 'pending_parse' — the latter because nothing writes it: all four PurchaseOrderEntity
    // construction sites override the entity's C# default (OrderIngestionService.cs:216 / :350 /
    // :523 and SampleOrderService.cs:123), so no order is ever IN pending_parse to be resolved.
    // Every other status owes BOTH → pending_review and → ready, and the rows below say so.
    //
    // → ready needs no argument beyond the write itself. → pending_review is the half that reads
    // impossible from a post-review status and is not: ResolveAsync re-runs the connection-level
    // price-variance guard on EVERY resolve (:206-239) and re-sets line.NeedsReview when a unit
    // price diverges from the catalog, so a resolve on a settled order legitimately reopens the
    // review loop. A partially-resolved order does the same thing without any guard: lines the
    // caller did not name keep NeedsReview.
    //
    // WP-19 reconciled this writer for ONE from-state (rejected_by_supplier), because that status
    // was a dead end and the edge was the way out. The WRITER is the same for every other
    // from-state, so the rest were left calling a live production write impossible — which is what
    // made the observer log "Unexpected order status transition" on legitimate operator actions,
    // the false positive its own doc says trains operators to ignore it. Reconciled here rather
    // than gated at the endpoint: the write is real and the maps were the things that were wrong.
    // EveryStatusAResolveCanBeIssuedFrom_HasBothRecomputeEdges now DERIVES the list from
    // AllStatuses instead of enumerating it, so a status added tomorrow cannot repeat this.

    /// <summary>from-status → the set of statuses the flow can move it to.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Transitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [PendingParse]       = Set(Parsing, Unrouted, RejectedBySupplier),
            [Parsing]            = Set(PendingReview, Ready, Failed, PendingParse, Unrouted, RejectedBySupplier),
            // Routing hold: parked awaiting a supplier; assigning one re-enqueues parse.
            // + RR. Reachable and consequential: an unrouted order HAS lines
            // (OrderIngestionService.cs:523 builds the full graph and only overrides the status),
            // so a line resolve — or a header-only one, which the endpoint accepts — recomputes it
            // straight to ready/pending_review with SupplierId still null. That is a real hole, not
            // just a map entry: OrdersController.AssignSupplier claims atomically on
            // `Status == Unrouted` (:682) and answers 409 otherwise, so the recompute LOCKS the
            // operator out of the one action the routing hold exists for. Named as a follow-up
            // rather than papered over here — closing it is an endpoint guard, a product decision
            // about what a resolve may do to an unrouted order, not a transition-map edit.
            [Unrouted]           = Set(Parsing, PendingParse, Failed, RejectedBySupplier, PendingReview, Ready),
            [PendingReview]      = Set(Ready, PendingReview, RejectedBySupplier),
            [Ready]              = Set(Transforming, PendingReview, RejectedBySupplier),
            // transforming → transform_failed: a TERMINAL transform failure (a template that would not
            // render, or an output mapping that could not be applied) — neither is fixable by retrying
            // the same inputs, so the order is parked visibly rather than reverted to 'ready'.
            // + RR (→ ready was already here as the transform-revert edge; → pending_review is new).
            [Transforming]       = Set(ReadyToDeliver, Ready, TransformFailed, Failed, RejectedBySupplier, PendingReview),
            // ready_to_deliver/delivered → ready: a mapping edit after transform (MV-1) invalidates
            // the artifact and resets the order so the next Send re-transforms.
            // ready_to_deliver → delivery_held: a billing flip pauses (not fails) delivery. This fires
            // BEFORE the 'delivering' claim, so it moves a still-idle, NEVER-dispatched order.
            // → delivered is GONE (WP-09). It was listed for the retired inbound supplier-status
            // webhook, and no other writer can perform it: OrdersController.MarkDelivered is gated on
            // ManuallyDeliverableFrom (delivery_unconfirmed only, checked twice), and DeliveryService
            // only writes 'delivered' AFTER its claim has moved the row to 'delivering'. Pinned by
            // OrderStatusMachineTests.EveryInboundDeliveredEdge_HasAProductionWriter.
            // + RR (→ ready was already here as the MV-1 edge; → pending_review is new).
            [ReadyToDeliver]     = Set(Delivering, DeliveryFailed, DeliveryHeld, Ready, RejectedBySupplier, PendingReview),
            // Billing hold → released back to ready_to_deliver when the org returns to good standing.
            // delivery_held → delivery_unconfirmed: the release RESTORES a held PARK instead of
            // re-driving it — HoldForBillingAsync records the origin (HeldFromStatus) and
            // ReleaseBillingHeldOrdersAsync branches on it, because an automatic re-send of an
            // unknown-outcome PO on a channel that cannot de-duplicate is the duplicate the park
            // exists to prevent.
            // → delivered is GONE (WP-09) — same reason as ready_to_deliver above. A held order is
            // settled by RELEASING it (→ ready_to_deliver, or → delivery_unconfirmed for a held
            // park) and settling it from there; it cannot be marked delivered in place.
            // + RR (→ pending_review). A billing hold pauses DELIVERY; it does not close the order
            // to correction, and nothing on the resolve path consults billing state.
            [DeliveryHeld]       = Set(ReadyToDeliver, DeliveryUnconfirmed, Ready, RejectedBySupplier, PendingReview),
            // delivering → delivery_unconfirmed: the park — a crash-recovery re-drive on a channel
            // that cannot de-duplicate stops rather than risk a duplicate PO.
            // delivering → delivery_dead_letter: StuckDeliveryDetectionService dead-letters an order
            // that kept stranding in 'delivering' after its re-drive budget was spent.
            // + RR (BOTH halves are new here). 'delivering' is not a moment, it is a status a row
            // SITS in — StuckDeliveryDetectionService exists precisely because dispatches strand
            // there — and the resolve endpoint has no guard, so an operator resolving a stranded
            // order writes ready/pending_review straight over the claim. Listed because it is real,
            // not because it is good: the dispatch may still be in flight, and its outcome write
            // lands on whatever the resolve left behind. Same follow-up as the unrouted row — the
            // cure is a from-status guard on the endpoint, a product decision, not a map edit.
            [Delivering]         = Set(Delivered, DeliveryFailed, DeliveryUnconfirmed, DeliveryDeadLetter, RejectedBySupplier, PendingReview, Ready),
            // + RR (→ ready was already here as the MV-1 edge; → pending_review is new — and it is
            // the edge that started this packet: an operator resolves a delivered order, the
            // price-variance guard re-flags a line, and both maps called the result impossible).
            [Delivered]          = Set(DeliveryFailed, Ready, RejectedBySupplier, PendingReview),
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
            // + RR (→ pending_review). The OBSERVER already listed this one, so the drift was
            // exempted rather than fixed — see the deleted KnownObserverOnlyEdges entry.
            [DeliveryFailed]     = Set(Delivering, DeliveryDeadLetter, DeliveryHeld, Ready, RejectedBySupplier, PendingReview),
            // Unknown-outcome park. The operator decides: send again (→ delivering) or confirm the
            // supplier got it (→ delivered). A mapping edit invalidates the artifact (→ ready, the
            // MV-1 sibling). Dead-letter/failed remain reachable if a later re-send exhausts retries.
            // → delivery_held: the delivery_failed sibling — a "Send again" for an org that lapsed
            // since the park is held (not delivered) via HoldForBillingAsync.
            // → delivered/rejected_by_supplier: the operator answers the question the park is waiting
            // on — did the PO arrive? — after checking with the supplier out-of-band. The park keeps
            // the IdempotencyKey the pre-send commit stamped (ParkUnconfirmedAsync finalises the
            // re-adopted in-flight row), so the evidence that a send BEGAN survives for that call.
            // + RR (→ pending_review).
            [DeliveryUnconfirmed] = Set(Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, DeliveryHeld, Ready, RejectedBySupplier, PendingReview),
            // dead_letter → delivery_failed: an ops requeue that fails again.
            // → delivered is GONE (WP-09) — same reason as delivery_failed above.
            // + RR (→ ready was already here as the MV-1 edge; → pending_review is new).
            [DeliveryDeadLetter] = Set(Delivering, DeliveryFailed, Ready, RejectedBySupplier, PendingReview),
            // rejected_by_supplier is a CORRECTION LOOP, not an ending (WP-19). It used to have no
            // successors at all, and DeliveryService routed every 400–499 into it — so an expired
            // API key, a moved endpoint or a rate limit parked a deliverable PO where nothing in
            // the product could move it. The classification split fixed WHICH orders arrive here;
            // these edges fix what an operator can do once one has.
            //
            // The exits are the two ways to produce a CORRECTED document. Re-sending the refused
            // bytes is deliberately NOT one of them — the supplier read them and said no — so no
            // delivery claim set admits this status and → delivering stays impossible.
            //   → pending_review / ready: the resolve recompute (+ RR — see the block above the map;
            //     these two rows are where it was first reconciled, one from-state at a time,
            //     before the general case was). OrderResolutionService.ResolveAsync recomputes the
            //     status from the lines with NO from-status guard, so correcting the line or header
            //     the supplier named already moves the order out. That writer PREDATES this packet:
            //     the edge was live in production while this map called the status terminal, which
            //     is part of how the dead end read as intentional.
            //   → transforming: the transform claim admits rejected_by_supplier
            //     (ClaimableForTransformFrom), exactly as it admits transform_failed — the order
            //     holds no artifact anyone may ship, and re-transforming with the corrected mapping
            //     is the cure.
            [RejectedBySupplier] = Set(PendingReview, Ready, Transforming),
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
    ///
    /// <para><b>This set is ENFORCED, not merely asserted.</b> Those two refusals were the whole
    /// justification for a while, and they left a third writer out: <c>OrderResolutionService</c>
    /// recomputed the status from the lines with no from-status check on every one of its paths, and
    /// a failed source file leaves NO lines — so <c>Lines.Any(NeedsReview)</c> over an empty
    /// collection landed on <c>ready</c>, and a header-only
    /// <c>POST /api/orders/{id}/resolve {"poNumber":"X"}</c> walked a failed order back into the
    /// pipeline. Marking one rejected was worse: <c>rejected_by_supplier</c> has exits (WP-19), so it
    /// laundered the order past any guard on resolve alone. All three writers now derive their
    /// refusal from THIS set (<c>OrderResolutionService.IsFinished</c>), which is what makes the
    /// declaration true rather than aspirational — and what stops the next writer from quietly
    /// contradicting it.</para>
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
    ///
    /// <para><b>Do not use this to decide whether a status is ALLOWED to have no exits.</b> It is
    /// derived from the map, so that reasoning is circular — see
    /// <see cref="DeclaredTerminal"/>, which states the product's answer independently and is what
    /// the dead-end invariant checks against.</para>
    /// </summary>
    public static bool IsTerminal(string status) => NextStatuses(status).Count == 0;

    /// <summary>A status the UI renders as the red "Failed" pill (delegates to the canonical bucket).</summary>
    public static bool IsFailure(string status) => FailureBucket.Contains(status);

    // ── Operation-specific entry guards (centralised; match the prior literals) ──

    /// <summary>
    /// WP-23 — the from-statuses an OPERATOR-ISSUED resolve is REFUSED from, with a 409.
    ///
    /// <para><b>This is an ENDPOINT ADMISSION RULE, not a transition rule, and the distinction is
    /// load-bearing.</b> <see cref="Transitions"/> answers "may an order in status X ever end up in
    /// status Y?" — a fact about the machine. This set answers a different question: "may a person
    /// ISSUE a correction while the order is in status X?" — a product decision about a control in
    /// the UI. The two are independent, and c61fe30 settled the first one deliberately: it added
    /// <c>unrouted → ready|pending_review</c> and <c>delivering → ready|pending_review</c> to BOTH
    /// status maps because the write was real and the maps were wrong. <b>Those edges stay.</b>
    /// Refusing the endpoint does not make the transition impossible — it makes the endpoint stop
    /// performing it — and pruning the edges on the strength of this set would re-create exactly the
    /// circular reasoning <see cref="DeclaredTerminal"/> exists to break. Pinned by
    /// <c>OrderStatusMachineTests.EveryResolveHeldStatus_KeepsBothRecomputeEdges</c>, which fails if
    /// anyone prunes them.</para>
    ///
    /// <para><b>Why these two, and only these two.</b> The resolve recompute
    /// (<c>OrderResolutionService.ResolveAsync</c> :242 and <c>AcceptAiSuggestionsAsync</c> :393)
    /// ends with an unconditional <c>Status = Lines.Any(NeedsReview) ? pending_review : ready</c>.
    /// From most from-states that is a correction landing where a correction should land. From these
    /// two it destroys something:</para>
    /// <list type="bullet">
    /// <item><c>unrouted</c> — the routing hold. An unrouted order HAS lines
    /// (<c>OrderIngestionService</c> builds the full graph and only overrides the status), so a line
    /// resolve — or a header-only one, which the endpoint accepts — recomputes it to ready with
    /// <c>SupplierId</c> still null. <c>OrdersController.AssignSupplier</c> then claims atomically on
    /// <c>Status == Unrouted</c> (:682) and answers 409 otherwise, so the recompute locks the
    /// operator out of the ONE action the routing hold exists for, permanently. The order becomes
    /// unassignable by any control in the product.</item>
    /// <item><c>delivering</c> — a live dispatch claim. <c>delivering</c> is not a moment, it is a
    /// status a row SITS in (<c>StuckDeliveryDetectionService</c> exists precisely because
    /// dispatches strand there), so a resolve writes straight over the claim while the send may
    /// still be in flight — and the dispatch's own outcome write then lands on whatever the resolve
    /// left behind.</item>
    /// </list>
    ///
    /// <para><b>Deliberately NOT here: <c>failed</c>.</b> It is already refused, one layer down and
    /// for a different reason — <c>OrderResolutionService.IsFinished</c> derives from
    /// <see cref="DeclaredTerminal"/> and answers 400 with "there is nothing here to correct; upload
    /// a corrected file as a new order". That is a permanent verdict about the order; this set is a
    /// temporary one about the moment ("do X first, then correct it"), which is why it earns a
    /// different status code and a different sentence. The two sets are asserted DISJOINT so the two
    /// refusals cannot quietly merge.</para>
    ///
    /// <para>Consumed by <c>OrdersController.RefusedByResolveHoldAsync</c>, which gates BOTH
    /// recompute endpoints — <c>POST /api/orders/{id}/resolve</c> and
    /// <c>POST /api/orders/{id}/accept-ai-suggestions</c>. Gating only the first would leave both
    /// holes open through the second, which shares the writer and validates nothing.</para>
    /// </summary>
    // MUTATION-CHECK C (WP-23): the canonical set is REPLACED here on purpose — the two real holes
    // are dropped and a status that belongs to DeclaredTerminal is put in their place. Expected RED:
    // all nine controller rows, the two 'failed' positive controls (failed would now 409), plus
    // ResolveHeldFrom_IsExactlyTheTwoHolesTheRecomputeDestroys,
    // ResolveHeldFrom_AndDeclaredTerminal_AreDisjoint and
    // EveryResolveHeldStatus_KeepsBothRecomputeEdges. Not for merge.
    public static readonly IReadOnlySet<string> ResolveHeldFrom =
        Set(Failed);

    /// <summary>
    /// The sentence an operator reads when <see cref="ResolveHeldFrom"/> refuses their correction.
    ///
    /// <para>Every one names what to DO — a status-code echo ("Order must be in one of these
    /// statuses: …") tells an operator what the server thinks and nothing about their next move.
    /// No raw status token appears in any of them, and no party noun: buyers first, inbound stays
    /// supported, so a sentence that says "supplier" is wrong on half the installs. Pinned by
    /// <c>OrdersControllerResolveStatusGuardTests</c>, which derives its rows from
    /// <see cref="ResolveHeldFrom"/> — so a status added to that set tomorrow fails the suite until
    /// it is given its own sentence here, rather than silently shipping the fallback.</para>
    /// </summary>
    public static string ResolveHoldMessage(string status) => status switch
    {
        Unrouted =>
            "This order is still waiting to be routed. Route it first, then make your corrections — "
          + "correcting it now would take it out of the routing queue while it still has nowhere to go.",
        Delivering =>
            "This order is being sent right now, so it cannot be corrected mid-send. "
          + "Wait for the send to finish, then correct it and send it again.",
        _ =>
            "This order cannot be corrected while the step it is in is still running. "
          + "Wait for that step to finish, then try your correction again.",
    };

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
        Set(Ready, Transforming, TransformFailed, RejectedBySupplier);

    /// <summary>
    /// <c>OrdersController.Transform</c>'s admission guard — the statuses the ENDPOINT claims, and
    /// the <see cref="RetryableFrom"/> analogue for the transform leg. Its own comment already
    /// described itself as "kept in lockstep with the transform claim in OrderTransformService",
    /// which is the sentence a hand-written literal writes just before it drifts.
    ///
    /// <para>It differs from <see cref="ClaimableForTransformFrom"/> by exactly
    /// <c>transforming</c>, and that delta is a product decision, not an oversight: the SERVICE
    /// claim admits <c>transforming</c> so a Hangfire retry can re-run a crashed attempt, while the
    /// endpoint answers a second click with 202 "already in progress" instead of starting a
    /// competing job. Pinned by
    /// <c>OrderStatusMachineTests.TransformEndpointAndServiceClaims_DifferExactlyBy_Transforming</c>.</para>
    ///
    /// <para><b>Invariant:</b> a subset of <see cref="ClaimableForTransformFrom"/>. A status the
    /// endpoint claims but the service claim refuses is the 52c6431 shape on the transform leg —
    /// the endpoint commits <c>transforming</c> and enqueues, the job's claim matches 0 rows and
    /// reports a benign skip, and the order sits in <c>transforming</c> with no artifact.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> TransformableFrom =
        Set(Ready, TransformFailed, RejectedBySupplier);

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
