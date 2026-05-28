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
    private readonly ProcuLinkDbContext _db;

    public IngressController(ProcuLinkDbContext db) => _db = db;

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

        return Ok(new
        {
            result.Value!.Id,
            result.Value.Status,
            LinesCount = result.Value.Lines.Count,
        });
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
