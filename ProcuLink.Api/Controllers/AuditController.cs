using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
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

    public AuditController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db      = db;
        _tenant  = tenant;
    }

    /// <summary>
    /// Returns org-scoped audit events, newest first, paginated.
    /// Enriches each event with order metadata (poNumber, buyerName, supplierName, format)
    /// when EntityType == "order" or "Order".
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? flowId = null,
        [FromQuery] string[]? actions = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? q = null,
        CancellationToken ct     = default)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var orgId = _tenant.OrganisationId;

        // One filtered query for both the count and the page
        var events = _db.AuditEvents.AsNoTracking().Where(e => e.OrgId == orgId);
        if (actions is { Length: > 0 }) events = events.Where(e => actions.Contains(e.Action));
        if (from is { } fromAt) { var since = fromAt.UtcDateTime; events = events.Where(e => e.CreatedAt >= since); }
        if (to is { } toAt) { var until = toAt.UtcDateTime; events = events.Where(e => e.CreatedAt < until); }
        // A flow or a text narrows to events of documents that still exist in this workspace.
        var match = DocumentSearch.Matches(q);
        if (flowId is not null || match is not null)
        {
            var docs = _db.FlowDocuments.Where(d => d.OrgId == orgId);
            if (flowId is { } f) docs = docs.Where(d => d.FlowId == f);
            if (match is not null) docs = docs.Where(match);
            events = events.Where(e => e.EntityType == "FlowDocument" && docs.Any(d => d.Id == e.EntityId));
        }

        var total = await events.CountAsync(ct);

        // Fetch the page of events with optional user join
        var rawEvents = await events
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id)
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

        // Load document names in one query (FileName is what DocumentSummaryDto shows)
        var docIds = rawEvents.Where(e => e.EntityType == "FlowDocument").Select(e => e.EntityId).Distinct().ToList();
        var docMeta = docIds.Count == 0
            ? new Dictionary<Guid, (Guid FlowId, string FileName)>()
            : (await _db.FlowDocuments.AsNoTracking()
                .Where(d => d.OrgId == orgId && docIds.Contains(d.Id))
                .Select(d => new { d.Id, d.FlowId, d.FileName })
                .ToListAsync(ct)).ToDictionary(d => d.Id, d => (d.FlowId, d.FileName));

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

            // ── Document enrichment ─────────────────────────────────────────
            Guid? documentId = null;
            Guid? flowId = null;
            string? documentName = null;
            if (e.EntityType == "FlowDocument" && docMeta.TryGetValue(e.EntityId, out var doc))
            {
                documentId   = e.EntityId;
                flowId       = doc.FlowId;
                documentName = doc.FileName;
            }

            // ── Human-readable message ──────────────────────────────────────
            var message = documentName is not null && DocumentMessages.TryGetValue(action, out var label)
                ? $"{label} · {documentName}"
                : BuildMessage(action, poNumber);

            return new
            {
                id            = e.Id.ToString(),
                ts            = e.CreatedAt.ToString("o"),
                orderId,
                poNumber,
                buyerName,
                supplierName,
                format,
                documentId,
                flowId,
                documentName,
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

    private static readonly Dictionary<string, string> DocumentMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["document.received"]        = "Document received",
        ["document.read"]            = "Document read",
        ["document.read_failed"]     = "Couldn't read",
        ["document.retry_read"]      = "Read again",
        ["document.value_edited"]    = "Value changed",
        ["document.value_taught"]    = "Value taught",
        ["document.sent"]            = "Sent",
        ["document.delivered"]       = "Delivered",
        ["document.delivery_failed"] = "Couldn't send",
    };

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
