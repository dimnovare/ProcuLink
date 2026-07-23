using FluentAssertions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for the ONE delivery-claim predicate factory. The claim-path BEHAVIOUR (that the
/// relational ExecuteUpdateAsync translation of this expression agrees with C#'s evaluation of
/// it) is pinned on real Postgres by <c>DeliveryClaimEquivalencePostgresTests</c>; these pin the
/// factory's semantics in isolation.
/// </summary>
public class DeliveryClaimTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StaleBefore = Now.AddMinutes(-2);

    private static Func<PurchaseOrderEntity, bool> Compiled(IReadOnlySet<string> set) =>
        DeliveryClaim.Claimable(OrgId, OrderId, set, StaleBefore).Compile();

    private static PurchaseOrderEntity Order(string status, DateTime updatedAt) =>
        new() { Id = OrderId, OrgId = OrgId, Status = status, UpdatedAt = updatedAt };

    [Theory]
    [InlineData(OrderStatusConstants.ReadyToDeliver, true)]
    [InlineData(OrderStatusConstants.DeliveryFailed, true)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, true)]
    [InlineData(OrderStatusConstants.Delivered, false)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter, false)]
    [InlineData(OrderStatusConstants.DeliveryHeld, false)]
    public void Claimable_IdleStatuses_MatchTheOperatorDispatchSet(string status, bool expected)
        => Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(Order(status, Now))
            .Should().Be(expected);

    [Fact]
    public void Claimable_FreshDelivering_IsRejected()
        => Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(
                Order(OrderStatusConstants.Delivering, Now))
            .Should().BeFalse("a just-stamped 'delivering' row belongs to the worker that stamped it — " +
                              "claiming it would double-dispatch the same PO to a real supplier");

    [Fact]
    public void Claimable_StaleDelivering_IsClaimable()
        => Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(
                Order(OrderStatusConstants.Delivering, Now.AddMinutes(-30)))
            .Should().BeTrue("a 'delivering' row older than the reclaim window is a crashed worker's " +
                             "orphan and must be recoverable");

    [Fact]
    public void Claimable_WrongOrg_IsRejected()
    {
        var foreign = Order(OrderStatusConstants.ReadyToDeliver, Now);
        foreign.OrgId = Guid.NewGuid();
        Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(foreign)
            .Should().BeFalse("org scoping lives INSIDE the predicate so the claim cannot be written un-scoped");
    }

    [Fact]
    public void Claimable_WrongOrder_IsRejected()
    {
        var other = Order(OrderStatusConstants.ReadyToDeliver, Now);
        other.Id = Guid.NewGuid();
        Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(other)
            .Should().BeFalse("the claim is per-order — a sibling order must never be swept into it");
    }

    /// <summary>
    /// An empty set compiles to `= ANY('{}')` on Postgres, which matches nothing, so the claim
    /// would affect 0 rows and the caller would read that as "someone else claimed it" — silently
    /// stranding the order. That is the exact failure this whole file exists to prevent, so fail
    /// loud instead.
    /// </summary>
    [Fact]
    public void Claimable_EmptySet_ThrowsRatherThanSilentlyMatchingNothing()
    {
        var act = () => DeliveryClaim.Claimable(
            OrgId, OrderId, new HashSet<string>(StringComparer.Ordinal), StaleBefore);

        act.Should().Throw<ArgumentException>()
           .WithParameterName("idleClaimable");
    }

    // ── The dispatch helper: the #42 conditional member, encoded in the factory ──────────────

    [Fact]
    public void ClaimableForDispatch_AutomaticActivation_NeverClaimsAPark()
        => DeliveryClaim.ClaimableForDispatch(OrgId, OrderId, requireAutoDeliver: true, StaleBefore)
            .Compile()(Order(OrderStatusConstants.DeliveryUnconfirmed, Now))
            .Should().BeFalse("a Hangfire refetch of a dead automatic activation must not re-send a " +
                              "PO the stuck sweep parked in the meantime — only a human claims a park");

    [Fact]
    public void ClaimableForDispatch_OperatorActivation_ClaimsAPark()
        => DeliveryClaim.ClaimableForDispatch(OrgId, OrderId, requireAutoDeliver: false, StaleBefore)
            .Compile()(Order(OrderStatusConstants.DeliveryUnconfirmed, Now))
            .Should().BeTrue("the park exists so a HUMAN can choose to re-send");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClaimableForDispatch_BothActivations_ClaimIdleSendReadyStatuses(bool requireAutoDeliver)
    {
        var pred = DeliveryClaim.ClaimableForDispatch(OrgId, OrderId, requireAutoDeliver, StaleBefore).Compile();
        pred(Order(OrderStatusConstants.ReadyToDeliver, Now)).Should().BeTrue();
        pred(Order(OrderStatusConstants.DeliveryFailed, Now)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClaimableForDispatch_BothActivations_ReclaimOnlyAStaleDelivering(bool requireAutoDeliver)
    {
        var pred = DeliveryClaim.ClaimableForDispatch(OrgId, OrderId, requireAutoDeliver, StaleBefore).Compile();
        pred(Order(OrderStatusConstants.Delivering, Now)).Should().BeFalse("fresh = another worker's live claim");
        pred(Order(OrderStatusConstants.Delivering, Now.AddMinutes(-30))).Should().BeTrue("stale = crashed orphan");
    }

    // ── The retry helper ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClaimableForRetry_NeverClaimsAPark()
        => DeliveryClaim.ClaimableForRetry(OrgId, OrderId, StaleBefore)
            .Compile()(Order(OrderStatusConstants.DeliveryUnconfirmed, Now))
            .Should().BeFalse("the backoff queue must never re-send a parked order on the operator's behalf");

    [Fact]
    public void ClaimableForRetry_ClaimsIdleRetryableStatuses_AndStaleDelivering()
    {
        var pred = DeliveryClaim.ClaimableForRetry(OrgId, OrderId, StaleBefore).Compile();
        pred(Order(OrderStatusConstants.ReadyToDeliver, Now)).Should().BeTrue();
        pred(Order(OrderStatusConstants.DeliveryFailed, Now)).Should().BeTrue();
        pred(Order(OrderStatusConstants.Delivering, Now.AddMinutes(-30))).Should().BeTrue();
        pred(Order(OrderStatusConstants.Delivering, Now)).Should().BeFalse();
    }
}
