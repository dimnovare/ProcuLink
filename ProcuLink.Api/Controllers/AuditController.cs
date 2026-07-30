using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/audit")]
public class AuditController : ControllerBase
{
    private readonly ProcuLinkDbContext  _db;
    private readonly ICurrentTenantService _tenant;
    private readonly IBillingService      _billing;

    public AuditController(ProcuLinkDbContext db, ICurrentTenantService tenant, IBillingService billing)
    {
        _db      = db;
        _tenant  = tenant;
        _billing = billing;
    }

    /// <summary>
    /// Returns org-scoped audit events, newest first, paginated.
    /// Enriches each event with order metadata (poNumber, buyerName, supplierName, format)
    /// when EntityType == "order" or "Order".
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct     = default)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var orgId = _tenant.OrganisationId;

        // The ORG-WIDE, cross-order, paginated trail is what Operations advertises as its
        // "advanced audit trail". The per-order history that every paid plan is sold as an
        // "audit log" is GET /api/orders/{id}/audit, and that stays open on every plan --
        // gating it would take away something Growth already pays for. Keep the two apart.
        if (!await _billing.HasFeatureAsync(orgId, BillingFeature.AdvancedAudit, ct))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = BillingGateErrors.RequiresPlan("advanced_audit", BillingFeature.AdvancedAudit),
                upgradeUrl = "/settings",
            });
        }

        // Total count for pagination metadata
        var total = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.OrgId == orgId)
            .CountAsync(ct);

        // Fetch the page of events with optional user join
        var rawEvents = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.OrgId == orgId)
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.CreatedAt,
                e.EntityType,
                e.EntityId,
                e.Action,
                e.Payload,
                e.UserId,
                UserEmail = e.User != null ? e.User.Email : null,
            })
            .ToListAsync(ct);

        if (rawEvents.Count == 0)
        {
            return Ok(new { events = Array.Empty<object>(), total, page, pageSize });
        }

        // Collect order IDs for enrichment (EntityType case-insensitive "order")
        var orderEntityIds = rawEvents
            .Where(e => string.Equals(e.EntityType, "order", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.EntityId)
            .Distinct()
            .ToHashSet();

        // Load order metadata in one query (PoNumber, CanonicalJson, SourceFileKey, Supplier.Name)
        Dictionary<Guid, (string PoNumber, JsonDocument? CanonicalJson, string? SourceFileKey, string? SupplierName)>
            orderMeta = new();

        if (orderEntityIds.Count > 0)
        {
            var orders = await _db.PurchaseOrders
                .AsNoTracking()
                .Where(o => o.OrgId == orgId && orderEntityIds.Contains(o.Id))
                .Select(o => new
                {
                    o.Id,
                    o.PoNumber,
                    o.CanonicalJson,
                    o.SourceFileKey,
                    SupplierName = o.Supplier != null ? o.Supplier.Name : null,
                })
                .ToListAsync(ct);

            foreach (var o in orders)
            {
                orderMeta[o.Id] = (o.PoNumber, o.CanonicalJson, o.SourceFileKey, o.SupplierName);
            }
        }

        // Map to DTO
        var dtos = rawEvents.Select(e =>
        {
            // ── Actor resolution ────────────────────────────────────────────
            string actorType;
            string actorName;
            string actorInitials;

            var action = e.Action ?? string.Empty;
            if (action.Contains("ai", StringComparison.OrdinalIgnoreCase)
                || action.Contains("nlp", StringComparison.OrdinalIgnoreCase)
                || action.Contains("extract", StringComparison.OrdinalIgnoreCase))
            {
                actorType     = "ai";
                actorName     = "Extraction engine";
                actorInitials = "AI";
            }
            else if (e.UserId.HasValue && !string.IsNullOrWhiteSpace(e.UserEmail))
            {
                actorType = "user";
                // Derive initials from email local part (e.g. "john.doe@..." → "JD")
                var local = e.UserEmail.Split('@')[0];
                var parts = local.Split('.', '_', '-');
                actorInitials = parts.Length >= 2
                    ? $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}"
                    : local.Length >= 2
                        ? local[..2].ToUpperInvariant()
                        : local.ToUpperInvariant();
                actorName = e.UserEmail;
            }
            else
            {
                actorType     = "system";
                actorName     = "System";
                actorInitials = "SY";
            }

            // ── Order enrichment ────────────────────────────────────────────
            string? orderId      = null;
            string? poNumber     = null;
            string? buyerName    = null;
            string? supplierName = null;
            string? format       = null;

            if (string.Equals(e.EntityType, "order", StringComparison.OrdinalIgnoreCase)
                && orderMeta.TryGetValue(e.EntityId, out var meta))
            {
                orderId      = e.EntityId.ToString();
                poNumber     = meta.PoNumber;
                supplierName = meta.SupplierName;
                format       = FormatFromFileKey(meta.SourceFileKey);
                buyerName    = ExtractBuyerName(meta.CanonicalJson);
            }

            // ── Human-readable message ──────────────────────────────────────
            var message = BuildMessage(action, poNumber);

            return new
            {
                id            = e.Id.ToString(),
                ts            = e.CreatedAt.ToString("o"),
                orderId,
                poNumber,
                buyerName,
                supplierName,
                format,
                action        = action,
                actorType,
                actorName,
                actorInitials,
                message,
                payload       = (object?)(e.Payload != null
                    ? JsonSerializer.Deserialize<object>(e.Payload.RootElement.GetRawText())
                    : null),
            };
        }).ToList();

        return Ok(new { events = dtos, total, page, pageSize });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ExtractBuyerName(JsonDocument? doc)
    {
        if (doc is null) return null;
        try
        {
            if (doc.RootElement.TryGetProperty("buyerName", out var el))
                return el.GetString();
            if (doc.RootElement.TryGetProperty("BuyerName", out var el2))
                return el2.GetString();
        }
        catch { /* malformed JSON */ }
        return null;
    }

    private static string? FormatFromFileKey(string? fileKey)
    {
        if (string.IsNullOrWhiteSpace(fileKey)) return null;
        var ext = Path.GetExtension(fileKey).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "pdf"  => "PDF",
            "csv"  => "CSV",
            "xlsx" => "XLSX",
            "xls"  => "XLSX",
            "xml"  => "XML",
            "cxml" => "cXML",
            "edi"  => "EDI",
            _      => ext.ToUpperInvariant(),
        };
    }

    private static string BuildMessage(string action, string? poNumber)
    {
        var po = poNumber is not null ? $" · {poNumber}" : string.Empty;
        return action switch
        {
            var a when a.Equals("created",          StringComparison.OrdinalIgnoreCase) => $"Order created{po}",
            var a when a.Equals("parsed",           StringComparison.OrdinalIgnoreCase) => $"Order parsed{po}",
            var a when a.Equals("status_changed",   StringComparison.OrdinalIgnoreCase) => $"Status updated{po}",
            var a when a.Equals("resolved",         StringComparison.OrdinalIgnoreCase) => $"Lines resolved{po}",
            var a when a.Equals("transform_queued", StringComparison.OrdinalIgnoreCase) => $"Transform queued{po}",
            var a when a.Equals("delivered",        StringComparison.OrdinalIgnoreCase) => $"Delivered{po}",
            var a when a.Equals("delivery_failed",  StringComparison.OrdinalIgnoreCase) => $"Delivery failed{po}",
            _ => $"{action}{po}",
        };
    }
}
