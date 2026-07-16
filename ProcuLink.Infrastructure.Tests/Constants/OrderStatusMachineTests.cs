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
    [InlineData(DeliveryDeadLetter, Delivering)] // ops requeue rescue
    [InlineData(DeliveryDeadLetter, DeliveryFailed)] // requeued dead-letter fails again / late failure webhook (aligns with the observer map)
    [InlineData(DeliveryDeadLetter, Ready)]      // MV-1 sibling: mapping edit after dead-letter
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
    public void IsAllowed_ImpossibleTransitions_AreRejected(string from, string to)
        => OrderStatusMachine.IsAllowed(from, to).Should().BeFalse($"{from} -> {to} must never happen");

    [Theory]
    [InlineData(Failed)]
    [InlineData(RejectedBySupplier)]
    [InlineData(TransformFailed)]
    public void IsTerminal_TrueForTerminalStates(string status)
        => OrderStatusMachine.IsTerminal(status).Should().BeTrue();

    [Theory]
    [InlineData(Delivered)]            // a webhook can still flip it to delivery_failed
    [InlineData(DeliveryDeadLetter)]   // an ops requeue can still rescue it
    [InlineData(Ready)]
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
    public void RedeliverableFrom_MatchesThePriorLiteralExactly()
        => OrderStatusMachine.RedeliverableFrom.Should()
            .BeEquivalentTo(new[] { DeliveryFailed, ReadyToDeliver });

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

        // 'transform_failed' is declared and bucketed but never WRITTEN by any production code —
        // OrderTransformService reverts a failed transform to 'ready' instead. Both the edge into
        // it and every edge out of it are dead.
        Edge(Transforming, TransformFailed),
        Edge(TransformFailed, Ready),
        Edge(TransformFailed, Transforming),
        Edge(TransformFailed, PendingReview),

        // Nothing WRITES 'pending_parse' either: every ingest path stamps 'parsing' straight onto
        // the stub (OrderIngestionService.cs:343, SampleOrderService.cs:130), and the stuck-order
        // requeue re-writes 'parsing' too (StuckOrderDetectionService.cs:101). No order is ever in
        // pending_parse, so nothing can transition out of it. Like transform_failed, the status is
        // declared but dead.
        Edge(PendingParse, PendingReview),
        Edge(PendingParse, Ready),
        Edge(PendingParse, Failed),

        // No call site fails an order from the review loop; a hard failure is only ever written
        // from 'parsing' (StuckOrderDetectionService.cs:195).
        Edge(PendingReview, Failed),
        Edge(Ready, Failed),

        // No re-transform path re-enters 'transforming' from these: the transform claim is keyed on
        // ready|transforming (OrderTransformService.cs:244), and a mapping edit resets the order to
        // 'ready' first (the MV-1 edges the machine already allows).
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

        // ── Reachable, but only through a gap — deliberately NOT blessed in the machine ──
        // WebhookIngressController.Status (:157-175) loads the order by id with NO from-status
        // predicate, then writes 'delivered' (or 'delivery_failed' on "rejected") to any order not
        // already delivered. A supplier callback can therefore force these — and, in principle,
        // 'delivered' onto an order still in pending_parse. That reads as a missing from-status
        // guard on the webhook rather than an intended flow, so the machine keeps calling these
        // impossible; teaching it to allow them would document the gap as design. If the guard is
        // added, these exemptions go stale and the second assertion below will say so. If instead
        // the unguarded write is judged intended, move them into Transitions.
        Edge(ReadyToDeliver, Delivered),
        Edge(DeliveryFailed, Delivered),
        Edge(DeliveryDeadLetter, Delivered),
        Edge(RejectedBySupplier, Delivered),
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

    [Fact]
    public void Machine_KnowsEveryDeclaredStatusConstant()
    {
        // Completeness: every OrderStatusConstants string is a node in the machine,
        // so a future status can't be silently absent from transition reasoning.
        var declared = new[]
        {
            PendingParse, Parsing, PendingReview, Ready, Transforming, ReadyToDeliver,
            Delivering, Delivered, DeliveryFailed, TransformFailed, RejectedBySupplier,
            DeliveryDeadLetter, Failed, Unrouted, DeliveryHeld,
        };
        foreach (var s in declared)
            OrderStatusMachine.Transitions.Keys.Should().Contain(s);
    }
}
