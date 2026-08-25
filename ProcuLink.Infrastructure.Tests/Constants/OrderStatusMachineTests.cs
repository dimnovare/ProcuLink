using System.Reflection;
using FluentAssertions;
using ProcuLink.Core.Constants;
using ProcuLink.Infrastructure.Services;
using Xunit;
using static ProcuLink.Core.Constants.OrderStatusConstants;

namespace ProcuLink.Infrastructure.Tests.Constants;

public class OrderStatusMachineTests
{
    [Theory]
    // Every transition the live code actually performs must be allowed (superset).
    [InlineData(PendingParse, Parsing)]
    [InlineData(Parsing, PendingReview)]
    [InlineData(Parsing, Ready)]
    [InlineData(Parsing, Failed)]
    [InlineData(Parsing, PendingParse)]          // stuck-order requeue
    [InlineData(PendingReview, Ready)]            // resolve
    [InlineData(Ready, Transforming)]            // transform
    [InlineData(Ready, PendingReview)]           // resolve recompute
    [InlineData(Transforming, ReadyToDeliver)]
    [InlineData(Transforming, Ready)]            // transform validation revert / requeue
    [InlineData(Transforming, Failed)]
    // A TERMINAL transform failure (broken template / unusable output mapping) parks the order
    // VISIBLY instead of reverting it to 'ready', where it was indistinguishable from
    // "never transformed" — see OrderTransformService.FailTransformAsync.
    [InlineData(Transforming, TransformFailed)]
    // ...and back OUT again: the user fixes the template/mapping and re-transforms. The transform
    // claim accepts transform_failed precisely so this is not a dead end.
    [InlineData(TransformFailed, Transforming)]
    [InlineData(ReadyToDeliver, Delivering)]
    [InlineData(ReadyToDeliver, DeliveryFailed)] // missing config
    [InlineData(ReadyToDeliver, DeliveryHeld)]   // mid-pipeline billing flip pauses delivery
    [InlineData(DeliveryHeld, ReadyToDeliver)]   // reactivation releases the hold and re-drives
    [InlineData(Delivering, Delivered)]
    [InlineData(Delivering, DeliveryFailed)]
    [InlineData(Delivering, DeliveryDeadLetter)] // stuck delivery, re-drive budget spent (StuckDeliveryDetectionService)
    [InlineData(DeliveryFailed, Delivering)]     // retry / redeliver
    [InlineData(DeliveryFailed, DeliveryDeadLetter)]
    [InlineData(DeliveryFailed, Ready)]          // MV-1 sibling: mapping edit after a failed delivery
    // An operator "Send again" from the park, for an org that lapsed since the park, is held
    // (not delivered) by DeliverOrderJob's billing gate → HoldForBillingAsync. Same case as
    // delivery_failed → delivery_held above.
    [InlineData(DeliveryUnconfirmed, DeliveryHeld)]
    // ...and back OUT again: the billing release restores a held park to delivery_unconfirmed
    // (HeldFromStatus records the origin) instead of re-driving it — an automatic re-send of an
    // unknown-outcome PO on a channel that cannot de-duplicate is the duplicate the park exists
    // to prevent. See ReleaseBillingHeldOrdersAsync.
    [InlineData(DeliveryHeld, DeliveryUnconfirmed)]
    [InlineData(DeliveryDeadLetter, Delivering)] // ops requeue rescue
    [InlineData(DeliveryDeadLetter, DeliveryFailed)] // requeued dead-letter fails again (aligns with the observer map)
    [InlineData(DeliveryDeadLetter, Ready)]      // MV-1 sibling: mapping edit after dead-letter
    // The four "late positive ACK" edges that used to sit here — ready_to_deliver / delivery_failed /
    // delivery_dead_letter / delivery_held → delivered — are GONE with the inbound supplier-status
    // webhook (WP-09). They are now asserted IMPOSSIBLE, below and in
    // EveryInboundDeliveredEdge_HasAProductionWriter.
    // delivered → delivery_failed survives WITHOUT the webhook: DeliveryService's pre-claim failure
    // paths (FailMissingConfigAsync / FailBeforeDispatchAsync) write delivery_failed with no
    // from-status check, racing the enqueue-time guards in Redeliver / RequeueDelivery.
    [InlineData(Delivered, DeliveryFailed)]
    [InlineData(Ready, RejectedBySupplier)]      // mark-rejected (from any non-terminal)
    [InlineData(Delivering, RejectedBySupplier)]
    // Routing (Phase 0): an order can be parked unrouted while it awaits a supplier, then
    // re-enter the parse flow once one is assigned.
    [InlineData(PendingParse, Unrouted)]         // extract found no supplier → hold
    [InlineData(Parsing, Unrouted)]              // extract found no supplier → hold
    [InlineData(Unrouted, Parsing)]              // assign-supplier re-enqueues parse
    [InlineData(Unrouted, PendingParse)]
    [InlineData(Unrouted, RejectedBySupplier)]   // operator discards an unrouted order
    // WP-19: rejected_by_supplier is NOT a dead end. A genuine business rejection is cured by
    // correcting the order and sending a corrected document, and both halves of that already have
    // production writers:
    //   • resolve   — OrderResolutionService.ResolveAsync recomputes pending_review|ready with NO
    //                 from-status guard, so fixing the line the supplier named moves the order out.
    //   • transform — OrderTransformService's claim accepts rejected_by_supplier (ClaimableForTransformFrom),
    //                 exactly as it accepts transform_failed: the recovery door after the operator
    //                 has fixed whatever the supplier refused.
    [InlineData(RejectedBySupplier, Ready)]
    [InlineData(RejectedBySupplier, PendingReview)]
    [InlineData(RejectedBySupplier, Transforming)]
    // ── The resolve recompute, for the OTHER from-states WP-19 left behind ────────────────────
    // Same writer as the two rejected_by_supplier rows above: OrderResolutionService.ResolveAsync
    // (:242) and AcceptAiSuggestionsAsync (:393) end with
    // `Status = Lines.Any(NeedsReview) ? pending_review : ready`, with no from-status guard on the
    // service and none on either endpoint (OrdersController.Resolve :483 validates the request body
    // only). The one guard is IsFinished = DeclaredTerminal = {failed}. WP-19 reconciled the writer
    // for one from-state because that status was a dead end; the writer reaches every OTHER
    // non-terminal status identically, and those edges were live in production while both maps
    // called them impossible.
    //
    // → ready follows from the write. → pending_review is the half that reads impossible from a
    // post-review status and is not: the connection-level price-variance guard re-runs on EVERY
    // resolve (OrderResolutionService.cs:206-239) and re-sets line.NeedsReview when a unit price
    // diverges from the catalog, and a partial resolve leaves un-named lines flagged.
    //
    // Enumerated here for readability; EveryStatusAResolveCanBeIssuedFrom_HasBothRecomputeEdges is
    // the assertion that cannot rot, because it DERIVES the from-state list from AllStatuses.
    [InlineData(Unrouted, PendingReview)]           // an unrouted order has lines; resolve recomputes
    [InlineData(Unrouted, Ready)]                   // ...even a header-only one — see the Unrouted row's follow-up
    [InlineData(Transforming, PendingReview)]
    [InlineData(ReadyToDeliver, PendingReview)]
    [InlineData(DeliveryHeld, PendingReview)]       // a billing hold pauses delivery, not correction
    [InlineData(Delivering, PendingReview)]         // a row SITS in 'delivering'; the endpoint has no guard
    [InlineData(Delivering, Ready)]
    [InlineData(Delivered, PendingReview)]          // price-variance guard re-flags a line on a delivered order
    [InlineData(DeliveryFailed, PendingReview)]     // the observer listed this all along; the machine did not
    [InlineData(DeliveryUnconfirmed, PendingReview)]
    [InlineData(DeliveryDeadLetter, PendingReview)]
    public void IsAllowed_RealTransitions_AreAllowed(string from, string to)
        => OrderStatusMachine.IsAllowed(from, to).Should().BeTrue($"{from} -> {to} is a real flow");

    [Theory]
    // Genuinely-impossible moves must be rejected (this is the value the machine adds).
    [InlineData(Delivered, Parsing)]
    [InlineData(Failed, Delivering)]
    // rejected_by_supplier → delivering stays impossible even after WP-19 gave the status an exit:
    // the exit is a CORRECTION loop (resolve / re-transform), never a re-send of the same bytes.
    // No delivery claim set admits rejected_by_supplier, so nothing can dispatch it in place.
    [InlineData(RejectedBySupplier, Delivering)]
    [InlineData(Delivered, Transforming)]
    [InlineData(Parsing, Delivered)]
    [InlineData(RejectedBySupplier, Delivered)]  // no silent un-rejection: nothing writes it
    // WP-09: the retired inbound supplier-status webhook was the ONLY writer of these four. An
    // operator settlement (OrdersController.MarkDelivered) admits delivery_unconfirmed alone, and
    // DeliveryService writes 'delivered' only after its claim has moved the row to 'delivering'.
    [InlineData(ReadyToDeliver, Delivered)]
    [InlineData(DeliveryFailed, Delivered)]
    [InlineData(DeliveryDeadLetter, Delivered)]
    [InlineData(DeliveryHeld, Delivered)]
    public void IsAllowed_ImpossibleTransitions_AreRejected(string from, string to)
        => OrderStatusMachine.IsAllowed(from, to).Should().BeFalse($"{from} -> {to} must never happen");

    [Theory]
    [InlineData(Failed)]
    public void IsTerminal_TrueForTerminalStates(string status)
        => OrderStatusMachine.IsTerminal(status).Should().BeTrue();

    [Theory]
    [InlineData(Delivered)]            // a pre-claim delivery failure can still flip it to delivery_failed
    [InlineData(DeliveryDeadLetter)]   // an ops requeue can still rescue it
    [InlineData(Ready)]
    // WP-19: a genuine business rejection is a correction loop, not an ending. The operator fixes
    // what the supplier named and re-sends; resolve and the transform claim are the two doors.
    [InlineData(RejectedBySupplier)]
    // transform_failed is a FAILURE state but NOT a terminal one: unlike 'failed' (a bad source file,
    // where recovery means a NEW order row), the order itself is fine — its template/mapping is broken.
    // Fixing that and re-transforming is the intended cure, so it must have a way out.
    [InlineData(TransformFailed)]
    public void IsTerminal_FalseForNonTerminalStates(string status)
        => OrderStatusMachine.IsTerminal(status).Should().BeFalse();

    [Fact]
    public void IsFailure_MatchesCanonicalBucket()
    {
        foreach (var s in FailureBucket)
            OrderStatusMachine.IsFailure(s).Should().BeTrue();
        OrderStatusMachine.IsFailure(Delivered).Should().BeFalse();
        OrderStatusMachine.IsFailure(Ready).Should().BeFalse();
    }

    [Fact]
    public void RedeliverableFrom_IsExactlyTheThreeOperatorSendableStatuses()
        // Exact, not a superset: a status added here becomes re-sendable from the UI, so widening
        // the set must be a deliberate edit rather than a silent side effect.
        // delivery_unconfirmed belongs because the park exists precisely so a HUMAN can choose to
        // re-send: the outcome of the original send was never observed, so a re-send may duplicate
        // the PO — a risk the automatic retry must never take on the operator's behalf.
        => OrderStatusMachine.RedeliverableFrom.Should()
            .BeEquivalentTo(new[] { DeliveryFailed, ReadyToDeliver, DeliveryUnconfirmed });

