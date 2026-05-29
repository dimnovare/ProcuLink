using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
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
public sealed class WebhookIngressController : ControllerBase
{
    private readonly IHmacWebhookVerifier                  _verifier;
    private readonly ProcuLinkDbContext                    _db;
    private readonly ILogger<WebhookIngressController>     _logger;

    public WebhookIngressController(
        IHmacWebhookVerifier                  verifier,
        ProcuLinkDbContext                    db,
        ILogger<WebhookIngressController>     logger)
    {
        _verifier = verifier;
        _db       = db;
        _logger   = logger;
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

        // Mutate order status only when the supplier reports a terminal/forward state.
        // We never overwrite an already-`delivered` order, and we don't move backwards
        // from delivered/delivery_failed via a "received"/"in_progress" callback.
        if (status == "delivered" && order.Status != "delivered")
        {
            order.Status    = "delivered";
            order.UpdatedAt = DateTime.UtcNow;
        }
        else if (status == "rejected" && order.Status != "delivered")
        {
            order.Status    = "delivery_failed";
            order.UpdatedAt = DateTime.UtcNow;
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

        _logger.LogInformation(
            "Webhook status={Status} received for order {OrderId} (org {OrgId}).",
            status, payload.OrderId, orgId);

        return Ok(new { ok = true, orderId = payload.OrderId, status = order.Status });
    }

    // ── helpers ──────────────────────────────────────────────────────────

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
