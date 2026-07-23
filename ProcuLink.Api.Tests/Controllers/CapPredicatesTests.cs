using FluentAssertions;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// ASSERT-THE-DIFFERENCE (spec 2026-07-17-delivery-cap-without-erasing-evidence.md, TDD item 7).
///
/// The dispatch-evidence predicate (<see cref="WebhookIngressController.HasDispatchMarker"/>) and
/// the cap predicate (<see cref="DeliveryAttempt.CountsAgainstCap"/>) read the same table and are
/// DIFFERENT ON PURPOSE — evidence asks "was a send ever begun, ever" (epoch-agnostic), the cap
/// asks "what counts against the CURRENT budget" (epoch-sensitive). Their do-not-unify comments
/// are necessary but not sufficient — this cluster's evidence base is that justifications get
/// skim-read. These tests make unification FAIL THE BUILD: on a post-requeue row the two
/// predicates DISAGREE, and both answers are correct. Same move as
/// <c>DispatchAndRetryClaimSets_DifferExactlyBy_DeliveryUnconfirmed</c>.
/// </summary>
public class CapPredicatesTests
{
    [Fact]
    public void EvidenceAndCap_DisagreeOnAPostRequeueRow()
    {
        // A genuinely-dispatched attempt that an operator requeue then excluded from the budget.
        var postRequeue = new DeliveryAttempt
        {
            Status          = DeliveryAttempt.StatusFailed,
            IdempotencyKey  = "plk-dlv-abc",
            ArtifactSha256  = "b5bb9d8014a0f9b1",
            CapSupersededAt = DateTime.UtcNow,
        };

        // Evidence: a send WAS begun — the supplier may legitimately report against it.
        WebhookIngressController.HasDispatchMarker.Compile()(postRequeue).Should().BeTrue(
            "dispatch evidence is epoch-agnostic: a requeue must never make a sent order read as never-sent");

        // Cap: it does NOT count against the current budget — the operator granted a fresh one.
        DeliveryAttempt.CountsAgainstCap.Compile()(postRequeue).Should().BeFalse(
            "the cap is epoch-sensitive: a superseded attempt must not burn the fresh budget");

        // Both correct. Collapsing the two predicates into one makes this test red — which is the
        // point: the erasure hole reopens under a new name the moment they unify.
    }

    [Fact]
    public void EvidenceAndCap_DisagreeOnAPreDispatchFailureRow()
    {
        // The mirror image: a pre-dispatch failure in the CURRENT budget — no bytes ever left, so
        // there is no evidence, but the attempt burns a budget slot.
        var preDispatch = new DeliveryAttempt
        {
            Status          = DeliveryAttempt.StatusFailed,
            IdempotencyKey  = null,
            ArtifactSha256  = null,
            CapSupersededAt = null,
        };

        WebhookIngressController.HasDispatchMarker.Compile()(preDispatch).Should().BeFalse(
            "nothing was sent — a supplier callback against this order alone must still be refused");
        DeliveryAttempt.CountsAgainstCap.Compile()(preDispatch).Should().BeTrue(
            "a failed attempt in the current budget counts toward the cap");
    }

    [Fact]
    public void NeitherPredicate_CountsAnInFlightRow_ButForDifferentReasons()
    {
        // An in-flight dispatching row: the cap excludes it (not yet a completed attempt); the
        // evidence predicate MATCHES it (OpenDispatchAttemptAsync stamps the idempotency key on
        // the row it commits BEFORE the wire send — the send was begun). Pinning both directions
        // stops a "simplification" that swaps one predicate in for the other.
        var inFlight = new DeliveryAttempt
        {
            Status          = DeliveryAttempt.StatusDispatching,
            IdempotencyKey  = "plk-dlv-inflight",
            ArtifactSha256  = null,
            CapSupersededAt = null,
        };

        DeliveryAttempt.CountsAgainstCap.Compile()(inFlight).Should().BeFalse(
            "an in-flight attempt is not yet a completed attempt — it must not burn a budget slot");
        WebhookIngressController.HasDispatchMarker.Compile()(inFlight).Should().BeTrue(
            "the key is committed before the send, so the send was begun — evidence holds");
    }
}