    /// <summary>
    /// <c>OpsController.RequeueDelivery</c>'s admission guard, pinned EXACTLY — the last gate on the
    /// delivery path that was still a hand-written status literal rather than a named set.
    ///
    /// <para>Exact, not a superset, for the same reason as
    /// <see cref="RedeliverableFrom_IsExactlyTheThreeOperatorSendableStatuses"/>: a status added
    /// here becomes rescuable past the dead-letter cap with the attempt budget reset, which is the
    /// most powerful control in the product. Widening it must be a deliberate edit.</para>
    ///
    /// <para><b>Deliberately NOT a subset of any claim set.</b> <c>delivery_dead_letter</c> is in no
    /// claim set at all, and does not need to be: the endpoint REWRITES the order to the claimable
    /// <c>delivery_failed</c> before it enqueues (<c>OpsController.cs</c>, "claimable, send-ready
    /// idle state"). Asserting the 52c6431 subset invariant here would therefore be asserting a rule
    /// this path does not follow.</para>
    /// </summary>
    [Fact]
    public void RequeueableFrom_IsExactlyTheTwoStatusesAnOperatorMayRescue()
        => OrderStatusMachine.RequeueableFrom.Should()
            .BeEquivalentTo(new[] { DeliveryDeadLetter, DeliveryFailed });

    /// <summary>
    /// Edges the observer calls "expected" that the machine deliberately does NOT allow.
    ///
    /// <para>The two maps are both supersets of the real flows, but for opposite reasons, so
    /// neither strictly contains the other. The observer is generous ON PURPOSE — it only logs,
    /// and a false-positive warning would train operators to ignore it, so it lists edges its
    /// author merely believed possible. The machine is the stricter of the two: its whole value
    /// is calling genuinely-impossible moves impossible. Blindly copying these edges into the
    /// machine would empty it of meaning.</para>
    ///
    /// <para>Each edge below was checked against production code and is NOT performed by any
    /// call site. Grouped by why the observer lists it anyway. This set may only ever SHRINK —
    /// <see cref="Machine_Transitions_AreASupersetOf_ObserverAllowedTransitions"/> fails on a
    /// stale entry, so reconciling an edge in either map forces its removal here.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> KnownObserverOnlyEdges = new HashSet<string>(StringComparer.Ordinal)
    {
        // The observer's "a failed parse can be retried" comment is wrong: nothing re-parses an
        // order whose status is 'failed'. ParseOrderJob calls it a terminal status and throws
        // rather than re-driving it (ParseOrderJob.cs:67-74); elsewhere it only FILTERS on failed
        // (:89-91). OrdersController rejects a transform of one outright ("Upload a corrected file
        // before transforming", OrdersController.cs:1355). Recovery is a NEW order row, so
        // 'failed' really is terminal, as the machine says.
        Edge(Failed, PendingParse),
        Edge(Failed, Parsing),

        // These two needed a SECOND argument, and until the WP-19 follow-up they did not have one.
        // Re-parse and transform are not the only writers that can move an order out of 'failed':
        // RESOLVE is, and it had no from-status guard. OrderResolutionService recomputed
        // pending_review|ready from the lines on THREE paths (ResolveAsync, AcceptAiSuggestionsAsync)
        // and wrote rejected_by_supplier on a fourth (MarkRejectedAsync) — and a failed source file
        // leaves no lines, so every recompute landed on 'ready'. That is the same writer whose
        // existence forced Edge(RejectedBySupplier, PendingReview) and Edge(RejectedBySupplier, Ready)
        // OUT of this list and into the machine, so leaving these two in on a re-parse-and-transform
        // argument was the same fact reaching opposite conclusions two entries apart.
        //
        // Resolved in the direction the product already takes everywhere else: 'failed' means the
        // SOURCE FILE could not be read, recovery is a new order row, and the three writers now
        // refuse a status in OrderStatusMachine.DeclaredTerminal (OrderResolutionService.IsFinished,
        // pinned by OrderServiceTerminalOrderGuardTests). So these edges have no production writer —
        // now for a reason that covers every writer, not just two of them.
        Edge(Failed, PendingReview),
        Edge(Failed, Ready),

        // Nothing WRITES 'pending_parse': every ingest path stamps 'parsing' straight onto
        // the stub (OrderIngestionService.cs:343, SampleOrderService.cs:130), and the stuck-order
        // requeue re-writes 'parsing' too. No order is ever in pending_parse, so nothing can
        // RESOLVE one — which is what keeps these two edges writerless.
        //
        // Edge(PendingParse, Failed) used to sit here on the same argument, and it no longer holds.
        // StuckOrderDetectionService now SWEEPS pending_parse — not because an order is in it, but
        // because it is the entity's C# default and therefore a status that leaks the first time an
        // ingest path forgets an assignment, into a hole with no sweeper, no alert and no UI bucket.
        // That sweep re-drives such an order through 'parsing' and, past the requeue budget,
        // dead-letters it to 'failed' exactly as it does a 'parsing' strand. So the edge has a
        // production writer for precisely the case it exists to cover, and it is declared in the
        // machine rather than exempted here.
        Edge(PendingParse, PendingReview),
        Edge(PendingParse, Ready),

        // No call site fails an order from the review loop; a hard failure is only ever written
        // from a PARSE-side status — 'parsing' (OrderIngestionService.SetOrderFailedAsync, whose
        // claim admits exactly OrderStatusMachine.ParseFailableFrom, and the stuck sweep's
        // dead-letter) or the pending_parse leak the same sweep now covers.
        Edge(PendingReview, Failed),
        Edge(Ready, Failed),

        // No re-transform path re-enters 'transforming' from these: the transform claim is keyed on
        // ready|transforming|transform_failed (OrderTransformService.cs), and a mapping edit resets a
        // post-artifact order to 'ready' first (the MV-1 edges the machine already allows).
        Edge(ReadyToDeliver, Transforming),
        Edge(DeliveryFailed, Transforming),
        // Edge(DeliveryFailed, PendingReview) used to sit here, exempted on the grounds that "no
        // call site" performed it. That was wrong, and wrong in the way this file keeps finding:
        // the resolve recompute has no from-status guard, so it reaches delivery_failed exactly as
        // it reached rejected_by_supplier. The observer had it right and the machine did not, which
        // is the same verdict WP-19 reached one entry further down. Reconciled into the machine —
        // this exemption is deleted, not moved.

        // Nothing DISPATCHES an order out of 'rejected_by_supplier': the ops requeue guards on
        // dead_letter|delivery_failed (OpsController.cs:126) and every delivery claim set
        // (ClaimableForDispatchFrom / ClaimableForAutomaticDispatchFrom / ClaimableForRetryFrom)
        // excludes it. The exit WP-19 gave the status is a correction loop — resolve and
        // re-transform — never a re-send of the bytes the supplier already refused, so this one
        // edge stays observer-only while → pending_review / ready / transforming were reconciled
        // into the machine.
        Edge(RejectedBySupplier, Delivering),

        // rejected_by_supplier -> delivery_failed is reachable through DeliveryService's pre-claim
        // failure paths (FailMissingConfigAsync / FailBeforeDispatchAsync), which write
        // delivery_failed with no status check, racing the enqueue-time guards in
        // OrdersController.Redeliver / OpsController.RequeueDelivery. Rare, but real — and the
        // OBSERVER LISTS it as expected, so it stays silent when it fires. That silence is precisely
        // WHY this exemption exists: the assertion below flags observer-listed edges the machine
        // calls impossible, and this is one.
        Edge(RejectedBySupplier, DeliveryFailed),
    };

    private static string Edge(string from, string to) => $"{from} -> {to}";

    /// <summary>
    /// The superset invariant, enforced STRUCTURALLY rather than trusted to review.
    ///
    /// <para><see cref="OrderStatusMachine.Transitions"/> is documented as a superset of every
    /// transition the code actually performs, so <c>IsAllowed</c> never rejects a real flow.
    /// <see cref="OrderStatusTransitionObserver"/>'s <c>AllowedTransitions</c> is the codebase's
    /// other, independently hand-maintained inventory of the same flows. Where the two disagree,
    /// one of them is wrong — either the machine is missing a real edge (and would reject a real
    /// flow), or the observer is blessing an edge that cannot happen.</para>
    ///
    /// <para>Two hand-maintained maps drift, and this pair has drifted twice: the A5 delivery_held
    /// work (d4d6eac) had to fix a transition registered in only one map, and delivering →
    /// delivery_dead_letter (StuckDeliveryDetectionService.cs:122) sat in the observer but not the
    /// machine. Pinning the invariant over BOTH maps catches the whole drift class at build time
    /// instead of one entry at a time in review.</para>
    ///
    /// <para>The assertion is two-sided so <see cref="KnownObserverOnlyEdges"/> cannot rot: a new
    /// unexempted disagreement fails, and so does an exemption that no longer disagrees.</para>
    /// </summary>
    [Fact]
    public void Machine_Transitions_AreASupersetOf_ObserverAllowedTransitions()
    {
        var drift = new List<string>();

        foreach (var (from, observerTargets) in OrderStatusTransitionObserver.AllowedTransitions)
        {
            var machineTargets = OrderStatusMachine.NextStatuses(from);

            foreach (var to in observerTargets)
            {
                // Self-transitions are implicitly expected by the observer and are not
                // required to appear in the machine's map.
                if (string.Equals(from, to, StringComparison.Ordinal)) continue;

                if (!machineTargets.Contains(to))
                    drift.Add(Edge(from, to));
            }
        }

        drift.Except(KnownObserverOnlyEdges).Should().BeEmpty(
            "OrderStatusMachine.Transitions must allow every transition OrderStatusTransitionObserver " +
            "treats as an expected, legitimate flow — otherwise OrderStatusMachine.IsAllowed reports a real " +
            "flow as impossible. Add the missing target(s) to OrderStatusMachine.Transitions[from]; or, if the " +
            "edge is not a real flow, exempt it in KnownObserverOnlyEdges with the call-site evidence that it " +
            "cannot happen.");

        KnownObserverOnlyEdges.Except(drift).Should().BeEmpty(
            "every KnownObserverOnlyEdges entry must still be a live disagreement between the two maps. A stale " +
            "entry means the edge was reconciled (or removed from the observer) without pruning the exemption, " +
            "which would silently re-open the drift it was hiding. Delete the listed entries.");
    }

