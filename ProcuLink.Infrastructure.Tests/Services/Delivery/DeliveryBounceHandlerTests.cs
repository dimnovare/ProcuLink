using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Delivery;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Delivery;

/// <summary>
/// B-12 — <i>"The address had a typo. Postmark bounced it in four seconds. It still shows
/// delivered."</i>
///
/// <para>"Delivered" on the email channels means only that the first hop accepted the handoff — a
/// Postmark 2xx. Nothing consumed the bounce that followed, so a mistyped supplier address left the
/// order reading <c>delivered</c> permanently. That inverts what this product sells.</para>
///
/// <para>The real <see cref="OrderExceptionService"/> is used, not a double: the whole design
/// decision is that a bounced order reaches the operator through the SAME reconciliation every other
/// delivery failure uses. A mocked exception service would let that claim be false while the tests
/// stayed green.</para>
/// </summary>
public class DeliveryBounceHandlerTests
{
    private const string Key = "delivery-key-b12";

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryBounceHandler MakeHandler(ProcuLinkDbContext db) =>
        new(db, new OrderExceptionService(db), NullLogger<DeliveryBounceHandler>.Instance);

    /// <summary>
    /// Seeds an order that the email channel already called delivered, plus the successful attempt
    /// that said so — carrying the idempotency key the outbound send stamped into provider metadata.
    /// </summary>
    private static (Guid OrgId, Guid OrderId) SeedDeliveredOrder(
        ProcuLinkDbContext db,
        string orderStatus = OrderStatusConstants.Delivered,
        string? idempotencyKey = Key)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            PoNumber = "PO-B12-1",
            Currency = "EUR",
            Status = orderStatus,
            OrderDate = DateOnly.FromDateTime(now),
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            OrgId = orgId,
            Channel = DeliveryProtocolConstants.Email,
            Destination = "supplier@example.com",
            Status = DeliveryAttempt.StatusSuccess,
            AttemptNumber = 1,
            AttemptedAt = now,
            IdempotencyKey = idempotencyKey,
            ResponseCode = 200,
            TransportAcceptedAt = now,
        });

        db.SaveChanges();
        return (orgId, orderId);
    }

    private static DeliveryBounceNotification HardBounce(string? key = Key) => new(
        IdempotencyKey: key,
        Kind: DeliveryBounceKind.Hard,
        Recipient: "suplier@example.com",
        Description: "The server was unable to deliver your message (ex: unknown user, mailbox not found).",
        ProviderMessageId: "pm-msg-1");

    [Fact]
    public async Task HardBounce_MovesTheOrderOffDelivered_AndOpensADeliveryException()
    {
        await using var db = NewDb();
        var (orgId, orderId) = SeedDeliveredOrder(db);

        var result = await MakeHandler(db).HandleAsync(HardBounce(), CancellationToken.None);

        result.Outcome.Should().Be(DeliveryBounceOutcome.OrderMarkedFailed);
        result.OrderId.Should().Be(orderId);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed,
            "an order the supplier never received must not keep reading 'delivered'");

        var open = await db.OrderExceptions.Where(e => e.OrgId == orgId && e.State == "open").ToListAsync();
        open.Should().ContainSingle(
            "the bounce must reach the operator through the same reconciliation every other delivery " +
            "failure uses, not through a second private path");
    }

    /// <summary>
    /// The attempt is the audit trail, and the trail must say what happened rather than what is
    /// convenient. The transport DID accept the message at T, so <c>TransportAcceptedAt</c> stays —
    /// the bounce is a later event, not a retraction of the handoff.
    /// </summary>
    [Fact]
    public async Task HardBounce_MarksTheAttemptFailed_ButKeepsTheTransportAcceptanceItReallyGot()
    {
        await using var db = NewDb();
        SeedDeliveredOrder(db);

        await MakeHandler(db).HandleAsync(HardBounce(), CancellationToken.None);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be(DeliveryAttempt.StatusFailed);
        attempt.RejectionReason.Should().Contain("rejected this order permanently")
            .And.Contain("suplier@example.com", "the operator's next move is to look at the address");
        attempt.TransportAcceptedAt.Should().NotBeNull(
            "the relay really did accept the message; erasing that would replace one false record with another");
    }

    [Fact]
    public async Task SpamComplaint_AlsoMovesTheOrderOffDelivered_WithItsOwnReason()
    {
        await using var db = NewDb();
        SeedDeliveredOrder(db);

        var result = await MakeHandler(db).HandleAsync(
            HardBounce() with { Kind = DeliveryBounceKind.SpamComplaint }, CancellationToken.None);

        result.Outcome.Should().Be(DeliveryBounceOutcome.OrderMarkedFailed);
        (await db.DeliveryAttempts.SingleAsync()).RejectionReason
            .Should().Contain("marked this order email as spam",
                "a complaint and a dead address are different operator problems and must read differently");
    }

    [Fact]
    public async Task Bounce_WritesAnAuditRowNamingTheTransitionItMade()
    {
        await using var db = NewDb();
        var (orgId, orderId) = SeedDeliveredOrder(db);

        await MakeHandler(db).HandleAsync(HardBounce(), CancellationToken.None);

        var audit = await db.AuditEvents.SingleAsync(a => a.Action == "DeliveryBounced");
        audit.OrgId.Should().Be(orgId);
        audit.EntityId.Should().Be(orderId);

        var payload = audit.Payload!.RootElement;
        payload.GetProperty("previousOrderStatus").GetString().Should().Be(OrderStatusConstants.Delivered,
            "the row must state what the order WAS — computing that from the already-mutated field is " +
            "how a trail comes to assert its own conclusion");
        payload.GetProperty("orderStatusChanged").GetBoolean().Should().BeTrue();
        payload.GetProperty("providerMessageId").GetString().Should().Be("pm-msg-1");
    }

    /// <summary>
    /// A bounce for a send that has already been superseded is still evidence about THAT send, but
    /// it is older information than the order's current state. Applying it would drag a re-sent or
    /// already-failed order backwards on the strength of a webhook about a dead attempt.
    /// </summary>
    [Theory]
    [InlineData(OrderStatusConstants.ReadyToDeliver)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter)]
    public async Task Bounce_AgainstAnOrderThatHasMovedOn_IsRecordedButDoesNotRewriteTheStatus(string status)
    {
        await using var db = NewDb();
        var (_, orderId) = SeedDeliveredOrder(db, orderStatus: status);

        var result = await MakeHandler(db).HandleAsync(HardBounce(), CancellationToken.None);

        result.Outcome.Should().Be(DeliveryBounceOutcome.AlreadyResolved);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(status);
        (await db.DeliveryAttempts.SingleAsync()).Status.Should().Be(DeliveryAttempt.StatusFailed,
            "the attempt still bounced, and the trail is the product");
    }

    /// <summary>
    /// The failure mode that would make this whole feature a check that cannot fail: a bounce whose
    /// metadata was dropped, or whose key matches nothing. It must NOT be silently swallowed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a-key-that-matches-no-attempt")]
    public async Task Bounce_ThatCannotBeAttributed_IsReportedUncorrelated_AndChangesNothing(string? key)
    {
        await using var db = NewDb();
        var (_, orderId) = SeedDeliveredOrder(db);

        var result = await MakeHandler(db).HandleAsync(HardBounce(key), CancellationToken.None);

        result.Outcome.Should().Be(DeliveryBounceOutcome.Uncorrelated);
        result.OrderId.Should().BeNull();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivered, "an unattributable bounce must not guess at an order");
        (await db.AuditEvents.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Anti-vacuity control for the "changes nothing" assertions above: the same seed, the same
    /// handler, a key that DOES match — and the state moves. Without this, a handler that no-op'd
    /// on every input would pass every uncorrelated case.
    /// </summary>
    [Fact]
    public async Task TheHandler_ActuallyChangesState_WhenTheKeyMatches()
    {
        await using var db = NewDb();
        var (_, orderId) = SeedDeliveredOrder(db);

        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivered, "the seed must start in the state under test");

        await MakeHandler(db).HandleAsync(HardBounce(), CancellationToken.None);

        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryFailed);
    }

    /// <summary>
    /// A bounce resolves its organisation from the attempt row and nothing else. Two orgs whose
    /// attempts happen to carry the same key must not bleed into each other — the handler takes the
    /// most recent match and touches only that one order.
    /// </summary>
    [Fact]
    public async Task Bounce_TouchesOnlyTheOrderItsAttemptBelongsTo()
    {
        await using var db = NewDb();
        var (_, firstOrderId) = SeedDeliveredOrder(db);
        await Task.Delay(5);
        var (_, secondOrderId) = SeedDeliveredOrder(db);

        var result = await MakeHandler(db).HandleAsync(HardBounce(), CancellationToken.None);

        result.OrderId.Should().Be(secondOrderId, "the newest attempt carrying the key is the one that sent it");

        var untouched = await db.PurchaseOrders.SingleAsync(o => o.Id == firstOrderId);
        untouched.Status.Should().Be(OrderStatusConstants.Delivered,
            "the other organisation's order must be left exactly as it was");
        (await db.AuditEvents.CountAsync()).Should().Be(1);
    }
}
