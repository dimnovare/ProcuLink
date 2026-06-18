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
using ProcuLink.Transform.Catalog;

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
    private readonly ILogger<IngressController> _logger;

    /// <summary>Max accepted length for Idempotency-Key — guards against accidental garbage.</summary>
    private const int MaxIdempotencyKeyLength = 200;

    public IngressController(
        ProcuLinkDbContext        db,
        IIdempotencyService       idempotency,
        ICurrentTenantService     tenant,
        ILogger<IngressController> logger)
    {
        _db          = db;
        _idempotency = idempotency;
        _tenant      = tenant;
        _logger      = logger;
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
        CancellationToken ct)
    {
        if (!await SlugMatchesCallerAsync(slug, ct))
            return Forbid();

        if (req?.Lines is null || req.Lines.Count == 0)
            return BadRequest(new { error = "Order must have at least one line." });

        var orgId = _tenant.OrganisationId;

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
            )).ToList(),
            // Workshop P0 — lossless capture for PUSHED orders. A pushed order has no stored source
            // FILE, so the only way it can expose draggable source fields in the Order Workshop is a
            // persisted SourceCapture. Project every pushed field (header + per line) into the
            // raw-fields bag; CreateStubFromParsedOrderAsync persists it as the one-per-order capture
            // (addressable as raw:{label} by a SourceMap rule). Null/blank values are dropped there.
            RawFields: BuildPushedRawFields(req)
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

    // ── POST /api/ingress/{slug}/catalog/{supplierId} — supplier catalog push ──

    /// <summary>
    /// Machine-to-machine supplier-catalog push (BE-7, plan 2026-06-12). GUID-only
    /// supplier segment (route-constrained — no name/code resolution). Two body forms:
    /// <c>multipart/form-data</c> with a <c>file</c> part (CSV or XLSX, routed by file
    /// name), or a raw CSV body (<c>text/csv</c> / <c>application/octet-stream</c>).
    /// Caps: 10 MB request body (413) and 50,000 rows (400). Idempotent by the
    /// (org, supplier, code) natural key — replaying the same file is a no-op (0 created),
    /// so no Idempotency-Key header is needed. Rate-limited per API key via the
    /// <c>"upload"</c> policy (the ApiKey principal carries <c>sub = apikey:{id}</c>).
    /// Response is byte-compatible with the browser import:
    /// <c>{ created, updated, skipped, total }</c>.
    /// </summary>
    [HttpPost("catalog/{supplierId:guid}")]
    [EnableRateLimiting("upload")]
    [RequestSizeLimit(IngressLimits.MaxFileBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> PushCatalog(
        string slug,
        Guid supplierId,
        [FromServices] ISupplierCatalogService catalog,
        CancellationToken ct)
    {
        if (!await SlugMatchesCallerAsync(slug, ct))
            return Forbid();

        var orgId = _tenant.OrganisationId;

        // Org-scoped, soft-delete-aware supplier check: foreign/unknown/deleted → 404.
        var supplierExists = await _db.Suppliers
            .AnyAsync(s => s.OrgId == orgId && s.Id == supplierId && s.DeletedAt == null, ct);
        if (!supplierExists)
            return NotFound(new { error = "Supplier not found." });

        List<SupplierProduct> drafts;
        try
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return BadRequest(new { error = "Multipart body must include a non-empty 'file' part." });

                await using var stream = file.OpenReadStream();
                var parsed = await SupplierCatalogFileParser.ParseByFileNameAsync(stream, file.FileName, ct);
                drafts = parsed.Drafts;
            }
            else if (IsRawCsvContentType(Request.ContentType))
            {
                var parsed = await SupplierCatalogFileParser.ParseCsvAsync(Request.Body, ct);
                drafts = parsed.Drafts;
            }
            else
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, new
                {
                    error = "Send multipart/form-data with a 'file' part, or a raw CSV body as text/csv.",
                });
            }
        }
        catch (CatalogTooLargeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception)
        {
            return BadRequest(new { error = "Could not read the catalog file. Provide a CSV or XLSX with a 'code' column." });
        }

        var withCode = drafts.Count(d => !string.IsNullOrWhiteSpace(d.Code));
        if (withCode == 0)
            return BadRequest(new { error = "No rows with a product code were found. Ensure the file has a 'code' column." });

        var (created, updated) = await catalog.UpsertManyAsync(orgId, supplierId, drafts, ct);

        return Ok(new
        {
            created,
            updated,
            skipped = drafts.Count - withCode,
            total   = created + updated,
        });
    }

    /// <summary>Raw-body push accepts CSV only (JSON bodies are a v2 decision).</summary>
    private static bool IsRawCsvContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true; // be lenient: parse as CSV
        var mediaType = contentType.Split(';')[0].Trim();
        return mediaType.Equals("text/csv", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
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