    /// <summary>
    /// EVERY edge that ends in <c>delivered</c>, in BOTH maps, must have a production writer.
    ///
    /// <para><b>Why this test exists.</b> The two-sided invariant above compares the maps to EACH
    /// OTHER, so it is structurally blind to an edge both maps agree on that nothing performs — and
    /// WP-09 created four of them at once. The retired inbound webhook was the writer for
    /// <c>ready_to_deliver → delivered</c>, <c>delivery_held → delivered</c>,
    /// <c>delivery_failed → delivered</c> and <c>delivery_dead_letter → delivered</c>; when it went,
    /// the edges stayed in both maps with comments still citing "supplier status webhooks" and "a
    /// late supplier ACK". The observer would then have stayed SILENT on four transitions nothing
    /// can perform — the exact drift its own "both maps or neither" comment warns about, in the one
    /// direction the sibling test cannot see.</para>
    ///
    /// <para><b>Why <c>delivered</c> specifically is testable.</b> Its writers are enumerable and
    /// each one is gated by a NAMED set rather than a literal, so this assertion reads the same
    /// declarations production reads instead of keeping a third hand-copied list:</para>
    /// <list type="bullet">
    ///   <item><c>DeliveryService.PersistAttemptAsync</c> — the automatic writer. It is only ever
    ///     reached AFTER the dispatch/retry claim, and the claim resyncs the tracked entity's
    ///     <c>Status</c> AND its <c>OriginalValue</c> to <c>delivering</c> (both the relational and
    ///     the InMemory branch) precisely so the observer diffs <c>delivering → delivered</c>. Its
    ///     pre-claim siblings (<c>FailMissingConfigAsync</c> / <c>FailBeforeDispatchAsync</c>) pass
    ///     a FAILED result, so they can only write <c>delivery_failed</c> /
    ///     <c>rejected_by_supplier</c>, never <c>delivered</c>.</item>
    ///   <item><c>OrdersController.MarkDelivered</c> — the manual writer, gated (twice) on
    ///     <see cref="OrderStatusMachine.ManuallyDeliverableFrom"/>.</item>
    /// </list>
    ///
    /// <para><b>The general case is NOT tested, and deliberately so.</b> "Every edge in both maps
    /// has a writer" cannot be asserted in general: a status write is an arbitrary assignment
    /// anywhere in the solution, tracked or via <c>ExecuteUpdateAsync</c>, and reflection cannot see
    /// method bodies. A source scan for <c>Status =</c> would find the writes but not the from-state
    /// each one is reachable in, which is the whole question. So the invariant is enforced where the
    /// writers ARE named — and adding a status to a named gate is the moment to extend this test to
    /// it.</para>
    /// </summary>
    [Fact]
    public void EveryInboundDeliveredEdge_HasAProductionWriter()
    {
        // delivering: DeliveryService's claim leaves every claimed row here before the outcome
        // write. ManuallyDeliverableFrom: the operator settlement endpoint's own gate.
        var writable = new HashSet<string>(OrderStatusMachine.ManuallyDeliverableFrom, StringComparer.Ordinal)
        {
            Delivering,
        };

        var machineInbound = OrderStatusMachine.Transitions
            .Where(kv => kv.Value.Contains(Delivered))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        var observerInbound = OrderStatusTransitionObserver.AllowedTransitions
            .Where(kv => kv.Value.Contains(Delivered))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        machineInbound.Should().BeEquivalentTo(writable,
            "OrderStatusMachine.Transitions may list an X -> delivered edge only where production can " +
            "actually write 'delivered' from X: DeliveryService's post-claim outcome write (always from " +
            "'delivering'), or OrdersController.MarkDelivered (gated on ManuallyDeliverableFrom). An extra " +
            "from-state blesses a move nothing performs; a missing one would make IsAllowed reject a real " +
            "flow. If a new writer is genuinely added, widen its NAMED gate — not this list");

        observerInbound.Should().BeEquivalentTo(writable,
            "OrderStatusTransitionObserver.AllowedTransitions may treat an X -> delivered edge as expected " +
            "only where production can actually write 'delivered' from X. The observer is generous by " +
            "design, but generosity toward an IMPOSSIBLE edge buys nothing and costs the warning that would " +
            "otherwise fire the day something starts performing it");
    }

    // ── The canonical delivery-claim sets (#36) ──────────────────────────────────────────────
    // Five hand-written status gates (the dispatch claim's relational + InMemory copies, the retry
    // claim, HoldForBillingAsync's holdable set, and OrdersController.RetryDelivery's admission
    // guard) drifted apart four times on one branch, always silently: a status present in one list
    // and absent from a sibling makes the claim match 0 rows, which reads as "someone else has it"
    // and logs SUCCESS having sent nothing. These tests pin the sets AND their deliberate deltas.

