using FluentAssertions;
using ProcuLink.Core.Constants;
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
