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
            // pending_parse → failed: the stuck-order sweep's dead-letter. Nothing WRITES
            // pending_parse today — it is the entity's C# default and all four construction sites
            // override it — but StuckOrderDetectionService now SWEEPS the status, precisely so a
            // future ingest path that forgets one of those assignments cannot leave an order
            // permanently invisible. That sweep re-drives a pending_parse row through 'parsing'
            // (the edge already listed here) and, past the requeue budget, dead-letters it to
            // 'failed' exactly as it does a 'parsing' strand. The edge is therefore real the
            // moment the leak it guards against happens, which is the only moment it matters.
            [PendingParse]       = Set(Parsing, Unrouted, RejectedBySupplier, Failed),
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
    /// <para><b>Why these four.</b> The resolve recompute
    /// (<c>OrderResolutionService.ResolveAsync</c> :242 and <c>AcceptAiSuggestionsAsync</c> :393)
    /// ends with an unconditional <c>Status = Lines.Any(NeedsReview) ? pending_review : ready</c>.
    /// From most from-states that is a correction landing where a correction should land. From these
    /// four it destroys something:</para>
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
    /// <item><c>parsing</c> (WP-23a) — a step whose completion is CLAIM-GUARDED, so the correction
    /// does not merely race the parse, it cancels it. Both parse-persist claims require
    /// <c>Status == Parsing</c> — <c>ParseStoredFileAsync</c>'s and the file-less sibling in
    /// <c>ResolvePersistedLinesAsync</c> — and, on 0 rows, return <c>Fail</c> BEFORE the lines are
    /// inserted. (<c>OrderIngestionService.cs:1188</c> / <c>:1479</c> for the two claims and
    /// <c>:1238</c> / <c>:1490</c> for the two <c>Fail</c>s, on <c>main</c> @ <c>c8ae076</c>. They were
    /// <c>:1167</c>/<c>:1458</c>/<c>:1217</c>/<c>:1469</c> when this packet was written — #97 shifted
    /// the file by 21 lines, which is why the METHOD names lead and the numbers carry a SHA.) Three
    /// things make it worse than a discarded parse rather than milder:
    /// <br/>(a) <b>it is silent.</b> The <c>Fail</c> makes <c>ParseOrderJob</c> throw (<c>:55</c>),
    /// Hangfire retries, the retry hits <c>ParseStoredFileAsync</c>'s own re-entry guard
    /// (<c>if (entity.Status != "parsing")</c>, <c>:843-844</c> on the same SHA)
    /// which returns <c>Ok</c> for an order that has left <c>parsing</c>, and <c>ParseOrderJob</c>'s
    /// terminal-failure re-throw (<c>:67</c>) fires only for <c>failed</c> — so the job lands GREEN.
    /// <c>StuckOrderDetectionService</c> cannot see the order either: it is no longer in a transient
    /// status. Nothing anywhere reports the loss.
    /// <br/>(b) <b>on a first parse there are no lines yet.</b> The stub is created with none
    /// (<c>OrderIngestionService.cs:341</c>), so the recompute writes <c>ready</c> over an empty line
    /// set, and <c>OrdersController.Transform</c>'s <c>Lines.Count == 0</c> guard (<c>:1599</c>) then
    /// refuses the order with "Order is still parsing" — a dead end whose only error message is
    /// untrue and names no way out.
    /// <br/>(c) <b>it re-opens the <c>unrouted</c> hole this set exists to close.</b>
    /// <c>AssignSupplier</c>'s claim flips <c>unrouted → parsing</c> and re-enqueues the parse
    /// (<c>OrdersController.cs:747-756</c>), so the whole re-parse is a window in which the routing
    /// correction is destroyable: the resolve recomputes the order to ready/pending_review, the
    /// re-parse is discarded, and <c>AssignSupplier</c>'s <c>Status == Unrouted</c> claim answers 409
    /// forever. That is the first bullet's permanent lockout, reached one step later — so refusing
    /// <c>unrouted</c> alone shut the door and left the window.</item>
    /// <item><c>transforming</c> (WP-23a) — the same shape, and the only one of the four where the
    /// corrupted result LEAVES THE BUILDING. The transform CLAIM is atomic
    /// (<c>OrderTransformService.cs:361-368</c>, keyed on <see cref="ClaimableForTransformFrom"/>)
    /// but its completion write is not: <c>:733</c> assigns <c>Status = ready_to_deliver</c> on the
    /// tracked entity, and <c>PurchaseOrderEntity</c> carries no concurrency token, so the UPDATE
    /// goes out with no status predicate and lands over the correction last-writer-wins. The
    /// artifact committed in that same <c>SaveChanges</c> was built from the graph loaded BEFORE the
    /// correction, and <c>TransformOrderJob.cs:120</c> enqueues delivery immediately after — so with
    /// <c>AutoDeliver</c> on, the stale document is what the counterparty receives while the UI shows
    /// the corrected lines. It also defeats a HOLD rather than merely losing work: a correction that
    /// leaves a line needing review writes <c>pending_review</c>, an explicit do-not-deliver, and the
    /// completion write overwrites that too. (The <c>OriginalValue</c> forcing at <c>:408-414</c> is
    /// the same assumption stated from the other side: it exists so the failure path's revert to
    /// <c>ready</c> is not diffed away as a no-op, which is only sound while nothing else writes the
    /// status during the window.)</item>
    /// </list>
    ///
    /// <para><b>Why the two WP-23a statuses are the same case as <c>delivering</c>, not a weaker
    /// one.</b> The <c>delivering</c> bullet's argument is that the status is not a moment but a
    /// status a row SITS in, evidenced by <c>StuckDeliveryDetectionService</c> existing because
    /// dispatches strand there. <c>StuckOrderDetectionService.TransientStatuses</c> is
    /// <c>{parsing, transforming}</c> and exists for exactly that reason — the same argument, applied
    /// verbatim, to the two statuses WP-23 skipped. The sweep also BOUNDS what the refusal costs: it
    /// requeues a stalled order (a fresh parse job for <c>parsing</c>; a reset to <c>ready</c> for
    /// <c>transforming</c>) up to <c>MaxRequeues</c> and then fails it, so "wait for that step to
    /// finish, then try again" is a true instruction rather than an indefinite one.</para>
    ///
    /// <para><b>What the operator loses: nothing they can use.</b> This set removes a control, which
    /// is why WP-23 would not widen it without a decision. Priced out, the control is empty: a first
    /// parse has no lines to correct yet, and the whole recovery path survives the wait — after the
    /// transform lands, a correction IS accepted from <c>ready_to_deliver</c>, the recompute moves the
    /// order to <c>ready</c>, and <see cref="ClaimableForTransformFrom"/> admits <c>ready</c>, so
    /// correct-then-re-send is intact. What the operator gains is that the correction they issue is
    /// either performed or refused out loud, instead of being accepted and then discarded (parsing) or
    /// overwritten (transforming).</para>
    ///
    /// <para><b>UI reachability splits the two, and not the way WP-23's note implied.</b> Verified in
    /// the frontend repo on <c>main</c> @ <c>831fad1</c>, 2026-07-31, rather than relayed:
    /// <c>OrderWorkshop.tsx:605</c> (<c>src/components/bridge/workshop/</c>) early-returns
    /// <c>&lt;ParsingGate/&gt;</c> for <c>order.status === "parsing"</c> and polls every 3s, so the
    /// parsing hole is API-reachable only — which is what made it lower-priority for WP-23.
    /// <b>No such early return exists for <c>transforming</c>.</b> The status appears in that file
    /// twice, both times cosmetic (the stepper stage at <c>:506</c> and a button label at
    /// <c>:721</c>), so the workshop renders its correction panels — including the bulk
    /// <c>/accept-ai-suggestions</c> button — while the transform runs. "Send it, then fix a line
    /// while it is still going" is therefore an ordinary CLICK path, not a path only an API client can
    /// take, which makes <c>transforming</c> the higher-priority of the two despite being the one
    /// named as the weaker candidate.</para>
    ///
    /// <para><b>Deliberately NOT here: <c>failed</c>.</b> It is already refused, one layer down and
    /// for a different reason — <c>OrderResolutionService.IsFinished</c> derives from
    /// <see cref="DeclaredTerminal"/> and answers "there is nothing here to correct; upload a
    /// corrected file as a new order". That is a permanent verdict about the order; this set is a
    /// temporary one about the moment ("do X first, then correct it"), which is why it earns a
    /// different status code and a different sentence. The two sets are asserted DISJOINT so the two
    /// refusals cannot quietly merge.
    /// <br/>Precise about what the operator actually receives, because the two endpoints differ and
    /// it would be easy to write "400" here and be half wrong: <c>/resolve</c> maps that failure to
    /// <b>400 + the sentence</b> (<c>OrdersController.Resolve</c>'s <c>BadRequest(new { error })</c>),
    /// while <c>/accept-ai-suggestions</c> collapses EVERY service failure to a bare
    /// <b>404 with no body</b> (<c>if (!result.IsSuccess) return NotFound();</c>). That asymmetry
    /// predates this set and is not fixed here — WP-23 gated the two endpoints identically for the
    /// statuses below, and deliberately did not re-open how they report the terminal refusal.</para>
    ///
    /// <para><b>Still a DECISION, not a proof of exhaustiveness.</b> WP-23 shipped
    /// <c>{unrouted, delivering}</c> — the two holes <c>c61fe30</c> named — and recorded
    /// <c>parsing</c> here as an evidenced third one left open on purpose, because closing it removes
    /// a control and that is a product decision WP-23 was not given, with <c>transforming</c> named
    /// as an un-ruled-out fourth. WP-23a is that decision, and both are in on the evidence above.
    /// What is still NOT claimed is that this enumerates every destructive from-state: these four are
    /// the four with call-site evidence, which is why
    /// <c>ResolveHeldFrom_IsExactlyTheStatusesThisGuardRefuses</c> asserts membership of a decision
    /// rather than a survey of the world. A fifth arrives the way these two did — name the writer,
    /// name the line, say what it destroys — and never by a refactor's side effect.</para>
    ///
    /// <para>Consumed by <c>OrdersController.RefusedByResolveHoldAsync</c>, which gates BOTH
    /// recompute endpoints — <c>POST /api/orders/{id}/resolve</c> and
    /// <c>POST /api/orders/{id}/accept-ai-suggestions</c>. Gating only the first would leave both
    /// holes open through the second, which shares the writer and validates nothing.</para>
    ///
    /// <para><b>Refused, not made impossible — narrowed rather than closed for three of the
    /// four.</b> The guard is a check-then-act read at the endpoint, and <c>PurchaseOrderEntity</c>
    /// carries no concurrency token, so an order that ENTERS a held status between this read and
    /// <c>OrderResolutionService</c>'s <c>SaveChangesAsync</c> is still overwritten
    /// last-writer-wins. Per status, by who performs that entry write:
    /// <list type="bullet">
    /// <item><c>delivering</c> — <c>DeliveryService</c>'s dispatch claim. Real race.</item>
    /// <item><c>transforming</c> — <c>OrderTransformService</c>'s transform claim, from <c>ready</c>,
    /// which is a status this guard admits. Real race, and the widest of the three: the ordinary
    /// "correct it, then send it" sequence is exactly what puts a resolve next to a transform
    /// claim.</item>
    /// <item><c>parsing</c> — reachable via a read that saw <c>pending_parse</c> and passed (ingest's
    /// <c>ParseOrderJob</c> then claims it). Narrow, and unreachable through <c>unrouted</c>: a read
    /// that sees the routing hold is refused before <c>AssignSupplier</c> can flip it. The stuck
    /// sweep's requeue keeps an order IN <c>parsing</c> rather than entering it, so it adds no
    /// window.</item>
    /// <item><c>unrouted</c> — no gap at all; nothing moves an order INTO the routing hold after
    /// ingest.</item>
    /// </list>
    /// The ordinary operator path is shut for all four; the races above are not. Closing them needs
    /// an atomic claim on the resolve write itself, in the shape <c>OrdersController.AssignSupplier</c>
    /// already uses — a change to the WRITER, which is why it is not smuggled in behind an endpoint
    /// admission rule.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ResolveHeldFrom =
        Set(Unrouted, Delivering, Parsing, Transforming);

    /// <summary>
    /// The sentence an operator reads when <see cref="ResolveHeldFrom"/> — or its subset
    /// <see cref="MappingEditRefusedFrom"/> — refuses their correction.
    ///
    /// <para>Every one names what to DO — a status-code echo ("Order must be in one of these
    /// statuses: …") tells an operator what the server thinks and nothing about their next move.
    /// No raw status token appears in any of them, and no party noun: buyers first, inbound stays
    /// supported, so a sentence that says "supplier" is wrong on half the installs. Pinned by
    /// <c>OrdersControllerResolveStatusGuardTests</c>, which derives its rows from
    /// <see cref="ResolveHeldFrom"/> — so a status added to that set tomorrow fails the suite until
    /// it is given its own sentence here, rather than silently shipping the fallback.</para>
    ///
    /// <para><b>One table, three endpoints — not three copies.</b> MV-2 gated
    /// <c>PUT /api/orders/{id}/mapping-override</c> on <see cref="MappingEditRefusedFrom"/> and
    /// reused these sentences rather than minting a parallel set: both statuses it refuses already
    /// have one here, and both already end in "correct it and send it again", which is literally
    /// what a mapping override is. A second table would drift from this one the way the five
    /// delivery-claim lists drifted four times.</para>
    /// </summary>
    public static string ResolveHoldMessage(string status) => status switch
    {
        Unrouted =>
            "This order is still waiting to be routed. Route it first, then make your corrections — "
          + "correcting it now would take it out of the routing queue while it still has nowhere to go.",
        Delivering =>
            "This order is being sent right now, so it cannot be corrected mid-send. "
          + "Wait for the send to finish, then correct it and send it again.",
        // WP-23a. Both name the STEP in the operator's words, because neither status is a thing a
        // user has a mental model of — "still being read" and "being turned into its outgoing
        // document" are what they would say happened, and each sentence says what the correction
        // would cost if it were accepted (the whole upload; the document that goes out).
        Parsing =>
            "This order is still being read, so there is nothing to correct yet. "
          + "Wait until it has finished, then make your corrections — a correction saved now would "
          + "throw away everything the uploaded file contained.",
        Transforming =>
            "This order is being turned into its outgoing document right now, so a correction saved "
          + "now would be left out of the file that goes out. Wait for that step to finish, then "
          + "correct it and send it again.",
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
    /// <c>OpsController.RequeueDelivery</c>'s admission guard — the operator escalation that rescues
    /// an order the automatic queue has given up on, resetting the attempt cap and dispatching PAST
    /// the dead-letter. Named for the same reason as <see cref="RetryableFrom"/>: it was the last
    /// gate on the delivery path still written as a hand-written status literal
    /// (<c>is not (DeliveryDeadLetter or DeliveryFailed)</c>) while every sibling read a named set,
    /// and the user-facing 400 listed the same two statuses a SECOND time in a string. Two copies of
    /// one rule is how the five claim lists drifted apart four times, always silently.
    ///
    /// <list type="bullet">
    /// <item><c>delivery_dead_letter</c> — the whole point: the retry budget is spent and
    /// <c>/api/orders/{id}/retry-delivery</c> deliberately refuses this status, so this endpoint is
    /// the ONLY way back.</item>
    /// <item><c>delivery_failed</c> — the same escalation reached before the cap ran out, e.g. to
    /// dispatch immediately rather than wait out the backoff.</item>
    /// </list>
    ///
    /// <para><b>Deliberately NOT a subset of any claim set</b>, unlike <see cref="RetryableFrom"/>
    /// and <see cref="RedeliverableFrom"/>. <c>delivery_dead_letter</c> is in no claim set at all,
    /// and does not need to be: the endpoint REWRITES the order to the claimable
    /// <c>delivery_failed</c> before enqueuing, precisely so the 52c6431 shape (an endpoint that
    /// admits a status the claim then refuses, reporting success having sent nothing) cannot
    /// occur.</para>
    ///
    /// <para>The endpoint's refusal message is built FROM this set, so the statuses it names cannot
    /// drift from the statuses it admits.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> RequeueableFrom =
        Set(DeliveryDeadLetter, DeliveryFailed);

    /// <summary>
    /// The statuses a PARSE failure may OVERWRITE — the guard on
    /// <c>OrderIngestionService.SetOrderFailedAsync</c>, which stamps terminal
    /// <see cref="OrderStatusConstants.Failed"/> when a stored source file turns out to be an
    /// unsupported format, throws while parsing, or yields no lines. The parse-leg analogue of
    /// <see cref="TransformFailableFrom"/>, and named here for the same reason that one is: the
    /// rule is written twice at the call site — a relational <c>ExecuteUpdateAsync</c> predicate
    /// and its EF-InMemory emulation — and two hand-written copies of one status list are exactly
    /// how the five delivery-claim lists drifted apart four times, each time silently.
    ///
    /// <para><b>Why the write needs a claim at all.</b> <c>ParseStoredFileAsync</c> reads the order
    /// ONCE at the top and refuses unless the status is <c>parsing</c>, but every failure write
    /// sits far below that read — after a storage download, a format detection, and (on the PDF and
    /// XLSX paths) a network call to an LLM extractor. The row is UNCLAIMED for that whole window,
    /// and two things reach it there:</para>
    /// <list type="bullet">
    /// <item>A CONCURRENT PARSE THAT SUCCEEDED. Two live parse jobs for one order is not an exotic
    ///   race — it is the recovery path working as designed: <c>StuckOrderDetectionService</c>
    ///   deliberately KEEPS a stalled order in <c>parsing</c> and enqueues a fresh job, because
    ///   resetting it to <c>pending_parse</c> made the new job skip the parse entirely. If the
    ///   slower attempt finishes second it stamps <c>failed</c> over the winner's
    ///   <c>pending_review</c> / <c>ready</c>.</item>
    /// <item>AN OPERATOR'S REJECTION VERDICT. <c>OrderResolutionService.MarkRejectedAsync</c>
    ///   carries no from-status guard beyond "not finished", and <c>parsing →
    ///   rejected_by_supplier</c> is a documented edge above, so a human can record a supplier
    ///   refusal while this very parse is in flight. That is a finding about the document; a
    ///   generic machine failure must not replace it.</item>
    /// </list>
    ///
    /// <para><b>Why the damage is permanent, which is what makes this worth a claim rather than a
    /// log line.</b> <c>failed</c> is the sole member of <see cref="DeclaredTerminal"/>, so
    /// <c>OrderResolutionService.IsFinished</c> then refuses every correction the operator has
    /// left: resolve, accept-AI, mark-rejected. The order is not merely mislabelled, it is WEDGED —
    /// a terminal lie whose only cure is a new order row or a database edit. The transform leg
    /// reached the same conclusion first (<see cref="TransformFailableFrom"/>); this is the same
    /// hole on the other leg.</para>
    ///
    /// <para><b>Exactly <c>parsing</c>, and deliberately NOT <c>pending_parse</c>.</b> A parse only
    /// ever RUNS from <c>parsing</c> — <c>ParseStoredFileAsync</c>'s own idempotency guard compares
    /// against that literal, and the stuck sweep re-writes that literal for the same reason. A row
    /// still in <c>pending_parse</c> is therefore one no parse has started on, so a parse failure
    /// cannot be ABOUT it; admitting it would let a stale attempt stamp terminal failure on an
    /// order another job is about to pick up. <c>pending_parse</c> is covered by
    /// <c>StuckOrderDetectionService</c> instead, which sweeps it forward into <c>parsing</c> and
    /// only dead-letters it once the requeue budget is spent.</para>
    ///
    /// <para><b>Invariant:</b> a subset of the statuses <c>failed</c> is reachable from in
    /// <see cref="Transitions"/>. Pinned by
    /// <c>OrderStatusMachineTests.ParseFailableFrom_IsExactlyParsing</c>.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ParseFailableFrom = Set(Parsing);

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
    ///
    /// <para><b>This set answers "may I START a transform from here?" and nothing else.</b> The
    /// separate question "may I OVERWRITE this with a failure?" is
    /// <see cref="TransformFailableFrom"/>, which is deliberately narrower — see its documentation
    /// for why the two must not be the same list.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ClaimableForTransformFrom =
        Set(Ready, Transforming, TransformFailed, RejectedBySupplier);

    /// <summary>
    /// The statuses a transform failure may OVERWRITE — the guard on
    /// <c>OrderTransformService.FailTransformFromClaimableAsync</c>, which records a failure from
    /// OUTSIDE the claim (the wrapper's catch can fire before the claim was ever taken, or after a
    /// completed transform released it). Written TWICE at that call site — a relational
    /// <c>ExecuteUpdateAsync</c> predicate and its EF-InMemory emulation — so it is named here for
    /// the same reason the claim's set is: two hand-written copies of one rule are exactly how the
    /// five delivery-claim lists drifted apart four times, each time silently.
    ///
    /// <para><b>Why this is NOT <see cref="ClaimableForTransformFrom"/>.</b> The two sets answer
    /// different questions, and the tempting one-sentence rule ("if we could have claimed it, we may
    /// fail it") conflates them. This set is <see cref="ClaimableForTransformFrom"/> minus exactly
    /// <c>rejected_by_supplier</c>:</para>
    /// <list type="bullet">
    /// <item>STARTING a transform from <c>rejected_by_supplier</c> is the correction workflow — the
    ///   supplier read the document and refused it, and producing a corrected one is the cure. That
    ///   is precisely why the claim admits it.</item>
    /// <item>OVERWRITING <c>rejected_by_supplier</c> with <c>transform_failed</c> destroys a
    ///   HUMAN'S VERDICT and replaces it with a generic machine failure. That status is load-bearing
    ///   rather than decorative: it is the one status no delivery claim set admits, and it feeds the
    ///   supplier acceptance-rate figures the dashboard reports.</item>
    /// </list>
    ///
    /// <para>The interleaving is reachable, not theoretical.
    /// <c>OrderResolutionService.MarkRejectedAsync</c> carries no from-status guard beyond "not
    /// finished", and <c>transforming → rejected_by_supplier</c> is a documented edge in the map
    /// above. So an operator can record a supplier rejection while a transform is still in flight;
    /// when that transform then throws, the failure write must find the row NOT its to fail and
    /// leave the verdict standing.</para>
    ///
    /// <para><b>Invariant:</b> a subset of <see cref="ClaimableForTransformFrom"/> differing from it
    /// by exactly <c>rejected_by_supplier</c>. Pinned by
    /// <c>OrderStatusMachineTests.TransformFailableFrom_IsClaimableMinus_RejectedBySupplier</c>, so
    /// a later edit to either set has to confront the delta instead of silently erasing it.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> TransformFailableFrom =
        Set(Ready, Transforming, TransformFailed);

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
    /// MV-1 — the statuses a MAPPING EDIT must reset to <see cref="Ready"/>, because the order
    /// already holds a built artifact and some path can ship that artifact WITHOUT re-transforming.
    /// Consumed by <c>OrderMappingOverrideService.UpsertAsync</c>, which is the only writer of the
    /// per-order override.
    ///
    /// <para><b>Enumerated on purpose — deriving it is what would have hidden the bug.</b> The
    /// obvious derivation is the union of the delivery claim/admission sets above
    /// (<see cref="ClaimableForDispatchFrom"/> ∪ <see cref="ClaimableForRetryFrom"/> ∪
    /// <see cref="RequeueableFrom"/> ∪ <see cref="RedeliverableFrom"/> …), and it is WRONG in both
    /// directions. It omits <c>delivery_held</c> — the status this set was fixed for — because the
    /// release REWRITES a held order to <c>ready_to_deliver</c> before enqueuing, so the claim never
    /// sees <c>delivery_held</c> at all (exactly the shape <see cref="RequeueableFrom"/> documents
    /// for <c>delivery_dead_letter</c>). It also omits <c>delivered</c>, which is in no claim set
    /// and belongs here for a reason the claim sets do not encode. A derivation that needs
    /// hand-written corrections is an enumeration with extra steps, and a less legible one.
    /// <br/>What replaces the derivation is a TOTALITY guard rather than a cleverer expression:
    /// <c>OrderStatusMachineTests.EveryStatus_IsClassifiedForTheMappingEditReset</c> partitions
    /// <see cref="AllStatuses"/> into this set, <see cref="MappingEditRefusedFrom"/>, a named
    /// residual, and the artifact-free remainder, so a status added tomorrow fails the build until
    /// someone decides which it is. The union invariant (every status in any delivery claim set is
    /// in this set) is asserted too — it is the half a derivation CAN prove soundly, so it is kept
    /// as a check rather than as the definition.</para>
    ///
    /// <list type="bullet">
    /// <item><c>ready_to_deliver</c> / <c>delivered</c> — the artifact exists and the next Send
    /// would redeliver it.</item>
    /// <item><c>delivery_failed</c> / <c>delivery_dead_letter</c> / <c>delivery_unconfirmed</c> —
    /// post-artifact and re-dispatchable/rescuable. Retry/Redeliver, the ops requeue, and "Send
    /// again" on a park all ship the LATEST STORED artifact without re-transforming.</item>
    /// <item><c>delivery_held</c> — the same bug one status over, and the worst of them because no
    /// human is in its loop. A billing hold parks a TRANSFORMED order holding its artifact;
    /// <c>DeliveryService.ReleaseBillingHeldOrdersAsync</c> then writes <c>ready_to_deliver</c> and
    /// calls <c>IRetryDeliveryEnqueuer.EnqueueAsync</c> the moment the org returns to good standing,
    /// and <c>RetryDeliveryAsync</c> resolves the stored artifact and dispatches it. A correction
    /// typed while the order was held would reach nobody, and nothing would report that it had been
    /// dropped. Resetting to <c>ready</c> also takes the row out of the release sweep's
    /// <c>Status == delivery_held</c> filter, which is correct and already anticipated there: the
    /// order waits for its re-transform instead of auto-sending a document nobody approved.</item>
    /// </list>
    ///
    /// <para><b>Deliberately NOT here: <c>rejected_by_supplier</c>.</b> No delivery claim set admits
    /// it — not dispatch, not retry, not the ops requeue, not Redeliver — so the refused artifact
    /// cannot be re-shipped IN PLACE. Its only exits are the correction loop (resolve, or a
    /// re-transform via <see cref="ClaimableForTransformFrom"/>), and both already invalidate the old
    /// artifact. Resetting here would be dead weight, not a fix.</para>
    ///
    /// <para><b>Not here because the edit never arrives: <c>delivering</c> and
    /// <c>transforming</c>.</b> Both are refused at the endpoint by
    /// <see cref="MappingEditRefusedFrom"/>, which carries that decision and its per-status
    /// evidence. <c>delivering</c> was MV-1's named residual — an accepted edit whose pre-edit bytes
    /// still went out — and a reset was never the cure for it. <c>transforming</c> WAS in this set
    /// (part of the original MV-1 trio), and its reset provably could not land:
    /// <c>OrderTransformService</c>'s completion write is a tracked
    /// <c>Status = ready_to_deliver</c> with no status predicate and no concurrency token, so it
    /// overwrites the reset last-writer-wins on every successful transform. A reset that cannot land
    /// is not protection; leaving it here claimed one.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> MappingEditInvalidatesArtifactFrom =
        Set(ReadyToDeliver, Delivered,
            DeliveryHeld, DeliveryFailed, DeliveryDeadLetter, DeliveryUnconfirmed);

    /// <summary>
    /// MV-2 — the statuses <c>PUT /api/orders/{id}/mapping-override</c> REFUSES an edit from,
    /// because the edit would be accepted, stored, and then silently discarded while a document
    /// built before it goes out. Consumed by <c>OrdersController.PutMappingOverride</c> through the
    /// same <c>RefusedByStatusHoldAsync</c> helper the two recompute endpoints use, and answered
    /// with <see cref="ResolveHoldMessage"/> — the operator-facing sentences already written for
    /// these statuses, not a second copy of them. Both existing sentences say "correct it, then send
    /// it again", which is exactly what a mapping override is, so the reuse is literal rather than
    /// approximate.
    ///
    /// <para><b>A subset of <see cref="ResolveHeldFrom"/>, and deliberately a strict one.</b> That
    /// set was evidenced against a DIFFERENT writer — the unconditional status recompute shared by
    /// <c>/resolve</c> and <c>/accept-ai-suggestions</c>. This endpoint's writer is
    /// <c>OrderMappingOverrideService.UpsertAsync</c>: a <c>canonical_json</c> merge plus a
    /// conditional status reset. Reusing all four of <c>ResolveHeldFrom</c> would be transcription,
    /// not a decision, and this file's own rule for that set is "name the writer, name the line, say
    /// what it destroys — never by a refactor's side effect". So the two statuses below carry that
    /// evidence and the other two do not:
    /// <list type="bullet">
    /// <item><c>delivering</c> — MV-1's named residual. The artifact has already been downloaded and
    /// handed to the dispatcher, so the edit changes nothing about what is being sent, and a reset
    /// cannot cure it (writing <c>ready</c> un-sends nothing and lands over a live dispatch claim
    /// last-writer-wins, the harm the <c>delivering</c> bullet of <see cref="ResolveHeldFrom"/>
    /// documents). The in-flight send then lands <c>delivered</c> or <c>delivery_failed</c>, and
    /// from the latter the AUTOMATIC retry — or <c>StuckDeliveryDetectionService</c>, which re-drives
    /// stale <c>delivering</c> rows on a timer with no human in the loop — resolves the SAME stored
    /// pre-edit artifact and ships it again. <c>DeliveryService.RetryDeliveryAsync</c> admits
    /// <c>delivering</c> explicitly in its advisory gate, so that path is open by design.</item>
    /// <item><c>transforming</c> — the reset this set replaces was provably defeated.
    /// <c>OrderTransformService</c> claims atomically and then resyncs the tracked entity's CURRENT
    /// and ORIGINAL values to <c>transforming</c>, but its completion write is a tracked
    /// <c>Status = ready_to_deliver</c> with no status predicate and no concurrency token — so it
    /// lands unconditionally over an edit's <c>ready</c>, and the artifact it commits was built from
    /// the graph loaded BEFORE the edit, with <c>TransformOrderJob</c> enqueueing delivery
    /// immediately after. Identical to the <c>transforming</c> bullet
    /// <c>OrdersControllerResolveStatusGuardTests</c> already cites for <c>/resolve</c>, which is
    /// why leaving the third door open would be the drift WP-23 closed for the first two.</item>
    /// </list></para>
    ///
    /// <para><b>Deliberately NOT here: <c>parsing</c> and <c>unrouted</c>.</b> <c>ResolveHeldFrom</c>
    /// refuses both, for harm this endpoint's writer cannot do. The parse does NOT write
    /// <c>canonical_json</c> — <c>OrderIngestionService</c> states that as a load-bearing invariant
    /// ("the typed columns are the single source of truth for this order's header") and the only
    /// other production writer of an EXISTING row's <c>canonical_json</c> is
    /// <c>OrderResolutionService</c>'s header merge, which is itself gated by that set. So an
    /// override saved during <c>parsing</c> is not clobbered by the parse, and it invalidates no
    /// artifact because none exists. And unlike the recompute, this endpoint never writes a status
    /// on an <c>unrouted</c> order (<c>unrouted</c> is not in
    /// <see cref="MappingEditInvalidatesArtifactFrom"/>), so it cannot recompute the order out of
    /// the routing hold — the precise harm that put <c>unrouted</c> in <c>ResolveHeldFrom</c>.
    /// A fifth status arrives the way these two did, with a writer and a line, and never as a
    /// symmetry argument.</para>
    ///
    /// <para><b>Refused, not made impossible.</b> Like the resolve guard, this is a check-then-act
    /// read at the endpoint and <c>PurchaseOrderEntity</c> carries no concurrency token, so an order
    /// that ENTERS one of these statuses between the read and <c>UpsertAsync</c>'s
    /// <c>SaveChangesAsync</c> is still accepted. Closing that needs an atomic claim on the override
    /// write itself, a change to the WRITER. The ordinary operator path is shut, which is what
    /// removes the reachable harm.</para>
    ///
    /// <para><b>What the operator loses, priced.</b> A refusal costs waiting, never a dead end:
    /// a transform is seconds, a send is minutes, and neither status can strand an order past them —
    /// <c>StuckOrderDetectionService</c> recovers a stalled <c>transforming</c> row to <c>ready</c>
    /// on its own timer (never to terminal <c>failed</c>), and <c>StuckDeliveryDetectionService</c>
    /// moves a stalled <c>delivering</c> row on to a retry or the dead-letter. From every one of
    /// those landing statuses the edit is accepted again, and from all but <c>ready</c> it also
    /// resets the artifact via <see cref="MappingEditInvalidatesArtifactFrom"/>.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> MappingEditRefusedFrom =
        Set(Delivering, Transforming);

    /// <summary>
    /// The same rule for a SUPPLIER-level mapping save
    /// (<c>PUT /api/suppliers/{id}/po-mapping</c>, apply-template, the two DELETEs, and
    /// <c>POST /api/orders/{id}/mapping-override/promote</c>). Those endpoints persist a
    /// <c>PoMappingConfig</c> whose <c>Output</c>/<c>OutputTree</c> half the transform consumes for an
    /// order with no per-order output seam, and none of them writes <c>PurchaseOrders</c> — so without
    /// this the pre-edit artifact is what Redeliver / RetryDelivery / the ops requeue / the stranded
    /// sweep subsequently ships, exactly as in
    /// <see cref="MappingEditInvalidatesArtifactFrom"/> but for many orders at once.
    ///
    /// <para><b>It is that set minus <c>delivered</c>, and the subtraction is the whole product
    /// decision.</b> A per-order mapping edit is a statement about THAT order's document, so
    /// resurrecting it is proportionate. A supplier-level save is a statement about the SUPPLIER —
    /// <c>PromoteMappingService.BuildPromotedMessage</c> tells the operator so in as many words
    /// ("Future uploads from this supplier reuse it"). Carrying <c>delivered</c> here would mean one
    /// typo fix in a supplier's output layout silently flips every order that supplier ever completed
    /// back to <c>ready</c>, where it reappears as pending work. A completed order re-enters this
    /// class only through Redeliver, which is a deliberate act on ONE order — and a per-order mapping
    /// edit before that Redeliver is already covered by the set above.</para>
    ///
    /// <para>The difference is pinned to exactly <c>{delivered}</c> by
    /// <c>OrderStatusMachineTests.SupplierMappingEditReset_DiffersFromThePerOrderResetByExactlyDelivered</c>,
    /// so widening either set without deciding about the other fails the build. The same totality
    /// guard that partitions <see cref="AllStatuses"/> for the per-order set covers this one.</para>
    ///
    /// <para><b>The two <see cref="MappingEditRefusedFrom"/> statuses are residuals here, and unlike
    /// the per-order path there is no cure to offer.</b> That endpoint answers 409 for
    /// <c>delivering</c> and <c>transforming</c> because it edits ONE order and the operator can act
    /// on the refusal. A supplier-level save cannot be refused because one of that supplier's orders
    /// happens to be mid-transform — that would make the primary configuration surface unavailable
    /// for reasons the operator did not cause and cannot see. A reset is no better: for
    /// <c>delivering</c> the artifact is already with the dispatcher, and for <c>transforming</c> the
    /// completion write is untokened (it resyncs the tracked row to <c>transforming</c>, so
    /// <c>entity.Status = ready_to_deliver</c> lands with no status predicate) and silently
    /// overwrites anything written underneath it. So an order that was mid-transform at the moment of
    /// the save keeps an artifact built from the pre-edit config. The window is one transform long
    /// and the real cure is a status predicate or concurrency token on THAT writer, which is neither
    /// this packet's change nor something to fake with a reset that cannot land.</para>
    ///
    /// <para><b>Membership is necessary, not sufficient.</b> A status in this set only makes an order
    /// a CANDIDATE. <c>ArtifactInvalidatingPoMappingService</c> then drops the orders the live
    /// supplier mapping provably cannot reach — the ones pinned to a published revision (whose
    /// transform never reads the live table) and the ones carrying a per-order output seam that
    /// outranks it — and does nothing at all when the save left the <c>Output</c>/<c>OutputTree</c>
    /// half unchanged.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> SupplierMappingEditInvalidatesArtifactFrom =
        Set(ReadyToDeliver,
            DeliveryHeld, DeliveryFailed, DeliveryDeadLetter, DeliveryUnconfirmed);

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
