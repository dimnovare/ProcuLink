using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Webhooks;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// HMAC-verified inbound webhook ingress for supplier ERPs / EDI gateways to
/// push acknowledgements + status callbacks back without an API key.
///
/// Required headers on every request:
///   X-ProcuLink-Timestamp  ISO-8601 UTC, within ±300s of receive time
///   X-ProcuLink-Nonce      Client-generated unique value (UUID recommended)
///   X-ProcuLink-Signature  lower-hex(HMAC-SHA256(secret, $"{ts}.{nonce}.{body}"))
///
/// Auth: HMAC alone — no Clerk, no API key. The org's webhook shared secret
/// (set/rotated by org admins) is the sole authenticator.
///
/// All failure paths return 401 with the same generic error message; never
/// distinguish which check failed (slug unknown vs bad signature vs replay).
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/webhook-ingress/{slug}")]
// Rate-limited per TENANT (the {slug} route value), not per source IP — many
// suppliers push callbacks for one org from a shared egress IP, so an IP-keyed
// bucket would let one noisy tenant exhaust every other tenant's quota. The
// "webhook" policy (Program.cs) derives its partition key from this slug.
[EnableRateLimiting("webhook")]
public sealed class WebhookIngressController : ControllerBase
{
    private readonly IHmacWebhookVerifier                  _verifier;
    private readonly ProcuLinkDbContext                    _db;
    private readonly IOrderExceptionService                _exceptions;
    private readonly ILogger<WebhookIngressController>     _logger;

    public WebhookIngressController(
        IHmacWebhookVerifier                  verifier,
        ProcuLinkDbContext                    db,
        IOrderExceptionService                exceptions,
        ILogger<WebhookIngressController>     logger)
    {
        _verifier   = verifier;
        _db         = db;
        _exceptions = exceptions;
        _logger     = logger;
    }

