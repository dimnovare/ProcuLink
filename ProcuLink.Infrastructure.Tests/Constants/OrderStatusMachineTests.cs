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
    [InlineData(DeliveryDeadLetter, DeliveryFailed)] // requeued dead-letter fails again / late failure webhook (aligns with the observer map)
    [InlineData(DeliveryDeadLetter, Ready)]      // MV-1 sibling: mapping edit after dead-letter
    // Supplier status webhook: a late positive ACK from every dispatched state. All four are
    // documented as intended in OrderStatusTransitionObserver and gated by WebhookReportableFrom.
    [InlineData(ReadyToDeliver, Delivered)]      // ACK races our own 'delivering' write
    [InlineData(DeliveryFailed, Delivered)]      // late positive ACK after a failed attempt
    [InlineData(DeliveryDeadLetter, Delivered)]  // late positive ACK after dead-lettering
    [InlineData(DeliveryHeld, Delivered)]        // ACK for an order sent before the billing hold
    [InlineData(Delivered, DeliveryFailed)]      // webhook late-failure edge
    [InlineData(Ready, RejectedBySupplier)]      // mark-rejected (from any non-terminal)
    [InlineData(Delivering, RejectedBySupplier)]
    // Routing (Phase 0): an order can be parked unrouted while it awaits a supplier, then
    // re-enter the parse flow once one is assigned.
    [InlineData(PendingParse, Unrouted)]         // extract found no supplier → hold
    [InlineData(Parsing, Unrouted)]              // extract found no supplier → hold
    [InlineData(Unrouted, Parsing)]              // assign-supplier re-enqueues parse
    [InlineData(Unrouted, PendingParse)]
    [InlineData(Unrouted, RejectedBySupplier)]   // operator discards an unrouted order
    public void IsAllowed_RealTransitions_AreAllowed(string from, string to)
        => OrderStatusMachine.IsAllowed(from, to).Should().BeTrue($"{from} -> {to} is a real flow");

    [Theory]
    // Genuinely-impossible moves must be rejected (this is the value the machine adds).
    [InlineData(Delivered, Parsing)]
    [InlineData(Failed, Delivering)]
    [InlineData(RejectedBySupplier, Ready)]
    [InlineData(Delivered, Transforming)]
    [InlineData(Parsing, Delivered)]
    [InlineData(RejectedBySupplier, Delivered)]  // terminal for webhooks: no silent un-rejection
    public void IsAllowed_ImpossibleTransitions_AreRejected(string from, string to)
        => OrderStatusMachine.IsAllowed(from, to).Should().BeFalse($"{from} -> {to} must never happen");

    [Theory]
    [InlineData(Failed)]
    [InlineData(RejectedBySupplier)]
    public void IsTerminal_TrueForTerminalStates(string status)
        => OrderStatusMachine.IsTerminal(status).Should().BeTrue();

    [Theory]
    [InlineData(Delivered)]            // a webhook can still flip it to delivery_failed
    [InlineData(DeliveryDeadLetter)]   // an ops requeue can still rescue it
    [InlineData(Ready)]
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
        Edge(Failed, PendingReview),
        Edge(Failed, Ready),

        // Nothing WRITES 'pending_parse': every ingest path stamps 'parsing' straight onto
        // the stub (OrderIngestionService.cs:343, SampleOrderService.cs:130), and the stuck-order
        // requeue re-writes 'parsing' too (StuckOrderDetectionService.cs:101). No order is ever in
        // pending_parse, so nothing can transition out of it. The status is declared but dead.
        Edge(PendingParse, PendingReview),
        Edge(PendingParse, Ready),
        Edge(PendingParse, Failed),

        // No call site fails an order from the review loop; a hard failure is only ever written
        // from 'parsing' (StuckOrderDetectionService.cs:195).
        Edge(PendingReview, Failed),
        Edge(Ready, Failed),

        // No re-transform path re-enters 'transforming' from these: the transform claim is keyed on
        // ready|transforming|transform_failed (OrderTransformService.cs), and a mapping edit resets a
        // post-artifact order to 'ready' first (the MV-1 edges the machine already allows).
        Edge(ReadyToDeliver, Transforming),
        Edge(DeliveryFailed, Transforming),
        Edge(DeliveryFailed, PendingReview),

        // Nothing moves an order OUT of 'rejected_by_supplier' under its own power: the ops
        // requeue guards on dead_letter|delivery_failed (OpsController.cs:126) and dispatch/retry
        // guard on ready_to_deliver|delivery_failed|stale-delivering.
        Edge(RejectedBySupplier, PendingReview),
        Edge(RejectedBySupplier, Ready),
        Edge(RejectedBySupplier, Transforming),
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

    [Fact]
    public void Machine_KnowsEveryDeclaredStatusConstant()
    {
        // Completeness: every OrderStatusConstants string is a node in the machine,
        // so a future status can't be silently absent from transition reasoning.
        var declared = new[]
        {
            PendingParse, Parsing, PendingReview, Ready, Transforming, ReadyToDeliver,
            Delivering, Delivered, DeliveryFailed, TransformFailed, RejectedBySupplier,
            DeliveryDeadLetter, Failed, Unrouted, DeliveryHeld, DeliveryUnconfirmed,
        };
        foreach (var s in declared)
            OrderStatusMachine.Transitions.Keys.Should().Contain(s);
    }
}
