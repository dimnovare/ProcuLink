using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services.Delivery;

/// <summary>
/// Turns an email provider's asynchronous failure report into the order state it actually implies.
///
/// <para>See <see cref="IDeliveryBounceHandler"/> for why this exists. In short: the email channels
/// call an order <c>delivered</c> the moment the first hop accepts the handoff, and nothing consumed
/// the bounce that followed a mistyped address — so the order stayed <c>delivered</c> forever.</para>
///
/// <para><b>What it deliberately does not do.</b> It does not invent an order status. A bounced
/// order lands in <see cref="OrderStatusConstants.DeliveryFailed"/>, which the queue, the review
/// screen and the retry affordance already render, and which
/// <see cref="IOrderExceptionService.ReconcileAsync"/> already turns into an open exception. A new
/// member would have had to be mirrored into the frontend's status manifest and rendered nowhere
/// until it was — a worse outcome than reusing the status that means exactly this: the supplier did
/// not get it, and a human has to act.</para>
///
/// <para><b>It does not rewrite the transport record either.</b> <c>TransportAcceptedAt</c> stays
/// set: the relay really did accept the message at that instant. The attempt flips to
/// <see cref="DeliveryAttempt.StatusFailed"/> with the provider's own description in
/// <c>RejectionReason</c>, so the trail reads as what happened — accepted at T, bounced at T+n —
/// rather than pretending the handoff never occurred.</para>
/// </summary>
public sealed class DeliveryBounceHandler : IDeliveryBounceHandler
{
    private readonly ProcuLinkDbContext _db;
    private readonly IOrderExceptionService _exceptions;
    private readonly ILogger<DeliveryBounceHandler> _logger;

    /// <summary>
    /// Order statuses a bounce can still contradict. Anything else — the order was re-sent, already
    /// failed, or was dead-lettered — is later information than this bounce, and overwriting it
    /// would drag an order backwards on the strength of a webhook about a superseded send.
    /// </summary>
    private static readonly HashSet<string> ContradictableStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        OrderStatusConstants.Delivered,
        OrderStatusConstants.DeliveryUnconfirmed,
    };

    public DeliveryBounceHandler(
        ProcuLinkDbContext db,
        IOrderExceptionService exceptions,
        ILogger<DeliveryBounceHandler> logger)
    {
        _db = db;
        _exceptions = exceptions;
        _logger = logger;
    }

    public async Task<DeliveryBounceResult> HandleAsync(
        DeliveryBounceNotification notification, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(notification.IdempotencyKey))
            return Uncorrelated(notification, "the notification carried no delivery key");

        // The attempt row is the tenant boundary here: it carries OrgId, so resolving the attempt
        // resolves the organisation. Nothing in the webhook payload names a tenant, and nothing in
        // it is trusted to.
        var attempt = await _db.DeliveryAttempts
            .Include(a => a.Order)
            .Where(a => a.IdempotencyKey == notification.IdempotencyKey && a.OrderId != null)
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefaultAsync(ct);

        if (attempt?.Order is null)
            return Uncorrelated(notification, "no delivery attempt matches the key");

        var order = attempt.Order;
        var now = DateTime.UtcNow;
        var reason = BuildReason(notification);

        // Record the bounce on the attempt whatever the order's state — the trail is the product,
        // and a bounce against a superseded send is still evidence about that send.
        attempt.Status = DeliveryAttempt.StatusFailed;
        attempt.RejectionReason = reason;
        attempt.ErrorMessage ??= reason;

        // Read before the write — the audit row states the transition, and computing "what it was"
        // from the mutated field is how a trail comes to assert its own conclusion.
        var previousOrderStatus = order.Status;
        var contradicts = ContradictableStatuses.Contains(order.Status);
        if (contradicts)
        {
            order.Status = OrderStatusConstants.DeliveryFailed;
            order.UpdatedAt = now;
        }

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = attempt.OrgId,
            UserId = null,
            EntityType = "Order",
            EntityId = order.Id,
            Action = "DeliveryBounced",
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                kind = notification.Kind.ToString(),
                recipient = notification.Recipient,
                description = notification.Description,
                providerMessageId = notification.ProviderMessageId,
                attemptId = attempt.Id,
                channel = attempt.Channel,
                // What the order was BEFORE this, so the trail explains the transition rather than
                // just asserting the new value.
                previousOrderStatus,
                orderStatusChanged = contradicts,
            })),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        if (!contradicts)
        {
            _logger.LogWarning(
                "Delivery bounce for order {OrderId} (org {OrgId}) recorded, but the order is in " +
                "'{Status}' — later information than this bounce, so the status was left alone.",
                order.Id, attempt.OrgId, order.Status);
            return new DeliveryBounceResult(DeliveryBounceOutcome.AlreadyResolved, order.Id);
        }

        // The existing reconciliation is what opens the exception; the status above is its input.
        // Reusing it means a bounced order reaches the operator through the same surface every
        // other delivery failure does, and auto-resolves the same way on a successful re-send.
        await _exceptions.ReconcileAsync(attempt.OrgId, order.Id, ct);

        _logger.LogWarning(
            "Delivery bounce for order {OrderId} (org {OrgId}): {Reason}. Order moved off " +
            "'delivered' to '{Status}' and a delivery exception was opened.",
            order.Id, attempt.OrgId, reason, OrderStatusConstants.DeliveryFailed);

        return new DeliveryBounceResult(DeliveryBounceOutcome.OrderMarkedFailed, order.Id);
    }

    private DeliveryBounceResult Uncorrelated(DeliveryBounceNotification notification, string why)
    {
        // NOT a silent drop. An unattributable bounce is a real delivery failure this product cannot
        // point at an order — the operator has to be able to see that it happened, or the webhook is
        // a check that cannot fail. The recipient is logged because the operator's next move is to
        // search their own supplier addresses for it.
        _logger.LogWarning(
            "Delivery bounce could not be attributed to an order ({Why}): kind={Kind} recipient={Recipient} " +
            "providerMessageId={ProviderMessageId}. No order state was changed.",
            why, notification.Kind, notification.Recipient, notification.ProviderMessageId);

        return new DeliveryBounceResult(DeliveryBounceOutcome.Uncorrelated);
    }

    private static string BuildReason(DeliveryBounceNotification n)
    {
        var what = n.Kind == DeliveryBounceKind.SpamComplaint
            ? "The recipient marked this order email as spam"
            : "The supplier's email address rejected this order permanently";

        var who = string.IsNullOrWhiteSpace(n.Recipient) ? null : $" ({n.Recipient})";
        var detail = string.IsNullOrWhiteSpace(n.Description) ? null : $": {n.Description}";

        return $"{what}{who}{detail}";
    }
}