    /// <summary>POST /api/webhook-ingress/{slug}/ping — connectivity + auth test.</summary>
    [HttpPost("ping")]
    public async Task<IActionResult> Ping(string slug, CancellationToken ct)
    {
        var (body, ts, nonce, sig) = await ReadHeadersAndBodyAsync(ct);
        var result = await _verifier.VerifyAsync(slug, ts, nonce, sig, body, ct);
        if (!result.Valid)
            return Unauthorized(new { error = result.ErrorMessage });

        return Ok(new { ok = true, slug, timestamp = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// POST /api/webhook-ingress/{slug}/acknowledge — supplier acknowledges receipt of an order.
    /// Body: { orderId: guid, supplierReference?: string, acknowledgedAt?: iso, notes?: string }
    /// </summary>
    [HttpPost("acknowledge")]
    public async Task<IActionResult> Acknowledge(string slug, CancellationToken ct)
    {
        var (body, ts, nonce, sig) = await ReadHeadersAndBodyAsync(ct);
        var result = await _verifier.VerifyAsync(slug, ts, nonce, sig, body, ct);
        if (!result.Valid)
            return Unauthorized(new { error = result.ErrorMessage });

        var orgId = result.OrganisationId!.Value;

        AcknowledgePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AcknowledgePayload>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON body." });
        }

        if (payload is null || payload.OrderId == Guid.Empty)
            return BadRequest(new { error = "orderId is required." });

        // Confirm order belongs to this org before recording the audit event.
        var orderExists = await _db.PurchaseOrders
                                   .AnyAsync(o => o.OrgId == orgId && o.Id == payload.OrderId, ct);
        if (!orderExists)
            return NotFound(new { error = "Order not found." });

        var auditPayload = JsonSerializer.Serialize(new
        {
            payload.SupplierReference,
            AcknowledgedAt = payload.AcknowledgedAt ?? DateTimeOffset.UtcNow,
            payload.Notes,
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            UserId     = null,
            EntityType = "PurchaseOrder",
            EntityId   = payload.OrderId,
            Action     = "webhook_acknowledge",
            Payload    = JsonDocument.Parse(auditPayload),
            CreatedAt  = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Webhook acknowledge received for order {OrderId} (org {OrgId}).",
            payload.OrderId, orgId);

        return Ok(new { ok = true, orderId = payload.OrderId });
    }

    /// <summary>
    /// POST /api/webhook-ingress/{slug}/status — supplier reports lifecycle status for an order.
    /// Body: { orderId: guid, status: "received|in_progress|rejected|delivered", reason?: string, occurredAt?: iso }
    /// </summary>
    [HttpPost("status")]
    public async Task<IActionResult> Status(string slug, CancellationToken ct)
    {
        var (body, ts, nonce, sig) = await ReadHeadersAndBodyAsync(ct);
        var result = await _verifier.VerifyAsync(slug, ts, nonce, sig, body, ct);
        if (!result.Valid)
            return Unauthorized(new { error = result.ErrorMessage });

        var orgId = result.OrganisationId!.Value;

        StatusPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatusPayload>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON body." });
        }

        if (payload is null || payload.OrderId == Guid.Empty)
            return BadRequest(new { error = "orderId is required." });

        var status = (payload.Status ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedStatuses.Contains(status))
            return BadRequest(new
            {
                error = $"status must be one of: {string.Join(", ", AllowedStatuses)}.",
            });

        var order = await _db.PurchaseOrders
                             .Where(o => o.OrgId == orgId && o.Id == payload.OrderId)
                             .FirstOrDefaultAsync(ct);
        if (order is null)
            return NotFound(new { error = "Order not found." });

        // A supplier business rejection is NOT a transport failure. delivery_failed would put the
        // order back in reach of the retry machinery -- StrandedFailedDeliveryDetectionService
        // sweeps aged delivery_failed orders with attempts remaining, and RetryDeliveryAsync
        // retries from delivery_failed -- so we would re-send a PO the supplier explicitly
        // rejected. (That sweeper's predicate is justified on the premise that a supplier
        // rejection lands in rejected_by_supplier: StrandedFailedDeliveryDetectionService.cs:46.)
        //
        // A rejection is honoured even for an already-delivered order: HTTP 200 from the channel
        // is transport success, never supplier business acceptance.
        var mutated = false;

        if (status == "rejected")
        {
            order.Status    = OrderStatusConstants.RejectedBySupplier;
            order.UpdatedAt = DateTime.UtcNow;
            mutated         = true;

            // Stamp the supplier's reason where the ORDER can actually read it. The AuditEvent below
            // is written with EntityType="PurchaseOrder", but OrdersController.Get filters audits on
            // EntityType=="Order" and otherwise falls back to the latest DeliveryAttempt -- so a
            // reason that lives only in that audit is unreachable, and the UI shows the supplier
            // rejecting the PO because of whatever the last transport error happened to be (e.g. a
            // gateway timeout) instead of the real reason. Mirrors the canonical manual path,
            // OrderResolutionService.MarkRejectedAsync (:261-269).
            if (!string.IsNullOrWhiteSpace(payload.Reason))
            {
                var latestAttempt = await _db.DeliveryAttempts
                    .Where(a => a.OrgId == orgId && a.OrderId == order.Id)
                    .OrderByDescending(a => a.AttemptedAt)
                    .FirstOrDefaultAsync(ct);

                if (latestAttempt is not null)
                    latestAttempt.RejectionReason = payload.Reason;
            }
        }
        else if (status == "delivered" && order.Status != OrderStatusConstants.Delivered)
        {
            order.Status    = OrderStatusConstants.Delivered;
            order.UpdatedAt = DateTime.UtcNow;
            mutated         = true;
        }

        var auditPayload = JsonSerializer.Serialize(new
        {
            ReportedStatus = status,
            payload.Reason,
            OccurredAt = payload.OccurredAt ?? DateTimeOffset.UtcNow,
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            UserId     = null,
            EntityType = "PurchaseOrder",
            EntityId   = payload.OrderId,
            Action     = "webhook_status",
            Payload    = JsonDocument.Parse(auditPayload),
            CreatedAt  = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        // Exceptions are derived from the order's status, so a status write that skips reconcile
        // leaves the previous status's exception open forever: an earlier 503 opens
        // "Delivery to the supplier failed.", this callback moves the order to rejected_by_supplier,
        // and nothing re-reconciles it -- the operator reads a transport failure on an order the
        // supplier actually REJECTED. The delivered case inverts it (a stale problem on a delivered
        // order). Both statuses have a correct mapping in OrderExceptionService.ProblemFor, so one
        // reconcile fixes both. Mirrors OrderResolutionService.MarkRejectedAsync (:279).
        if (mutated)
            await SafeReconcileExceptionsAsync(orgId, payload.OrderId, ct);

        _logger.LogInformation(
            "Webhook status={Status} received for order {OrderId} (org {OrgId}).",
            status, payload.OrderId, orgId);

        return Ok(new { ok = true, orderId = payload.OrderId, status = order.Status });
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reconcile the order's exceptions, never failing the supplier's callback if it goes wrong.
    /// The status write is already committed by this point, so a reconcile error must not turn a
    /// SUCCESSFUL status update into a 500 the supplier will retry. Same contract (and same reason)
    /// as OrderServiceShared.SafeReconcileExceptionsAsync.
    /// </summary>
    private async Task SafeReconcileExceptionsAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        try
        {
            await _exceptions.ReconcileAsync(orgId, orderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Webhook status: exception reconcile failed for order {OrderId} (org {OrgId}) — non-fatal, " +
                "the status write is already committed.",
                orderId, orgId);
        }
    }

    private async Task<(string Body, string Ts, string Nonce, string Sig)> ReadHeadersAndBodyAsync(
        CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var ts    = Request.Headers["X-ProcuLink-Timestamp"].FirstOrDefault() ?? string.Empty;
        var nonce = Request.Headers["X-ProcuLink-Nonce"].FirstOrDefault()     ?? string.Empty;
        var sig   = Request.Headers["X-ProcuLink-Signature"].FirstOrDefault() ?? string.Empty;
        return (body, ts, nonce, sig);
    }

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "received",
        "in_progress",
        "rejected",
        "delivered",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── payload DTOs ─────────────────────────────────────────────────────

    public sealed record AcknowledgePayload(
        Guid             OrderId,
        string?          SupplierReference,
        DateTimeOffset?  AcknowledgedAt,
        string?          Notes);

    public sealed record StatusPayload(
        Guid             OrderId,
        string?          Status,
        string?          Reason,
        DateTimeOffset?  OccurredAt);
}
