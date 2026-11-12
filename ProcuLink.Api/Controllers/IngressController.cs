using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Ingress;
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
    private readonly ICurrentTenantService     _tenant;
    private readonly IBillingService           _billing;
    private readonly ILogger<IngressController> _logger;
    private readonly IOrderIntakeSwitch?       _intakeSwitch;

    /// <summary>Max accepted length for Idempotency-Key — guards against accidental garbage.</summary>
    private const int MaxIdempotencyKeyLength = 200;

    public IngressController(
        ProcuLinkDbContext        db,
        IIdempotencyService       idempotency,
        ICurrentTenantService     tenant,
        IBillingService           billing,
        ILogger<IngressController> logger,
        IOrderIntakeSwitch?       intakeSwitch = null)
    {
        _db          = db;
        _idempotency = idempotency;
        _tenant      = tenant;
        _billing     = billing;
        _logger      = logger;
        _intakeSwitch = intakeSwitch;
    }

    private async Task<bool> SlugMatchesCallerAsync(string slug, CancellationToken ct)
    {
        // Unified resolution: ApiKeyAuthHandler publishes the internal org UUID into
        // HttpContext.Items (same value-space as the JWT path), which ICurrentTenantService
        // reads — no per-controller org_id claim parsing.
        var orgId = _tenant.OrganisationId;
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
        [FromServices] IClaimedOrderCreator claimedOrders,
        CancellationToken ct)
    {
        if (!await SlugMatchesCallerAsync(slug, ct))
            return Forbid();

        var orgId = _tenant.OrganisationId;
        if (_intakeSwitch?.IsRetired(orgId) == true) return StatusCode(410, new { error = "orders_retired" });

        if (req?.Lines is null || req.Lines.Count == 0)
            return BadRequest(new { error = "Order must have at least one line." });

        // ── Idempotency: CLAIM-FIRST, not check-then-create ─────────────────
        // Zapier/Make.com deliver at-least-once, so the same logical order can POST here multiple
        // times — and two of those deliveries can be in flight at once. Honour an explicit
        // Idempotency-Key header; when absent, derive a stable key from (slug + PO number + line
        // shape) so a verbatim retry of the same payload is also deduplicated.
        //
        // The claim row is committed BEFORE the order is created, and the (org_id, key) primary key
        // is the real guard — the same claim-first ordering the SFTP/S3/IMAP pull channels use (see
        // IngressDedupe). A check-then-create pair let two concurrent requests both see "no row" and
        // both create an order, and a crash between create and bind duplicated on the next retry.
        var idempotencyKey = ExtractIdempotencyKey(Request) ?? DeriveIdempotencyKey(slug, req);
        var claim = await _idempotency.ClaimAsync(idempotencyKey, orgId, ct);

        if (!claim.IsNew)
        {
            // A live claim already exists: this is a replay, or we lost a concurrent race. Either
            // way the answer is the claim's order — never a second one.
            var existingOrder = await orders.GetByIdAsync(orgId, claim.OrderId, ct);
            if (existingOrder.IsSuccess)
            {
                _logger.LogInformation(
                    "Idempotent ingress replay for key {Key}, org {OrgId} → existing order {OrderId}",
                    idempotencyKey, orgId, claim.OrderId);

                return Ok(new
                {
                    existingOrder.Value!.Id,
                    existingOrder.Value.Status,
                    LinesCount       = existingOrder.Value.Lines.Count,
                    idempotentReplay = true,
                });
            }

            // The claim exists but its order does not. Two ways to get here, and the same action
            // serves both: a transient failure abandoned an earlier attempt (RESUME), or the winner
            // of a concurrent race has claimed but not yet committed (we recreate under its id).
            // Creation is a find-or-create on that primary key, so neither can yield a second order,
            // and a key whose order was never created is never permanently blocked.
            _logger.LogInformation(
                "Idempotency key {Key} for org {OrgId} claims order {OrderId} which does not exist yet; " +
                "creating under the claimed id (resume).",
                idempotencyKey, orgId, claim.OrderId);
        }

        // ── Billing gate ───────────────────────────────────────────────────
        // Byte-for-byte the gate OrdersController.Upload applies. REST ingress had NO
        // billing gate at all, so a frozen / trial-expired org whose browser uploads were
        // already being refused could keep pushing orders through the API indefinitely.
        //
        // Placed AFTER the idempotency short-circuit on purpose: an at-least-once retry of
        // an order that was already accepted must keep returning that order, not start
        // 429-ing once the account state changes.
        //
        // CheckOrderLimitAsync is the SOFT-CAP-SAFE gate: its Allowed flag is
        // BillingStatus.CanProcessOrders, which for an active paid plan ignores volume
        // entirely. Going over the monthly allowance still succeeds here and accrues
        // €0.50/order overage — only a non-processing account status (or an expired Pilot,
        // whose cap is a trial limit rather than a volume block) refuses.
        var limitCheck = await _billing.CheckOrderLimitAsync(orgId, ct);
        if (!limitCheck.Allowed)
        {
            _logger.LogInformation(
                "Ingress push refused for org {OrgId}: plan {Plan}, pilotExpired={PilotExpired}",
                orgId, limitCheck.Plan, limitCheck.PilotExpired);

            return StatusCode(429, new
            {
                error      = limitCheck.PilotExpired ? "pilot_expired" : "order_limit_reached",
                plan       = limitCheck.Plan,
                limit      = limitCheck.Limit,
                upgradeUrl = "/settings",
            });
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
            )).ToList(),
            // Workshop P0 — lossless capture for PUSHED orders. A pushed order has no stored source
            // FILE, so the only way it can expose draggable source fields in the Order Workshop is a
            // persisted SourceCapture. Project every pushed field (header + per line) into the
            // raw-fields bag; CreateStubFromParsedOrderAsync persists it as the one-per-order capture
            // (addressable as raw:{label} by a SourceMap rule). Null/blank values are dropped there.
            RawFields: BuildPushedRawFields(req)
        );

        // Create under the CLAIMED id (find-or-create on that primary key). No BindAsync afterwards:
        // the claim is already durable, so there is no longer a window between the order existing
        // and the key pointing at it — which is exactly the window that used to duplicate.
        var result = await claimedOrders.CreateClaimedFromParsedOrderAsync(
            orgId, supplierId, claim.OrderId, extracted, "ingress_api", inboundSenderDomain: null, ct);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

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
    /// Workshop P0 — projects every field of a PUSHED order payload into a flat raw-fields bag
    /// (header + per-line) so the persisted <see cref="ProcuLink.Core.Services.Ai.ExtractedOrder"/>
    /// carries a lossless <c>SourceCapture</c>. Without a stored source file, this bag is the only
    /// way a pushed order exposes draggable source fields in the Order Workshop. Labels are stable
    /// and human-readable ("Line 1 · Buyer item code"); blank values are dropped downstream by
    /// <c>BuildSourceCapture</c>. Pure — no I/O.
    /// </summary>
    private static IReadOnlyList<ExtractedRawField> BuildPushedRawFields(IngressOrderRequest req)
    {
        var fields = new List<ExtractedRawField>();

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                fields.Add(new ExtractedRawField(label, value!.Trim()));
        }

        // Header-level pushed fields.
        Add("Order number", req.OrderNumber);
        Add("Order date", req.OrderDate?.ToString("yyyy-MM-dd"));
        Add("Currency", req.Currency);
        Add("Notes", req.Notes);
        Add("Supplier", req.SupplierId);

        // Per-line pushed fields — one labelled entry per non-blank value.
        for (var i = 0; i < req.Lines.Count; i++)
        {
            var line = req.Lines[i];
            var n = i + 1;
            Add($"Line {n} · Buyer item code", line.BuyerItemCode);
            Add($"Line {n} · Description", line.Description);
            Add($"Line {n} · Quantity", line.Quantity.ToString(CultureInfo.InvariantCulture));
            Add($"Line {n} · Unit", line.Unit);
            Add($"Line {n} · Unit price", line.UnitPrice.ToString(CultureInfo.InvariantCulture));
        }

        return fields;
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
