using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Constants;

/// <summary>
/// Every known order status must be CLASSIFIED — into exactly one of failure, parked, in-flight,
/// delivered, or held.
///
/// <para><b>Why this exists.</b> <c>DashboardController</c> kept a private "exceptions" set
/// documented as "everything that failed, plus the orders parked awaiting a human". It listed one
/// parked status. <c>unrouted</c> and <c>delivery_unconfirmed</c> were both absent, so an order
/// parked after a crash fell through every check the dashboard performed and was scored as a
/// healthy delivery — a supplier whose every order was parked rendered a green 100%.</para>
///
/// <para><b>Why a partition and not a list.</b> The previous guard shape was "walk the failure
/// bucket and assert each member fails", which is one-directional: it can only catch a status that
/// SHOULD be in the bucket it already walks. It cannot see a status that belongs to no bucket at
/// all, because there is nothing to walk it from — and "belongs to no bucket" is precisely how both
/// missing statuses behaved. A partition asks the question from the other end: here is every status
/// the machine knows, prove each one has a home. A new status added tomorrow fails this test on the
/// day it is declared, instead of quietly defaulting to "healthy".</para>
/// </summary>
public class OrderStatusBucketPartitionTests
{
    /// <summary>
    /// The five buckets, as (name, members). <see cref="OrderStatusConstants.Delivered"/> and
    /// <see cref="OrderStatusConstants.DeliveryHeld"/> are single-member buckets by nature: one is
    /// the only success, the other is a deliberate, self-releasing billing pause that the founder
    /// call (2026-07-16, recorded on <c>OpsHealthSummary.DeliveryHeld</c>) keeps out of both the
    /// failure and the parked sets.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, IReadOnlySet<string> Members)> Buckets = new[]
    {
        ("FailureBucket",      OrderStatusConstants.FailureBucket),
        ("AwaitingHumanBucket", OrderStatusConstants.AwaitingHumanBucket),
        ("InFlightBucket",     OrderStatusConstants.InFlightBucket),
        ("Delivered",          (IReadOnlySet<string>)new HashSet<string> { OrderStatusConstants.Delivered }),
        ("DeliveryHeld",       (IReadOnlySet<string>)new HashSet<string> { OrderStatusConstants.DeliveryHeld }),
    };

    [Fact]
    public void EveryKnownStatus_BelongsToExactlyOneBucket()
    {
        var unclassified = new List<string>();
        var multiplyClassified = new List<string>();

        foreach (var status in OrderStatusMachine.AllStatuses)
        {
            var homes = Buckets.Where(b => b.Members.Contains(status)).Select(b => b.Name).ToList();
            if (homes.Count == 0) unclassified.Add(status);
            if (homes.Count > 1) multiplyClassified.Add($"{status} → {string.Join(" + ", homes)}");
        }

        unclassified.Should().BeEmpty(
            "a status in no bucket is invisible to every health check that reads them — add it to the "
          + "bucket that describes it rather than letting it default to healthy");
        multiplyClassified.Should().BeEmpty(
            "the buckets must be mutually exclusive, or a status counts as two different things at once");
    }

    [Fact]
    public void EveryBucketMember_IsAStatusTheMachineKnows()
    {
        // The reverse direction: a bucket may not carry a status the machine has never heard of
        // (a typo, or a constant deleted from the machine but left in a bucket).
        var members = Buckets.SelectMany(b => b.Members.Select(s => (b.Name, Status: s))).ToList();

        // Unconditional floor FIRST: every assertion below sits inside a loop, so without this the
        // test would report green over an empty walk.
        members.Should().HaveCount(
            16, "the five buckets held 16 statuses between them when this floor was written");

        var unknown = members
            .Where(m => !OrderStatusMachine.AllStatuses.Contains(m.Status))
            .Select(m => $"{m.Name} lists '{m.Status}'")
            .ToList();

        unknown.Should().BeEmpty("a bucket may not name a status the machine does not know");
    }

    /// <summary>
    /// Anti-vacuity floor. Every assertion above is a "should be empty" or a loop, all of which pass
    /// trivially if the walk collapses to nothing — the failure mode this repo has already paid for
    /// more than once.
    /// </summary>
    [Fact]
    public void ThePartitionWalk_IsNotEmpty()
    {
        OrderStatusMachine.AllStatuses.Should().HaveCountGreaterThanOrEqualTo(
            16, "the machine knew 16 statuses when this floor was written");
        Buckets.Should().HaveCount(5);
        Buckets.Sum(b => b.Members.Count).Should().Be(
            OrderStatusMachine.AllStatuses.Count,
            "a partition covers every status exactly once, so the bucket sizes must add up");

        // The two statuses whose absence from the dashboard's exception set caused the defect.
        OrderStatusConstants.AwaitingHumanBucket.Should().Contain(OrderStatusConstants.DeliveryUnconfirmed);
        OrderStatusConstants.AwaitingHumanBucket.Should().Contain(OrderStatusConstants.Unrouted);
    }

    /// <summary>
    /// <c>delivery_unconfirmed</c> is parked, NOT failed — the two subsystems disagreed about this
    /// and the disagreement is what the packet resolved. <c>OpsHealthSummary</c> counts it in
    /// <c>TotalProblemOrders</c> ("a park is a FAULT"), so it must appear in a bucket a health check
    /// treats as needing attention; but it is not a failure, because the whole meaning of the status
    /// is that we do not know the send failed.
    /// </summary>
    [Fact]
    public void DeliveryUnconfirmed_IsParked_AndIsNotAFailure()
    {
        OrderStatusConstants.AwaitingHumanBucket.Should().Contain(OrderStatusConstants.DeliveryUnconfirmed);
        OrderStatusConstants.FailureBucket.Should().NotContain(
            OrderStatusConstants.DeliveryUnconfirmed,
            "we do not know that the send failed — recording it as a failure is the equal and opposite lie");
        OrderStatusConstants.SettledDeliveryBucket.Should().NotContain(
            OrderStatusConstants.DeliveryUnconfirmed,
            "an unknown outcome is not a settled one, so it must not enter a success rate's denominator");
    }

    /// <summary>
    /// The settled set is the score's denominator: exactly the failure bucket plus
    /// <c>delivered</c>, and nothing in flight, parked, or held.
    /// </summary>
    [Fact]
    public void SettledDeliveryBucket_IsTheFailureBucketPlusDelivered()
    {
        OrderStatusConstants.SettledDeliveryBucket.Should().BeEquivalentTo(
            OrderStatusConstants.FailureBucket.Append(OrderStatusConstants.Delivered));

        foreach (var parked in OrderStatusConstants.AwaitingHumanBucket)
            OrderStatusConstants.SettledDeliveryBucket.Should().NotContain(parked);
        foreach (var inFlight in OrderStatusConstants.InFlightBucket)
            OrderStatusConstants.SettledDeliveryBucket.Should().NotContain(inFlight);
        OrderStatusConstants.SettledDeliveryBucket.Should().NotContain(OrderStatusConstants.DeliveryHeld);
    }
}
