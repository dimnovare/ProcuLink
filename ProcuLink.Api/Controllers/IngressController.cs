using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Inbound REST API for machine-to-machine callers (Zapier, Make.com, custom).
/// Auth: X-ProcuLink-Key header (ApiKey scheme only).
/// Slug guard: path slug must match the authenticated org.
/// </summary>
[ApiController]
[Route("api/ingress/{slug}")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public sealed class IngressController : ControllerBase
{
    private readonly ProcuLinkDbContext        _db;
    private readonly IIdempotencyService       _idempotency;
    private readonly ILogger<IngressController> _logger;

    /// <summary>Max accepted length for Idempotency-Key — guards against accidental garbage.</summary>
    private const int MaxIdempotencyKeyLength = 200;

    public IngressController(
        ProcuLinkDbContext        db,
        IIdempotencyService       idempotency,
        ILogger<IngressController> logger)
    {
        _db          = db;
        _idempotency = idempotency;
        _logger      = logger;
    }

    private async Task<bool> SlugMatchesCallerAsync(string slug, CancellationToken ct)
    {
        var orgIdClaim = User.FindFirst("org_id")?.Value;
        if (!Guid.TryParse(orgIdClaim, out var orgId)) return false;
        return await _db.Organisations.AnyAsync(o => o.Id == orgId && o.Slug == slug, ct);
    }

    // GET /api/ingress/{slug}/ping — auth test
    [HttpGet("ping")]
    public async Task<IActionResult> Ping(string slug, CancellationToken ct)
    {
        if (!await SlugMatchesCallerAsync(slug, ct))
            return Forbid();
        return Ok(new { message = "ProcuLink inbound API OK", slug, timestamp = DateTime.UtcNow });
    }

    // POST /api/ingress/{slug}/orders
    [HttpPost("orders")]
    public async Task<IActionResult> ReceiveOrder(
        string slug,
        [FromBody] IngressOrderRequest req,
        [FromServices] IOrderService orders,
        CancellationToken ct)
    {
        if (!await SlugMatchesCallerAsync(slug, ct))
            return Forbid();

        if (req?.Lines is null || req.Lines.Count == 0)
            return BadRequest(new { error = "Order must have at least one line." });

        var orgId = Guid.Parse(User.FindFirst("org_id")!.Value);

        // ── Idempotency short-circuit ───────────────────────────────────────
        // Zapier/Make.com deliver at-least-once, so the same logical order can
        // POST here multiple times. Honour an explicit Idempotency-Key header;
        // when absent, derive a stable key from (slug + PO number + line shape)
        // so a verbatim retry of the same payload is also deduplicated. Within
        // the 24 h window a replay returns the original order instead of
        // creating (and later delivering) a duplicate.
        var idempotencyKey = ExtractIdempotencyKey(Request) ?? DeriveIdempotencyKey(slug, req);
        var existingOrderId = await _idempotency.TryGetExistingOrderIdAsync(idempotencyKey, orgId, ct);
        if (existingOrderId is not null)
        {
            var existingOrder = await orders.GetByIdAsync(orgId, existingOrderId.Value, ct);
            if (existingOrder.IsSuccess)
            {
                _logger.LogInformation(
                    "Idempotent ingress replay for key {Key}, org {OrgId} → existing order {OrderId}",
                    idempotencyKey, orgId, existingOrderId.Value);

                return Ok(new
                {
                    existingOrder.Value!.Id,
                    existingOrder.Value.Status,
                    LinesCount       = existingOrder.Value.Lines.Count,
                    idempotentReplay = true,
                });
            }

            // Mapped order vanished (e.g. hard-deleted) — fall through and
            // create a fresh one, then re-bind the key below.
            _logger.LogWarning(
                "Idempotency key {Key} mapped to missing order {OrderId}; creating a new order",
                idempotencyKey, existingOrderId.Value);
        }

        // Resolve supplier by GUID or by Name (ExternalId not present on Supplier entity)
        Guid supplierId;
        if (Guid.TryParse(req.SupplierId, out var sid))
        {
            // Verify the supplier belongs to this org
            var exists = await _db.Suppliers
                .AnyAsync(s => s.OrgId == orgId && s.Id == sid && s.DeletedAt == null, ct);
            if (!exists)
                return BadRequest(new { error = $"Supplier '{req.SupplierId}' not found." });
            supplierId = sid;
        }
        else
        {
            // Fall back to matching by Name (case-insensitive)
            var supplier = await _db.Suppliers
                .Where(s => s.OrgId == orgId
                         && s.DeletedAt == null
                         && s.Name.ToLower() == req.SupplierId.ToLower())
                .FirstOrDefaultAsync(ct);
            if (supplier is null)
                return BadRequest(new { error = $"Supplier '{req.SupplierId}' not found." });
            supplierId = supplier.Id;
        }

        // Build ExtractedOrder — maps to Core.Services.Ai.ExtractedOrder shape.
        // OrderDate is DateTime? in ExtractedOrder, so convert DateOnly? → DateTime?.
        DateTime? orderDate = req.OrderDate.HasValue
            ? req.OrderDate.Value.ToDateTime(TimeOnly.MinValue)
            : null;

        var extracted = new ExtractedOrder(
            PoNumber:  req.OrderNumber,
            OrderDate: orderDate,
            BuyerName: null,
            Currency:  req.Currency,
            Lines: req.Lines.Select((l, i) => new ExtractedOrderLine(
                LineNumber:    i + 1,
                BuyerItemCode: l.BuyerItemCode ?? string.Empty,
                Description:   l.Description,
                Quantity:      l.Quantity,
                Unit:          l.Unit,
                UnitPrice:     l.UnitPrice
            )).ToList()
        );

        var result = await orders.CreateStubFromParsedOrderAsync(orgId, supplierId, extracted, "ingress_api", ct);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

        // Bind the idempotency key to the new order so an at-least-once retry
        // sees the same order id and short-circuits above.
        await _idempotency.BindAsync(idempotencyKey, orgId, result.Value!.Id, ct);

        return Ok(new
        {
            result.Value!.Id,
            result.Value.Status,
            LinesCount = result.Value.Lines.Count,
        });
    }

    /// <summary>
    /// Reads the optional <c>Idempotency-Key</c> header. Returns null for missing,
    /// blank, or absurdly long values — the caller then derives a payload-based key.
    /// </summary>
    private static string? ExtractIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var raw))
            return null;

        var value = raw.ToString();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxIdempotencyKeyLength)
            return null;

        return trimmed;
    }

    /// <summary>
    /// Derives a stable idempotency key from the org slug + the order payload
    /// (PO number, currency, and the line shape) when the caller did not send an
    /// explicit <c>Idempotency-Key</c>. Two verbatim retries of the same body
    /// therefore deduplicate, while genuinely different orders get distinct keys.
    /// Prefixed so it can never collide with a client-supplied header value.
    /// </summary>
    private static string DeriveIdempotencyKey(string slug, IngressOrderRequest req)
    {
        var sb = new StringBuilder();
        sb.Append(slug).Append('|')
          .Append(req.OrderNumber ?? string.Empty).Append('|')
          .Append(req.OrderDate?.ToString("O") ?? string.Empty).Append('|')
          .Append(req.Currency ?? string.Empty).Append('|')
          .Append(req.SupplierId);
        foreach (var l in req.Lines)
        {
            sb.Append('|')
              .Append(l.BuyerItemCode ?? string.Empty).Append('~')
              .Append(l.Description ?? string.Empty).Append('~')
              .Append(l.Quantity.ToString(CultureInfo.InvariantCulture)).Append('~')
              .Append(l.Unit ?? string.Empty).Append('~')
              .Append(l.UnitPrice.ToString(CultureInfo.InvariantCulture));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "ingress-auto:" + Convert.ToHexString(hash);
    }
}

public sealed record IngressOrderRequest(
    string?                         OrderNumber,
    DateOnly?                       OrderDate,
    string?                         Currency,
    string?                         Notes,
    string                          SupplierId,
    IReadOnlyList<IngressOrderLine> Lines
);

public sealed record IngressOrderLine(
    string? BuyerItemCode,
    string? Description,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice
);