    /// <summary>
    /// The 52c6431 regression, pinned. delivery_unconfirmed was added to RedeliverableFrom — so
    /// OrdersController.Redeliver began returning 202 for it — but not to the claim, so the claim
    /// matched 0 rows and DispatchArtifactAsync took its BENIGN no-op branch: the job logged SUCCESS
    /// having sent nothing and the order stayed parked while the operator was told it was sent.
    /// A code review caught it once. This assertion catches it every time.
    /// (Redeliver enqueues with requireAutoDeliver: false, so the OPERATOR set is the one that must
    /// contain it — an automatic activation deliberately cannot claim a park, see the delta test.)
    /// </summary>
    [Fact]
    public void RedeliverableFrom_IsSubsetOf_ClaimableForDispatchFrom()
        => OrderStatusMachine.RedeliverableFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForDispatchFrom,
            "every status OrdersController.Redeliver accepts (202 + enqueue DeliverOrderJob) must be a " +
            "status the operator dispatch claim can actually claim. A status in RedeliverableFrom but " +
            "not in ClaimableForDispatchFrom strands the order SILENTLY — 0 rows claimed reads as " +
            "'someone else has it', so there is no error, no retry, and no exception to notice");

    /// <summary>
    /// The #42 conditional member, pinned as an EXACT delta. The dispatch claim admits
    /// delivery_unconfirmed only for a human activation (requireAutoDeliver: false); an automatic
    /// activation (TransformOrderJob, the stranded-ready sweep, or a Hangfire refetch of either)
    /// must never claim a park — a dead activation refetched ~30 min later would otherwise re-send
    /// a PO the stuck sweep parked in the meantime, the exact duplicate the park exists to prevent.
    /// Flattening the two sets back into one unconditional set reintroduces that race
    /// (AutomaticParkClaimPostgresTests pins the behaviour; this pins the declaration).
    /// </summary>
    [Fact]
    public void OperatorAndAutomaticDispatchClaimSets_DifferExactlyBy_DeliveryUnconfirmed()
    {
        OrderStatusMachine.ClaimableForAutomaticDispatchFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForDispatchFrom,
            "an automatic activation may never claim a status a human's could not");
        OrderStatusMachine.ClaimableForDispatchFrom
            .Except(OrderStatusMachine.ClaimableForAutomaticDispatchFrom)
            .Should().BeEquivalentTo(new[] { DeliveryUnconfirmed },
                "the park is the ONE status only a human activation may claim — widening or narrowing " +
                "this delta is a product decision, so it must be a deliberate edit to this test");
    }

    /// <summary>
    /// The retry claim is deliberately the CONSERVATIVE set: it excludes delivery_unconfirmed so
    /// only a human "Send again" re-drives a parked order, never the automatic backoff queue. This
    /// pins the direction of that asymmetry — retry may narrow the dispatch set, never widen it.
    /// </summary>
    [Fact]
    public void DispatchAndRetryClaimSets_DifferExactlyBy_DeliveryUnconfirmed()
    {
        OrderStatusMachine.ClaimableForRetryFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForDispatchFrom,
            "RetryDeliveryAsync's claim must never accept a status the operator dispatch claim rejects");
        OrderStatusMachine.ClaimableForDispatchFrom
            .Except(OrderStatusMachine.ClaimableForRetryFrom)
            .Should().BeEquivalentTo(new[] { DeliveryUnconfirmed },
                "a parked (delivery_unconfirmed) order is re-driven ONLY by a human Send again — the " +
                "automatic backoff queue must never pick it up");
    }

    /// <summary>
    /// The automatic dispatch set and the retry set are the SAME product rule stated at two gates:
    /// no automatic path — first-deliver, sweep re-drive, backoff retry, or a Hangfire refetch of
    /// any of them — may claim a park; everything else send-ready is fair game for both. They are
    /// equal by that shared rule, not by coincidence, so diverging them must be a deliberate edit
    /// here with a new rule to justify it.
    /// </summary>
    [Fact]
    public void AutomaticDispatchAndRetryClaimSets_AreTheSameSet()
        => OrderStatusMachine.ClaimableForAutomaticDispatchFrom.Should().BeEquivalentTo(
            OrderStatusMachine.ClaimableForRetryFrom,
            "both encode 'an automatic activation never claims a park'; if they ever differ, one of " +
            "the two automatic legs has quietly acquired a claim the other refuses");

    /// <summary>
    /// The FOURTH list. HoldForBillingAsync sits downstream of DeliverOrderJob's billing gate, on
    /// the path "Send again" takes when the org has lapsed. Its drift is worse than the claim's: a
    /// refused status holds nothing, sends nothing, audits nothing, and never reaches
    /// delivery_held — so ReleaseBillingHeldOrdersAsync never rescues it on reactivation.
    /// Permanent strand, no self-heal. Hand-fixed once in 392b5a4; this assertion stops a repeat.
    /// </summary>
    [Fact]
    public void RedeliverableFrom_IsSubsetOf_HoldableForBillingFrom()
        => OrderStatusMachine.RedeliverableFrom.Should().BeSubsetOf(
            OrderStatusMachine.HoldableForBillingFrom,
            "a lapsed org's Send again reaches the billing gate BEFORE the claim. A status the hold " +
            "set refuses is held nowhere, sent nowhere and audited nowhere, and never becomes " +
            "delivery_held — so the reactivation re-drive never finds it. That strand is permanent, " +
            "unlike a lost claim");

    /// <summary>
    /// The FIFTH list. OrdersController.RetryDelivery admits only delivery_failed and mints a 400
    /// for anything else; the retry claim must be able to claim whatever it admits, or the 202 is a
    /// lie in the 52c6431 shape. Passes today — this is drift prevention, not a fix.
    /// </summary>
    [Fact]
    public void RetryableFrom_IsSubsetOf_ClaimableForRetryFrom()
        => OrderStatusMachine.RetryableFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForRetryFrom,
            "OrdersController.RetryDelivery returns 202 for every status in RetryableFrom and " +
            "enqueues RetryDeliveryJob; a status it admits but the retry claim rejects is the " +
            "52c6431 shape again");

    /// <summary>
    /// TransformOrderJob enqueues delivery straight after a successful transform, and
    /// StrandedReadyOrderDetectionService re-drives orders it finds parked there — both are
    /// AUTOMATIC activations (requireAutoDeliver: true), so it is the automatic set that must
    /// accept ready_to_deliver, not merely the operator one.
    /// </summary>
    [Fact]
    public void ReadyToDeliver_IsClaimableForAutomaticDispatch()
        => OrderStatusMachine.ClaimableForAutomaticDispatchFrom.Should().Contain(ReadyToDeliver,
            "TransformOrderJob and StrandedReadyOrderDetectionService both enqueue DeliverOrderJob " +
            "for a ready_to_deliver order with requireAutoDeliver: true; if the automatic claim " +
            "rejected it, every fresh auto-delivery would strand");

    /// <summary>
    /// OpsController.RequeueDelivery's guard set accepts delivery_dead_letter, which is NOT
    /// claimable. It is safe only because the requeue normalizes the row to delivery_failed and
    /// commits BEFORE enqueuing. The invariant therefore holds over the NORMALIZED TARGET, not over
    /// the guard set — assert the thing that is actually true, so this test does not quietly become
    /// a lie if the normalizing write moves. (The requeue enqueues EnqueueRedeliver —
    /// requireAutoDeliver: false — so the operator set is the one it claims through.)
    /// </summary>
    [Fact]
    public void OpsRequeue_NormalizedTarget_IsClaimableForDispatch()
        => OrderStatusMachine.ClaimableForDispatchFrom.Should().Contain(DeliveryFailed,
            "OpsController.RequeueDelivery rewrites the order to delivery_failed before enqueuing " +
            "DeliverOrderJob");

    // ── The dead-end invariant (WP-19) ────────────────────────────────────────────────────────

    /// <summary>
    /// The invariant, as a PURE function over a map, so the guard below can be proven to catch
    /// something rather than merely asserted to hold today.
    /// </summary>
    /// <returns>Every status that has no way out and was never declared finished.</returns>
    /// <remarks>
    /// Enumerates KEYS <b>and</b> TARGETS. Keys alone was the hole: a status reachable only as a
    /// target has no row, so <c>NextStatuses</c> returns the empty set and <c>IsTerminal</c> returns
    /// true for it — a dead end by every definition the product uses — and a key-only scan never
    /// looks at it. That is not an exotic shape; it is what a new status looks like the first time
    /// someone writes the edge INTO it and forgets to give it a row, which is the same omission that
    /// made <c>rejected_by_supplier</c> a dead end, arriving through the one door the guard did not
    /// watch. Pinned by <see cref="DeadEndInvariant_CatchesAStatusThatExistsOnlyAsATransitionTarget"/>.
    /// </remarks>
    private static IReadOnlyList<string> DeadEnds(
        IReadOnlyDictionary<string, IReadOnlySet<string>> transitions,
        IReadOnlySet<string> declaredTerminal)
    {
        var everyStatus = transitions.Keys
            .Concat(transitions.Values.SelectMany(targets => targets))
            .ToHashSet(StringComparer.Ordinal);

        return everyStatus
            .Where(status => !declaredTerminal.Contains(status))
            .Where(status => !transitions.TryGetValue(status, out var next) || next.Count == 0)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every <c>OrderStatusConstants</c> string, by REFLECTION.
    ///
    /// <para>The completeness check used to compare against a 16-name array typed out by hand, which
    /// is a second copy of a fact that lives in the code — so a 17th constant would have been absent
    /// from the machine AND absent from the list that was supposed to notice, and both halves would
    /// have stayed green. Reading the constants themselves removes the copy.</para>
    ///
    /// <para>Deliberately <c>const string</c> fields only: <c>FailureBucket</c> is a
    /// <c>static readonly</c> SET of statuses, not a status, and would otherwise be swept in.</para>
    /// </summary>
    private static IReadOnlyList<string> DeclaredStatusConstants(Type constantsType)
        => constantsType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <b>The regression guard for the whole defect class.</b> No status may have an empty edge set
    /// unless the product DECLARES the order finished there.
    ///
    /// <para><b>The defect it exists for.</b> <c>rejected_by_supplier</c> had no outgoing edges, was
    /// absent from <see cref="OrderStatusMachine.RedeliverableFrom"/>, and was where
    /// <c>DeliveryService</c> routed every 400–499. An expired API key (401), a moved endpoint (404)
    /// or a rate limit (429) therefore parked a perfectly deliverable PO in a state that NO control
    /// in the product could move — the operator's only recourse was a database edit. Every
    /// individual fact looked defensible in review; the hole was only visible by asking the question
    /// this test asks.</para>
    ///
    /// <para><b>Why <see cref="OrderStatusMachine.DeclaredTerminal"/> and not
    /// <c>IsTerminal</c>.</b> <c>IsTerminal</c> DERIVES terminality from an empty edge set, so
    /// asserting "every terminal status has no exits" is a tautology and asserting "every
    /// non-terminal status has exits" is vacuous — the invariant only bites against a set that is
    /// declared independently of the map. Adding a status to <c>DeclaredTerminal</c> to silence this
    /// test is therefore a visible product decision ("an order that reaches this is over"), which is
    /// exactly the review this defect escaped.</para>
    ///
    /// <para><b>Scope, honestly.</b> This proves a status has an EDGE, not that a human can reach
    /// it. An edge with no production writer is a different defect, caught by
    /// <see cref="EveryInboundDeliveredEdge_HasAProductionWriter"/> and by
    /// <see cref="Machine_Transitions_AreASupersetOf_ObserverAllowedTransitions"/>; the writers for
    /// the edges this test unblocked are named in
    /// <see cref="RejectedBySupplier_ExitsThroughACorrectionLoop_NotARedelivery"/>. Two weak
    /// guarantees over the same map are what make the pair strong: an edge no one writes fails one,
    /// a status with no edge fails the other.</para>
    /// </summary>
    [Fact]
    public void NoNonTerminalStatus_IsADeadEnd()
        => DeadEnds(OrderStatusMachine.Transitions, OrderStatusMachine.DeclaredTerminal)
            .Should().BeEmpty(
                "a status with no outgoing transitions that the product does NOT declare finished is " +
                "an order the operator cannot move: not redeliverable, not retryable, not resolvable, " +
                "fixable only by editing the database. If the listed status really is an ending, say " +
                "so in OrderStatusMachine.DeclaredTerminal and justify it there; otherwise give it the " +
                "exit the product owes it");

    /// <summary>
    /// Proof that the guard above BITES. A test that has only ever been green is indistinguishable
    /// from a test that cannot fail — and this one is guarding a defect that survived review by
    /// looking reasonable, so "it passes" is not the evidence that matters. The same
    /// <see cref="DeadEnds"/> function is run against a synthetic map carrying exactly the defect,
    /// and must report it.
    /// </summary>
    [Fact]
    public void DeadEndInvariant_CatchesASyntheticEmptyEdgeStatus()
    {
        var synthetic = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [Ready]              = new HashSet<string>(StringComparer.Ordinal) { Transforming },
            [Transforming]       = new HashSet<string>(StringComparer.Ordinal) { ReadyToDeliver },
            // ready_to_deliver needs a row of its own now that the invariant enumerates TARGETS as
            // well as keys — without one it would be a second (accidental) dead end and this test
            // would stop isolating the one it is about.
            [ReadyToDeliver]     = new HashSet<string>(StringComparer.Ordinal) { Ready },
            // The defect, reproduced: a failure state with nowhere to go that nobody declared final.
            [RejectedBySupplier] = new HashSet<string>(StringComparer.Ordinal),
            // A declared ending in the same map, so the assertion below shows the guard
            // DISTINGUISHES the two rather than flagging every empty set it sees.
            [Failed]             = new HashSet<string>(StringComparer.Ordinal),
        };

        DeadEnds(synthetic, OrderStatusMachine.DeclaredTerminal)
            .Should().Equal(new[] { RejectedBySupplier });
    }

    /// <summary>
    /// The hole the guard above still had: it only ever looked at the map's KEYS.
    ///
    /// <para>A status that appears only as a TARGET is never examined — and yet
    /// <see cref="OrderStatusMachine.NextStatuses"/> returns the empty set for it and
    /// <see cref="OrderStatusMachine.IsTerminal"/> returns <c>true</c>, so it is a real dead end by
    /// every definition the product uses. An order routed there has no way out and the invariant
    /// stays green. That is not a hypothetical shape: it is what happens the first time someone adds
    /// a status by writing the edge INTO it and forgetting to give it a row of its own — the exact
    /// omission that made <c>rejected_by_supplier</c> a dead end, arriving through the one door the
    /// guard does not watch.</para>
    ///
    /// <para>Reproduced with a target that has no key, so the assertion below fails on the
    /// key-only implementation and passes once the invariant enumerates targets too.</para>
    /// </summary>
    [Fact]
    public void DeadEndInvariant_CatchesAStatusThatExistsOnlyAsATransitionTarget()
    {
        const string quarantined = "quarantined";

        var synthetic = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // 'quarantined' is reachable — and has no row, so nothing can move an order out of it.
            [Ready]        = new HashSet<string>(StringComparer.Ordinal) { Transforming, quarantined },
            [Transforming] = new HashSet<string>(StringComparer.Ordinal) { ReadyToDeliver },
            [ReadyToDeliver] = new HashSet<string>(StringComparer.Ordinal) { Ready },
            // A declared ending, so the assertion shows the guard still DISTINGUISHES the two.
            [Failed]       = new HashSet<string>(StringComparer.Ordinal),
        };

        DeadEnds(synthetic, OrderStatusMachine.DeclaredTerminal)
            .Should().Equal(new[] { quarantined },
                "a status reachable only as a target has no outgoing edges at all — NextStatuses " +
                "returns the empty set and IsTerminal returns true — so it is a dead end the " +
                "product never declared, and examining only the map's keys cannot see it");
    }

    /// <summary>
    /// <see cref="OrderStatusMachine.DeclaredTerminal"/> is a product decision, pinned exactly so
    /// widening it is a deliberate edit and not a convenient way to quiet the invariant above.
    /// </summary>
    [Fact]
    public void DeclaredTerminal_IsExactlyFailed()
        => OrderStatusMachine.DeclaredTerminal.Should().BeEquivalentTo(new[] { Failed },
            "'failed' is a bad SOURCE FILE — it cannot be fixed in place, and recovery is a new " +
            "order row (ParseOrderJob refuses to re-drive it; OrdersController.Transform answers " +
            "'Upload a corrected file'). Nothing else in the pipeline is finished in that sense");

    /// <summary>
    /// The writers behind rejected_by_supplier's exit, named — because an edge with no writer is
    /// not an exit, it is the SAME defect wearing the invariant's clothes.
    ///
    /// <para>The exit is a CORRECTION loop, deliberately: a genuine business rejection means the
    /// supplier read the document and refused it, so the cure is a corrected document, never a
    /// re-send of the same bytes. Both doors are existing product surface:</para>
    /// <list type="bullet">
    ///   <item><c>OrderResolutionService.ResolveAsync</c> — recomputes <c>pending_review|ready</c>
    ///     with NO from-status guard, so correcting the line or header the supplier named already
    ///     moves a rejected order out. (This edge was live in production the whole time; the map
    ///     called the status terminal anyway, which is how the dead end read as intentional.)</item>
    ///   <item><c>OrderTransformService</c>'s claim — accepts
    ///     <see cref="OrderStatusMachine.ClaimableForTransformFrom"/>, which now includes
    ///     rejected_by_supplier for the same reason it includes transform_failed: it holds no
    ///     shippable artifact and re-transforming is the intended cure.</item>
    /// </list>
    /// <para>And the door that stays SHUT: no delivery claim set admits rejected_by_supplier, so
    /// nothing re-sends the refused artifact in place.</para>
    /// </summary>
    [Fact]
    public void RejectedBySupplier_ExitsThroughACorrectionLoop_NotARedelivery()
    {
        OrderStatusMachine.NextStatuses(RejectedBySupplier).Should().BeEquivalentTo(
            new[] { PendingReview, Ready, Transforming },
            "these three are exactly the correction-loop edges, and each has a named production " +
            "writer: resolve recomputes pending_review|ready, and the transform claim admits the status");

        OrderStatusMachine.ClaimableForTransformFrom.Should().Contain(RejectedBySupplier,
            "'Transform again' is the operator's one-click exit — the transform_failed precedent, " +
            "for the same reason: the order holds no artifact anyone may ship, and re-transforming " +
            "with the corrected mapping is the cure");

        foreach (var claim in new[]
                 {
                     OrderStatusMachine.ClaimableForDispatchFrom,
                     OrderStatusMachine.ClaimableForAutomaticDispatchFrom,
                     OrderStatusMachine.ClaimableForRetryFrom,
                     OrderStatusMachine.RedeliverableFrom,
                     OrderStatusMachine.RetryableFrom,
                 })
            claim.Should().NotContain(RejectedBySupplier,
                "the supplier read these bytes and refused them — re-sending them unchanged is the " +
                "one thing that certainly does not help, and it would hand the supplier a duplicate " +
                "of a document they already answered");
    }

    // ── The resolve recompute, generalised (the WP-19 follow-up) ──────────────────────────────

    /// <summary>
    /// Statuses no order is ever IN, and which a resolve therefore cannot be issued from.
    ///
    /// <para><c>pending_parse</c> is the entity's C# default
    /// (<c>PurchaseOrderEntity.Status = "pending_parse"</c>) and nothing else — every one of the
    /// four construction sites overrides it before the row is saved
    /// (<c>OrderIngestionService.cs:216</c> writes pending_review|ready, <c>:350</c> writes
    /// parsing, <c>:523</c> writes unrouted|pending_review|ready, <c>SampleOrderService.cs:123</c>
    /// writes parsing), and no writer anywhere assigns it afterwards — the stuck-order requeue
    /// deliberately re-writes <c>parsing</c> instead, and says why
    /// (<c>StuckOrderDetectionService.cs:90-101</c>: resetting to pending_parse made the
    /// re-enqueued job skip the parse and strand the order). Ask the code rather than trusting
    /// this list:</para>
    /// <code>grep -rn 'Status *= *"pending_parse"\|Status[ ,]*OrderStatusConstants\.PendingParse' --include=*.cs</code>
    ///
    /// <para>This rests on exactly the same fact as
    /// <c>Edge(PendingParse, PendingReview)</c> / <c>Edge(PendingParse, Ready)</c> in
    /// <see cref="KnownObserverOnlyEdges"/> — one fact, two consumers, so a writer appearing
    /// tomorrow invalidates both together rather than one of them quietly.</para>
    ///
    /// <para><b>"No order is ever in it" is a fact about TODAY, not a guarantee.</b> It holds only
    /// while every construction site remembers to override the default, and the cost of the first
    /// one that forgets is an order in a status with no sweeper, no alert and no UI bucket — lost
    /// silently and permanently, not merely late. <c>StuckOrderDetectionService</c> therefore
    /// sweeps <c>pending_parse</c> anyway: while this list is accurate the sweep matches no rows and
    /// costs nothing, and the moment it stops being accurate the leak surfaces as an ordinary
    /// requeue instead of a disappearance. Covering the status does NOT make it written, so this
    /// exclusion stands.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> StatusesNoOrderIsEverIn =
        new HashSet<string>(StringComparer.Ordinal) { PendingParse };

    /// <summary>
    /// The generalisation of the two <c>rejected_by_supplier</c> rows WP-19 added, and the guard
    /// that stops this from being re-discovered one from-state at a time.
    ///
    /// <para><b>The defect.</b> <c>OrderResolutionService.ResolveAsync</c> (:242) and
    /// <c>AcceptAiSuggestionsAsync</c> (:393) both end with
    /// <c>Status = Lines.Any(NeedsReview) ? pending_review : ready</c>. Neither carries a
    /// from-status guard; neither endpoint adds one (<c>OrdersController.Resolve</c> :483 validates
    /// the request BODY, <c>AcceptAiSuggestions</c> :1737 validates nothing). The only guard is
    /// <c>IsFinished</c> = <see cref="OrderStatusMachine.DeclaredTerminal"/>. So the from-states a
    /// resolve can be issued from are every status except <c>failed</c> and the never-written
    /// <see cref="StatusesNoOrderIsEverIn"/> — and both maps owed each of them TWO edges. WP-19
    /// reconciled the pair for <c>rejected_by_supplier</c> alone, because that status was a dead
    /// end and the edges were the way out; the writer is identical for the rest, which stayed
    /// marked impossible while performing in production. The observer's cost is concrete: it
    /// logged "Unexpected order status transition" on ordinary operator actions, the false positive
    /// its own doc says trains operators to ignore it.</para>
    ///
    /// <para><b>Why it asserts over BOTH maps.</b>
    /// <see cref="Machine_Transitions_AreASupersetOf_ObserverAllowedTransitions"/> is
    /// one-directional by design (observer ⊆ machine), so a MACHINE edge absent from the observer
    /// is invisible to it — which is how <c>ready_to_deliver → ready</c> and
    /// <c>delivery_dead_letter → ready</c> stayed missing from the observer even though the machine
    /// allowed them and <c>OrderMappingOverrideService.IsPastReady</c> (:111) performs both as
    /// tracked MV-1 writes. Checking the two maps against the CALL SITE, rather than against each
    /// other, is what sees that direction.</para>
    ///
    /// <para><b>Derived, not transcribed</b> — the from-state list comes from
    /// <see cref="OrderStatusMachine.AllStatuses"/>, so a status added tomorrow is covered without
    /// anyone editing this test. That is the difference between fixing eleven edges and closing the
    /// class; the [InlineData] rows above are the readable enumeration of the same fact.</para>
    ///
    /// <para><b>WP-23 — the third exclusion.</b> This test's own closing message asked the next
    /// packet to "gate the endpoint and say so … with the call-site evidence" for any from-state a
    /// resolve genuinely cannot be issued from. WP-23 did exactly that for the two holes c61fe30
    /// named and WP-23a for the two machine-owned steps (<c>parsing</c>, <c>transforming</c>), so
    /// <see cref="OrderStatusMachine.ResolveHeldFrom"/> joins DeclaredTerminal and
    /// StatusesNoOrderIsEverIn as a reason a status is NOT in this list. It is deliberately a THIRD
    /// exclusion rather than an addition to either of those: the order genuinely IS in the status
    /// (unlike StatusesNoOrderIsEverIn) and is not finished (unlike DeclaredTerminal) — it is simply
    /// refused this one operation, at the endpoint, for now. <b>The edges themselves are NOT
    /// pruned</b>, and <see cref="EveryResolveHeldStatus_KeepsBothRecomputeEdges"/> asserts the same
    /// two edges for exactly the excluded statuses, so this narrowing costs zero coverage.</para>
    /// </summary>
    [Fact]
    public void EveryStatusAResolveCanBeIssuedFrom_HasBothRecomputeEdges()
    {
        var resolvableFrom = OrderStatusMachine.AllStatuses
            .Except(OrderStatusMachine.DeclaredTerminal, StringComparer.Ordinal)
            .Except(StatusesNoOrderIsEverIn, StringComparer.Ordinal)
            .Except(OrderStatusMachine.ResolveHeldFrom, StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // The bound is a VACUITY guard, not a pin on the count: it exists so a machine whose status
        // list shrank — or whose three exclusion sets grew to swallow it — fails loudly instead of
        // asserting over an empty list. It is therefore EXPECTED to move when an exclusion set is
        // widened deliberately, and WP-23a is the first time that happened: adding parsing +
        // transforming to ResolveHeldFrom took this list from 12 to exactly 10 (16 statuses − 1
        // DeclaredTerminal − 1 StatusesNoOrderIsEverIn − 4 ResolveHeldFrom), which turned `> 10` red
        // on a widening that is correct. Lowered to 5 rather than re-pinned at 10 so the next
        // deliberate widening does not have to edit this line to stay honest; 5 is still far above
        // the near-empty case this is here to catch.
        resolvableFrom.Should().HaveCountGreaterThan(5,
            "if the machine's status list ever shrinks to nothing — or the three exclusion sets grow " +
            "to cover nearly all of it — this assertion must fail loudly rather than pass over an " +
            "empty or near-empty list");

        var missing = new List<string>();
        foreach (var from in resolvableFrom)
        foreach (var to in new[] { PendingReview, Ready })
        {
            // A self-transition is not an edge: the recompute writing the status the order is
            // already in is a no-op the observer treats as expected and the machine need not list.
            if (string.Equals(from, to, StringComparison.Ordinal)) continue;

            if (!OrderStatusMachine.IsAllowed(from, to))
                missing.Add($"machine: {Edge(from, to)}");
            if (!OrderStatusTransitionObserver.IsExpected(from, to))
                missing.Add($"observer: {Edge(from, to)}");
        }

        missing.Should().BeEmpty(
            "a resolve can be issued from every one of these statuses — the recompute in " +
            "OrderResolutionService.ResolveAsync/AcceptAiSuggestionsAsync has no from-status guard " +
            "and neither endpoint adds one — so both maps must list pending_review AND ready as " +
            "successors. A missing MACHINE edge makes IsAllowed reject a live production write; a " +
            "missing OBSERVER edge makes the log-only observer WARN on a legitimate operator " +
            "action, which is the false positive that teaches operators to ignore it. If a resolve " +
            "genuinely cannot be issued from one of these, gate the endpoint and say so in " +
            "ResolveHeldFrom (or StatusesNoOrderIsEverIn, or DeclaredTerminal) with the call-site " +
            "evidence — do not leave the map contradicting the writer");
    }

    /// <summary>
    /// WP-23, and the guard against the pruning this packet makes tempting (TRAP 1).
    ///
    /// <para><see cref="OrderStatusMachine.ResolveHeldFrom"/> stops the two RECOMPUTE ENDPOINTS from
    /// being issued against <c>unrouted</c> and <c>delivering</c> (and, after WP-23a, <c>parsing</c>
    /// and <c>transforming</c> — which is precisely why this test derives its rows from the set
    /// instead of listing them). It says nothing about whether the
    /// transition is possible, and the next reader will be tempted to conclude that it does: "no
    /// writer performs unrouted → ready any more, so the edge is dead — prune it." That is the
    /// reasoning c61fe30 refused, and it is how <c>rejected_by_supplier</c> became an unintended dead
    /// end in the first place. An edge is removed on evidence about WRITERS, never on the existence
    /// of a guard on one caller: the machine is documented as a superset of what the code performs,
    /// so a legal-but-unperformed edge is correct by construction, and the guard is an endpoint
    /// admission rule that a future writer (or a lifted hold) is free to make live again.</para>
    ///
    /// <para>Asserted over BOTH maps and over the DERIVED set, so widening the hold tomorrow pulls
    /// the new status into this protection automatically. Deliberately overlaps
    /// <see cref="EveryStatusAResolveCanBeIssuedFrom_HasBothRecomputeEdges"/>'s former range: adding
    /// the ResolveHeldFrom exclusion there must not quietly drop these edges from coverage.</para>
    /// </summary>
    [Fact]
    public void EveryResolveHeldStatus_KeepsBothRecomputeEdges()
    {
        OrderStatusMachine.ResolveHeldFrom.Should().NotBeEmpty(
            "an empty hold would make this assertion pass over nothing");

        var missing = new List<string>();
        foreach (var from in OrderStatusMachine.ResolveHeldFrom)
        foreach (var to in new[] { PendingReview, Ready })
        {
            if (string.Equals(from, to, StringComparison.Ordinal)) continue;

            if (!OrderStatusMachine.IsAllowed(from, to))
                missing.Add($"machine: {Edge(from, to)}");
            if (!OrderStatusTransitionObserver.IsExpected(from, to))
                missing.Add($"observer: {Edge(from, to)}");
        }

        missing.Should().BeEmpty(
            "c61fe30 added these edges to both status maps deliberately, because the write was real " +
            "and the maps were wrong. WP-23's endpoint guard changes who may ISSUE the write; it " +
            "does not make the transition impossible, and pruning the edges on the strength of a " +
            "guard is the circular reasoning DeclaredTerminal exists to break");
    }

    /// <summary>
    /// WP-23 — the hold is exactly the statuses this guard refuses, and no more. Exact rather than a
    /// superset for the same reason as
    /// <see cref="RedeliverableFrom_IsExactlyTheThreeOperatorSendableStatuses"/>: every status added
    /// here takes a control away from operators, so widening must be a deliberate edit with a
    /// product argument, never a side effect of a refactor.
    ///
    /// <para><b>This asserts a DECISION, not a survey.</b> It was first written as
    /// <c>…IsExactlyTheTwoHolesTheRecomputeDestroys</c>, and an adversarial review showed that name
    /// and its justification were simply false: <c>parsing</c> is a third from-state where the
    /// recompute destroys something, and worse than the first two rather than milder. Naming the set
    /// after a claim about the world meant the test asserted that claim (R5) — and the claim was
    /// wrong. It asserts what the product decided instead, which is why widening it is an edit HERE
    /// and not a side effect anywhere else.</para>
    ///
    /// <para><b>WP-23a widened it, deliberately.</b> <c>parsing</c> and <c>transforming</c> are in.
    /// The evidence for each is on <see cref="OrderStatusMachine.ResolveHeldFrom"/>; the two facts
    /// that decided it are that <c>parsing</c> is the window <c>AssignSupplier</c> flips an
    /// <c>unrouted</c> order INTO (<c>OrdersController.cs:747-756</c>), so refusing <c>unrouted</c>
    /// alone left this set's own lockout reachable one step later, and that <c>transforming</c>'s
    /// COMPLETION write is unclaimed (<c>OrderTransformService.cs:733</c>) while
    /// <c>TransformOrderJob.cs:120</c> enqueues delivery straight after it — the only one of the four
    /// where the correction is not just lost but a stale document is sent. The set is still not
    /// claimed to be exhaustive.</para>
    /// </summary>
    [Fact]
    public void ResolveHeldFrom_IsExactlyTheStatusesThisGuardRefuses()
        => OrderStatusMachine.ResolveHeldFrom.Should().BeEquivalentTo(
            new[] { Unrouted, Delivering, Parsing, Transforming },
            "each of these four takes a control away from operators, so each must be a deliberate " +
            "edit here with call-site evidence on ResolveHeldFrom. unrouted: the recompute clears " +
            "the routing hold with SupplierId still null, after which AssignSupplier's atomic " +
            "`Status == Unrouted` claim answers 409 forever and no control in the product can route " +
            "the order. delivering: a row SITS in that status, so the recompute overwrites a live " +
            "dispatch claim whose outcome write then lands on top of the correction. parsing: both " +
            "parse-persist claims require `Status == Parsing` and Fail on 0 rows BEFORE inserting " +
            "the lines, the retry then no-ops and the job lands GREEN, and AssignSupplier flips an " +
            "unrouted order INTO parsing — so leaving it open left the unrouted lockout reachable " +
            "through the re-parse. transforming: the transform CLAIM is atomic but its completion " +
            "write is not, so it lands over the correction and ships an artifact built before it. " +
            "This is still NOT claimed to be the complete list of destructive from-states: a fifth " +
            "arrives by naming the writer and the line, never as a refactor's side effect");

    /// <summary>
    /// WP-23 — <c>ResolveHoldMessage</c>'s <c>switch</c> arms are the ONE place a held status is
    /// spelled out a second time, so they are the one place the set can silently drift from its
    /// consumer. A status removed from <see cref="OrderStatusMachine.ResolveHeldFrom"/> whose arm is
    /// left behind fails nothing on its own: the arm is simply never reached, and the next reader
    /// finds a sentence implying a refusal the product no longer performs.
    ///
    /// <para>So the complement is asserted: every status the guard does NOT refuse must fall through
    /// to the generic arm. Derived from <see cref="OrderStatusMachine.AllStatuses"/>, so this needs
    /// no maintenance when a status is added on either side.</para>
    /// </summary>
    [Fact]
    public void ResolveHoldMessage_HasNoArmForAStatusTheGuardDoesNotRefuse()
    {
        var fallback = OrderStatusMachine.ResolveHoldMessage("a-status-the-machine-has-never-heard-of");

        var stray = OrderStatusMachine.AllStatuses
            .Except(OrderStatusMachine.ResolveHeldFrom, StringComparer.Ordinal)
            .Where(s => !string.Equals(OrderStatusMachine.ResolveHoldMessage(s), fallback, StringComparison.Ordinal))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        stray.Should().BeEmpty(
            "a bespoke refusal sentence for a status the guard admits is unreachable code that reads " +
            "like a rule — the second hand-written enumeration of the held set, and the exact drift " +
            "the five delivery-claim literals were centralised to prevent");
    }

    /// <summary>
    /// WP-23 — the two refusals on the resolve path stay separate concepts.
    ///
    /// <para><see cref="OrderStatusMachine.DeclaredTerminal"/> is a permanent verdict about the ORDER
    /// ("its source file could not be read; there is nothing here to correct"), enforced in
    /// <c>OrderResolutionService.IsFinished</c> and answered with a 400.
    /// <see cref="OrderStatusMachine.ResolveHeldFrom"/> is a temporary verdict about the MOMENT ("do
    /// X first, then correct it"), enforced at the endpoint and answered with a 409. Merging them
    /// would give one of the two cases the other's status code and the other's sentence — and it is
    /// the sentence that decides whether the operator's next action is right.</para>
    /// </summary>
    [Fact]
    public void ResolveHeldFrom_AndDeclaredTerminal_AreDisjoint()
        => OrderStatusMachine.ResolveHeldFrom
            .Intersect(OrderStatusMachine.DeclaredTerminal, StringComparer.Ordinal)
            .Should().BeEmpty(
                "a status in both sets would be refused twice with two different codes and two " +
                "different sentences, and which one an operator sees would depend on call order");

    /// <summary>
    /// The transform claim set, pinned. It is written TWICE in OrderTransformService (a relational
    /// <c>ExecuteUpdateAsync</c> predicate and its EF-InMemory emulation) — the same
    /// two-copies-of-one-rule shape that made the five delivery-claim lists drift apart four times,
    /// each time silently.
    /// </summary>
    [Fact]
    public void ClaimableForTransformFrom_IsExactlyTheFourReTransformableStatuses()
        => OrderStatusMachine.ClaimableForTransformFrom.Should().BeEquivalentTo(
            new[] { Ready, Transforming, TransformFailed, RejectedBySupplier },
            "ready is the normal entry; transforming lets a crashed attempt re-run; transform_failed " +
            "and rejected_by_supplier are the two recovery doors — both hold no shippable artifact, " +
            "so re-transforming can neither duplicate a send nor ship stale content. Any status past " +
            "transform (ready_to_deliver and beyond) must stay OUT: claiming one would upload a " +
            "duplicate artifact and re-enqueue delivery, double-sending the PO");

    /// <summary>
    /// The transform leg's own 52c6431 guard, and the SIXTH copy of this rule that WP-19 found:
    /// <c>OrdersController.Transform</c> carried its own <c>ready|transform_failed</c> literal under
    /// a comment reading "kept in lockstep with the transform claim in OrderTransformService" — the
    /// sentence a second hand-written copy writes just before it drifts. The endpoint commits
    /// <c>transforming</c> and enqueues; the service then re-claims. A status the endpoint admits
    /// but the service claim refuses leaves the order in <c>transforming</c> with no artifact, and a
    /// job that logged a benign "already in flight or done" skip.
    /// </summary>
    [Fact]
    public void TransformableFrom_IsSubsetOf_ClaimableForTransformFrom()
        => OrderStatusMachine.TransformableFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForTransformFrom,
            "every status the transform ENDPOINT claims must be one the transform SERVICE can " +
            "re-claim, or the 202 is a lie and the order strands mid-transform");

    /// <summary>
    /// …and the delta is exactly <c>transforming</c>, which is a product decision: the SERVICE
    /// admits it so a Hangfire retry can re-run a crashed attempt, while the ENDPOINT answers a
    /// second click with 202 "already in progress" rather than racing a competing job.
    /// </summary>
    [Fact]
    public void TransformEndpointAndServiceClaims_DifferExactlyBy_Transforming()
        => OrderStatusMachine.ClaimableForTransformFrom
            .Except(OrderStatusMachine.TransformableFrom)
            .Should().BeEquivalentTo(new[] { Transforming },
                "'transforming' is the ONE status only the service claim may take (crash re-run); " +
                "widening or narrowing this delta changes whether a double-click can race a job");

    /// <summary>
    /// The OTHER transform delta, and the one that costs an operator their finding if it collapses:
    /// "may I START a transform from here?" (<see cref="OrderStatusMachine.ClaimableForTransformFrom"/>)
    /// versus "may I OVERWRITE this with a failure?"
    /// (<see cref="OrderStatusMachine.TransformFailableFrom"/>).
    ///
    /// <para><c>OrderTransformService.FailTransformFromClaimableAsync</c> first shipped guarding on
    /// the CLAIM's set, under the one-sentence rule "if we could have claimed it, we may fail it".
    /// That rule is wrong by exactly one status. <c>rejected_by_supplier</c> is a post-success
    /// OPERATOR VERDICT, and it is reachable mid-transform:
    /// <c>OrderResolutionService.MarkRejectedAsync</c> carries no from-status guard, and
    /// <c>transforming → rejected_by_supplier</c> is a documented edge in the map. So an operator
    /// records the supplier's refusal while a transform is in flight, that transform throws, and its
    /// catch stamps <c>transform_failed</c> over the verdict — replacing "the supplier refused this
    /// document" with "something went wrong". The status is load-bearing rather than decorative: it
    /// is the one status no delivery claim set admits, and it feeds the supplier acceptance-rate
    /// figures.</para>
    ///
    /// <para>Re-TRANSFORMING a rejected order stays correct — that is how a corrected document is
    /// produced — so the delta runs in one direction only, and both directions are asserted here. A
    /// future edit to either set has to confront this test rather than silently erase the
    /// distinction.</para>
    /// </summary>
    [Fact]
    public void TransformFailableFrom_IsClaimableMinus_RejectedBySupplier()
    {
        OrderStatusMachine.ClaimableForTransformFrom
            .Except(OrderStatusMachine.TransformFailableFrom, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new[] { RejectedBySupplier },
                "starting a transform from a supplier rejection is the correction workflow, but " +
                "overwriting that rejection with transform_failed destroys a human's finding");

        OrderStatusMachine.TransformFailableFrom
            .Except(OrderStatusMachine.ClaimableForTransformFrom, StringComparer.Ordinal)
            .Should().BeEmpty(
                "a status a transform failure may overwrite but a transform could never have " +
                "claimed is a status the failure write would stamp without ever owning the row");
    }

    /// <summary>
    /// The parse leg's answer to "may I OVERWRITE this with a failure?", pinned exactly — the
    /// analogue of <see cref="TransformFailableFrom_IsClaimableMinus_RejectedBySupplier"/> one leg
    /// earlier.
    ///
    /// <para><c>OrderIngestionService.SetOrderFailedAsync</c> stamps terminal <c>failed</c> from
    /// three sites, all of them minutes below <c>ParseStoredFileAsync</c>'s single top-of-method
    /// status read — after a storage download, a format detection, and on the PDF/XLSX paths a
    /// network call to an LLM extractor. It used to carry no from-status test at all, so a parse
    /// that lost the race overwrote a CONCURRENT PARSE'S SUCCESS (the stuck sweep re-drives a
    /// stalled order by keeping it in <c>parsing</c> and enqueuing a fresh job, so two live parse
    /// jobs is the recovery path working) or an operator's <c>rejected_by_supplier</c> VERDICT. And
    /// because <c>failed</c> is the sole <see cref="OrderStatusMachine.DeclaredTerminal"/> member,
    /// <c>OrderResolutionService.IsFinished</c> then refused every correction — a terminal lie with
    /// no operator exit.</para>
    ///
    /// <para><b>Exactly <c>parsing</c>, and the exclusions are the content.</b> A parse only ever
    /// RUNS from <c>parsing</c>, so a row in any other status is one this failure is not ABOUT.
    /// <c>pending_parse</c> is the tempting addition and is deliberately out: a row still in it has
    /// had no parse started on it, and admitting it would let a stale attempt stamp terminal failure
    /// on an order <c>StuckOrderDetectionService</c> is about to re-drive.</para>
    ///
    /// <para><b>Invariant:</b> every member must be able to reach <c>failed</c> in
    /// <see cref="OrderStatusMachine.Transitions"/> — a claim admitting a status the map says cannot
    /// fail is the two-maps-disagree shape this file exists to catch.</para>
    /// </summary>
    [Fact]
    public void ParseFailableFrom_IsExactlyParsing()
    {
        OrderStatusMachine.ParseFailableFrom.Should().BeEquivalentTo(new[] { Parsing },
            "a parse only ever runs from 'parsing', so every other status is one the failure is " +
            "not about — and 'failed' is terminal, so a mistaken overwrite is unrecoverable");

        OrderStatusMachine.ParseFailableFrom.Should().NotContain(PendingParse,
            "a row still in pending_parse has had no parse started on it; admitting it would let a " +
            "stale attempt stamp terminal failure on an order the stuck sweep is about to re-drive");

        foreach (var from in OrderStatusMachine.ParseFailableFrom)
            OrderStatusMachine.IsAllowed(from, Failed).Should().BeTrue(
                $"the parse-failure claim admits {from}, so the machine must list failed as one of " +
                "its successors — otherwise the map calls a live production write impossible");
    }

    /// <summary>
    /// Completeness: every <c>OrderStatusConstants</c> string is a node in the machine, so a future
    /// status cannot be silently absent from transition reasoning.
    ///
    /// <para><b>Derived, not transcribed.</b> This used to compare against a hand-typed 16-name
    /// array — a second copy of the constants, and therefore a backstop that could only ever notice
    /// statuses someone had already remembered to add to it. A 17th constant would slip past the
    /// machine and past its own completeness check at the same time, which is precisely the case the
    /// check exists for. Both sides now come from the code.</para>
    /// </summary>
    [Fact]
    public void Machine_KnowsEveryDeclaredStatusConstant()
    {
        var declared = DeclaredStatusConstants(typeof(OrderStatusConstants));

        declared.Should().HaveCountGreaterThan(10,
            "if reflection ever stops finding the constants this assertion must fail loudly rather " +
            "than pass over an empty list");

        OrderStatusMachine.Transitions.Keys.Should().Contain(declared,
            "a declared status with no row in the machine has no transitions at all — NextStatuses " +
            "returns the empty set and IsTerminal returns true for it, so it is a dead end nobody " +
            "chose. Give it a row (and an exit), or declare it terminal on purpose");
    }

    /// <summary>
    /// Proof that the reflection above WOULD see a new status — the same discipline
    /// <see cref="DeadEndInvariant_CatchesASyntheticEmptyEdgeStatus"/> applies to the dead-end
    /// guard. A hand-typed list looked identical to this one on a green run; the difference only
    /// shows when a constant is added, so the test manufactures that moment instead of waiting for
    /// it.
    /// </summary>
    [Fact]
    public void ReflectionOverStatusConstants_SeesAStatusNobodyRememberedToList()
    {
        var found = DeclaredStatusConstants(typeof(SyntheticStatusConstants));

        found.Should().Contain("quarantined",
            "the whole point of deriving the list is that a constant added tomorrow is discovered " +
            "without anyone editing this test");
        found.Should().Contain(new[] { "ready", "failed" });
        found.Should().NotContain("not_a_status",
            "static readonly fields are not status constants — only const strings are");
    }

    // ── MV-1: the mapping-edit staleness reset ──────────────────────────────────────────────────
    //
    // OrderMappingOverrideService.UpsertAsync resets an order to 'ready' — forcing a re-transform
    // before the next send — when the order already holds an artifact some path could ship as-is.
    // The membership list lived in that service as a hand-written `is … or …` chain, and
    // 'delivery_held' was missing from it while six siblings were present, so a correction typed
    // during a billing hold was accepted, discarded, and the pre-edit document went to the supplier
    // when billing was restored.
    //
    // A chain cannot be asked "is every status accounted for?", which is the only question that
    // would have caught it. These tests ask it: every status in the machine must land in exactly one
    // of the three buckets below, so a status added tomorrow fails the build until someone decides
    // which it is. Deriving the set instead was considered and rejected — see the doc on
    // OrderStatusMachine.MappingEditInvalidatesArtifactFrom.

    /// <summary>
    /// Statuses that CAN hold a dispatchable artifact, ACCEPT a mapping edit, and are still
    /// deliberately NOT reset — i.e. a known open stale-artifact path. Recorded here, with the
    /// reason, rather than left to look like an oversight.
    ///
    /// <para><b>EMPTY, and pinned empty.</b> <c>delivering</c> was the sole member: MV-1 named it
    /// rather than closing it, because a reset is not the cure there (the artifact has already been
    /// handed to the dispatcher, so writing <c>ready</c> un-sends nothing and lands over a live
    /// dispatch claim last-writer-wins) and an endpoint refusal is a product decision MV-1 was not
    /// given. MV-2 made that decision: <c>delivering</c> moved to
    /// <see cref="OrderStatusMachine.MappingEditRefusedFrom"/>, where the edit is refused with 409
    /// before it can be stored and discarded. It is no longer a residual because the gap is closed,
    /// not because anyone stopped counting it.</para>
    ///
    /// <para>The bucket stays because its job was never "hold <c>delivering</c>" — it was to make a
    /// deliberately-unfixed stale-artifact path cost an edit here and an argument in the doc above.
    /// A new member is the <c>delivery_held</c> bug being accepted rather than fixed, and
    /// <c>MappingEditKnownResidual_IsEmpty</c> is what forces that to be said out loud.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> MappingEditKnownResidual =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Statuses that hold NO artifact anyone can ship in place, so a mapping edit has nothing to
    /// invalidate. Each entry carries the fact that makes it true — not "it feels upstream".
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> MappingEditArtifactFree =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PendingParse]   = "before parse — the order has no lines, let alone an artifact",
            [Parsing]        = "the parse has not produced a canonical order yet",
            [Unrouted]       = "parked awaiting a supplier; nothing has been transformed",
            [PendingReview]  = "pre-transform review; an artifact is only built after 'ready'",
            [Ready]          = "the reset TARGET — the next send re-transforms from here",
            [Failed]         = "a bad source file; recovery is a new order row, and no artifact exists",
            [TransformFailed] = "the transform produced no artifact (OrderTransformService fails before upload)",
            // The one that needs the most care: it DOES hold an artifact, and it is still artifact-free
            // for this question because no path can ship that artifact IN PLACE.
            [RejectedBySupplier] =
                "no delivery claim set admits it — not dispatch, not retry, not the ops requeue, not " +
                "Redeliver — so the refused artifact cannot be re-shipped. Its only exits (resolve, or " +
                "a re-transform via ClaimableForTransformFrom) already produce a fresh document",
        };

    /// <summary>Statuses in none of the four buckets — the thing that must always be empty.</summary>
    private static IReadOnlyCollection<string> UnclassifiedForMappingEdit(IEnumerable<string> statuses) =>
        statuses
            .Where(s => !OrderStatusMachine.MappingEditRefusedFrom.Contains(s)
                     && !OrderStatusMachine.MappingEditInvalidatesArtifactFrom.Contains(s)
                     && !MappingEditKnownResidual.Contains(s)
                     && !MappingEditArtifactFree.ContainsKey(s))
            .ToList();

    /// <summary>
    /// THE guard. Every status the machine knows is classified for the mapping-edit reset: refused
    /// at the endpoint, reset, known residual, or provably artifact-free. A status that is none of
    /// those is one nobody has decided about — which is exactly what <c>delivery_held</c> was.
    ///
    /// <para>MV-2 added the first bucket. The four answer ONE question — "what happens to a mapping
    /// edit made in this status?" — and refusal supersedes the rest: an edit that never reaches
    /// <c>UpsertAsync</c> has no artifact to invalidate, which is why a refused status must NOT also
    /// sit in the reset set. That is not pedantry about set membership: <c>transforming</c> sat in
    /// both meanings at once (refused nowhere, reset here, reset provably overwritten by
    /// <c>OrderTransformService</c>'s untokened completion write) and the reset read as protection
    /// while providing none.</para>
    /// </summary>
    [Fact]
    public void EveryStatus_IsClassifiedForTheMappingEditReset()
    {
        var all = OrderStatusMachine.AllStatuses;

        // Anti-vacuity: an empty (or gutted) status list must FAIL here rather than sweep nothing
        // and report success. The buckets are asserted to partition it, so if `all` were empty every
        // assertion below would pass over no work at all.
        all.Should().HaveCountGreaterThan(10,
            "the sweep must examine every real status — a shrunken list means the guard stopped looking");

        UnclassifiedForMappingEdit(all).Should().BeEmpty(
            "every status must be decided: either the edit is refused at the endpoint " +
            "(MappingEditRefusedFrom), or it invalidates the artifact " +
            "(MappingEditInvalidatesArtifactFrom), or it is a named residual, or the status provably " +
            "holds no artifact anyone can ship in place. An unclassified status is the delivery_held bug");

        // Partition, not merely cover: a status in two buckets means two contradictory decisions.
        OrderStatusMachine.MappingEditRefusedFrom
            .Should().NotIntersectWith(OrderStatusMachine.MappingEditInvalidatesArtifactFrom,
                "a refused edit never reaches UpsertAsync, so it has nothing to reset — claiming both " +
                "is how 'transforming' kept a reset that OrderTransformService's completion write " +
                "silently overwrote")
            .And.NotIntersectWith(MappingEditKnownResidual)
            .And.NotIntersectWith(MappingEditArtifactFree.Keys);
        OrderStatusMachine.MappingEditInvalidatesArtifactFrom
            .Should().NotIntersectWith(MappingEditKnownResidual)
            .And.NotIntersectWith(MappingEditArtifactFree.Keys);
        MappingEditKnownResidual.Should().NotIntersectWith(MappingEditArtifactFree.Keys);

        (OrderStatusMachine.MappingEditRefusedFrom.Count
         + OrderStatusMachine.MappingEditInvalidatesArtifactFrom.Count
         + MappingEditKnownResidual.Count
         + MappingEditArtifactFree.Count)
            .Should().Be(all.Count, "the four buckets must tile the machine exactly, with no status counted twice");
    }

    /// <summary>
    /// Proof the guard above is not vacuous: fed a status list containing one nobody classified, it
    /// reports it. A green partition on today's statuses looks identical whether the check works or
    /// not — the difference only shows the day a status is added, so this manufactures that day.
    /// </summary>
    [Fact]
    public void MappingEditClassification_CatchesAStatusNobodyDecidedAbout()
        => UnclassifiedForMappingEdit(OrderStatusMachine.AllStatuses.Append("quarantined"))
            .Should().ContainSingle().Which.Should().Be("quarantined",
                "a status added tomorrow must fail this suite until someone says whether a mapping " +
                "edit invalidates its artifact");

    /// <summary>
    /// The half a DERIVATION can prove soundly, kept as a check rather than promoted to the
    /// definition: every status a delivery path can claim or admit holds a shippable artifact, so
    /// every one of them must be reset by a mapping edit. It is a necessary condition and not a
    /// sufficient one — it does NOT contain <c>delivery_held</c> (the release rewrites the order to
    /// <c>ready_to_deliver</c> before enqueuing, so no claim set ever names the held status), which
    /// is precisely why the reset set is enumerated and guarded for totality instead of derived from
    /// these.
    /// </summary>
    [Fact]
    public void MappingEditReset_CoversEveryStatusADeliveryPathCanClaimOrAdmit()
    {
        var claimable = new[]
            {
                OrderStatusMachine.ClaimableForDispatchFrom,
                OrderStatusMachine.ClaimableForAutomaticDispatchFrom,
                OrderStatusMachine.ClaimableForRetryFrom,
                OrderStatusMachine.RedeliverableFrom,
                OrderStatusMachine.RetryableFrom,
                OrderStatusMachine.RequeueableFrom,
                OrderStatusMachine.HoldableForBillingFrom,
            }
            .SelectMany(s => s)
            .ToHashSet(StringComparer.Ordinal);

        claimable.Should().HaveCountGreaterThan(3,
            "if the claim sets ever read empty this must fail rather than assert over nothing");

        OrderStatusMachine.MappingEditInvalidatesArtifactFrom.Should().Contain(claimable,
            "a status a delivery path can claim ships the STORED artifact without re-transforming, " +
            "so a mapping edit made in it must invalidate that artifact first");

        OrderStatusMachine.MappingEditInvalidatesArtifactFrom.Should().Contain(DeliveryHeld)
            .And.NotBeSubsetOf(claimable,
                "delivery_held is the counter-example that makes the derivation unsafe: it is in NO " +
                "claim set (ReleaseBillingHeldOrdersAsync rewrites it to ready_to_deliver first) and " +
                "it must still be reset");
    }

    /// <summary>
    /// The supplier-level reset set is the per-order one MINUS exactly <c>delivered</c>, asserted in
    /// both directions. The subtraction is the whole product decision behind
    /// <see cref="OrderStatusMachine.SupplierMappingEditInvalidatesArtifactFrom"/> — a supplier-wide
    /// save must not resurrect every order that supplier ever completed — and stating it as a
    /// difference rather than as a second literal is what stops the two from drifting: widening
    /// either set without deciding about the other fails here.
    ///
    /// <para>Totality comes free from this: the per-order set is partitioned against
    /// <see cref="OrderStatusMachine.AllStatuses"/> by
    /// <see cref="EveryStatus_IsClassifiedForTheMappingEditReset"/>, so a set pinned as that one minus
    /// a named member is classified for every status too. A status added tomorrow still fails the
    /// build there, once, rather than in two places that could disagree.</para>
    /// </summary>
    [Fact]
    public void SupplierMappingEditReset_DiffersFromThePerOrderResetByExactlyDelivered()
    {
        OrderStatusMachine.SupplierMappingEditInvalidatesArtifactFrom
            .Should().HaveCountGreaterThan(3,
                "if this set ever reads empty the difference assertion below would pass over nothing")
            .And.BeSubsetOf(OrderStatusMachine.MappingEditInvalidatesArtifactFrom,
                "a supplier-level mapping save invalidates a STRICT subset of what a per-order edit " +
                "does — it may never reset a status the per-order path decided was artifact-free");

        OrderStatusMachine.MappingEditInvalidatesArtifactFrom
            .Except(OrderStatusMachine.SupplierMappingEditInvalidatesArtifactFrom, StringComparer.Ordinal)
            .Should().ContainSingle().Which.Should().Be(Delivered,
                "delivered is the ONLY status the supplier-level save deliberately leaves alone: a " +
                "per-order edit is a statement about that order's document, a supplier save is a " +
                "statement about future uploads, and flipping completed orders back to ready is not " +
                "something an operator fixing a typo asked for");
    }

    /// <summary>
    /// The union invariant survives the subtraction, which is the evidence that removing
    /// <c>delivered</c> costs no delivery path anything: <c>delivered</c> is in NO claim set — not
    /// dispatch, not retry, not the ops requeue, and not <see cref="OrderStatusMachine.RedeliverableFrom"/>
    /// (which admits <c>delivery_failed</c> / <c>ready_to_deliver</c> / <c>delivery_unconfirmed</c>) —
    /// so nothing can ship a delivered order's stored artifact IN PLACE. If a future change ever makes
    /// <c>delivered</c> claimable, this fails and the subtraction has to be re-argued.
    /// </summary>
    [Fact]
    public void SupplierMappingEditReset_StillCoversEveryStatusADeliveryPathCanClaimOrAdmit()
    {
        var claimable = new[]
            {
                OrderStatusMachine.ClaimableForDispatchFrom,
                OrderStatusMachine.ClaimableForAutomaticDispatchFrom,
                OrderStatusMachine.ClaimableForRetryFrom,
                OrderStatusMachine.RedeliverableFrom,
                OrderStatusMachine.RetryableFrom,
                OrderStatusMachine.RequeueableFrom,
                OrderStatusMachine.HoldableForBillingFrom,
            }
            .SelectMany(s => s)
            .ToHashSet(StringComparer.Ordinal);

        claimable.Should().HaveCountGreaterThan(3,
            "if the claim sets ever read empty this must fail rather than assert over nothing");

        OrderStatusMachine.SupplierMappingEditInvalidatesArtifactFrom.Should().Contain(claimable,
            "every status a delivery path can claim ships the STORED artifact without re-transforming, " +
            "so neither dropping delivered nor inheriting the MappingEditRefusedFrom residuals may " +
            "have dropped any of them with it");

        OrderStatusMachine.SupplierMappingEditInvalidatesArtifactFrom.Should().Contain(DeliveryHeld,
            "delivery_held is in no claim set and must still be reset — the same counter-example that " +
            "makes deriving either of these sets from the claim sets unsafe");
    }

    /// <summary>
    /// The residual is EMPTY. MV-2 closed its only member by refusing the edit at the endpoint, so
    /// every status that can hold a shippable artifact now either refuses the edit or invalidates
    /// the artifact. Any member added here is a real stale-artifact path being accepted rather than
    /// fixed, and it must cost an edit here and an argument in the doc above.
    /// </summary>
    [Fact]
    public void MappingEditKnownResidual_IsEmpty()
        => MappingEditKnownResidual.Should().BeEmpty(
            "a known residual is a live path on which a correction is stored and then discarded " +
            "while a pre-edit document goes out — the delivery_held bug, accepted rather than fixed");

    /// <summary>
    /// MV-2 — the refusal set is a SUBSET of <see cref="OrderStatusMachine.ResolveHeldFrom"/>, and a
    /// STRICT one. Both directions matter and neither is decorative.
    ///
    /// <para><b>Subset.</b> A status this endpoint refuses is one where an in-flight machine step
    /// owns the order, and every such status is already refused by the two recompute endpoints. A
    /// member here that <c>ResolveHeldFrom</c> does not carry would mean the mapping editor is
    /// stricter than the resolver about the same order in the same moment — two answers to one
    /// question, which is the drift WP-23 centralised these sets to prevent.</para>
    ///
    /// <para><b>Strict.</b> <c>ResolveHeldFrom</c> was evidenced against the status RECOMPUTE, a
    /// different writer. Widening this set to match it would be transcription — <c>parsing</c> and
    /// <c>unrouted</c> have no call-site evidence for THIS writer (the parse does not write
    /// <c>canonical_json</c>, and this endpoint writes no status on an <c>unrouted</c> order), and
    /// this file's rule for the sibling set is "name the writer, name the line, say what it
    /// destroys". Pinning strictness is what stops a later "make them consistent" refactor from
    /// removing two operator controls nobody argued for.</para>
    /// </summary>
    [Fact]
    public void MappingEditRefusedFrom_IsAStrictSubsetOfResolveHeldFrom()
    {
        OrderStatusMachine.MappingEditRefusedFrom.Should().NotBeEmpty(
            "an empty refusal set would make every assertion about it vacuous — and would reopen " +
            "the delivering gap");

        OrderStatusMachine.MappingEditRefusedFrom.Should().BeSubsetOf(
            OrderStatusMachine.ResolveHeldFrom,
            "a mapping edit may not be refused from a status where the far more destructive status " +
            "recompute is still allowed");

        OrderStatusMachine.ResolveHeldFrom.Should().NotBeSubsetOf(
            OrderStatusMachine.MappingEditRefusedFrom,
            "the two sets answer questions about DIFFERENT writers; equality would mean one of them " +
            "was transcribed rather than evidenced");
    }

    /// <summary>
    /// Every refused status has its own operator sentence, reached through the SAME
    /// <see cref="OrderStatusMachine.ResolveHoldMessage"/> table the recompute endpoints use rather
    /// than a second copy. The subset invariant above is what makes that reuse total, but a subset
    /// relation alone does not prove the sentence is specific — the fallback would satisfy it.
    /// </summary>
    [Fact]
    public void EveryMappingEditRefusal_HasItsOwnSentence_FromTheSharedTable()
    {
        var fallback = OrderStatusMachine.ResolveHoldMessage("a-status-the-machine-has-never-heard-of");

        foreach (var status in OrderStatusMachine.MappingEditRefusedFrom)
            OrderStatusMachine.ResolveHoldMessage(status).Should().NotBe(fallback,
                $"'{status}' ships the generic fallback instead of a sentence about its own case");

        OrderStatusMachine.MappingEditRefusedFrom
            .Select(OrderStatusMachine.ResolveHoldMessage)
            .Should().OnlyHaveUniqueItems("two refusals reading identically tells the operator nothing");
    }

    /// <summary>A stand-in for a future <c>OrderStatusConstants</c>, carrying one status nobody listed.</summary>
    private static class SyntheticStatusConstants
    {
        public const string Ready = "ready";
        public const string Failed = "failed";
        public const string Quarantined = "quarantined";

        // Shaped like FailureBucket: public, static, NOT a const — must not be mistaken for a status.
        public static readonly string NotAStatus = "not_a_status";
    }
}
