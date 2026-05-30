using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Supplier order-confirmation endpoints. A confirmation is the inbound acknowledgement of a
/// purchase order: did the supplier accept / reject / change qty-date-price, which lines are
/// confirmed, and what needs review.
///
/// v1 ingestion is manual only — a structured manual-entry payload, or an uploaded file stub
/// that a human reviews. API/EDI ingestion is intentionally out of scope here.
///
/// All routes are tenant-scoped: org is resolved from the Clerk JWT by TenantResolutionMiddleware
/// and injected via ICurrentTenantService. This controller is auto-discovered; the service is
/// registered separately in Program.cs by the host.
/// </summary>
[Authorize]
[ApiController]
public sealed class OrderConfirmationController : ControllerBase
{
    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    private static readonly string[] AllowedUploadExtensions =
        { ".csv", ".xml", ".edi", ".json", ".pdf", ".txt" };

    private readonly IOrderConfirmationService            _confirmations;
    private readonly ICurrentTenantService                _tenant;
    private readonly ILogger<OrderConfirmationController> _logger;

    public OrderConfirmationController(
        IOrderConfirmationService            confirmations,
        ICurrentTenantService                tenant,
        ILogger<OrderConfirmationController> logger)
    {
        _confirmations = confirmations;
        _tenant        = tenant;
        _logger        = logger;
    }

    // ── POST /api/orders/{orderId}/confirmation ───────────────────────────────────
    // Manual entry: a structured, already-parsed confirmation payload.
    [HttpPost("api/orders/{orderId:guid}/confirmation")]
    public async Task<IActionResult> Record(
        Guid orderId, [FromBody] RecordConfirmationRequest request, CancellationToken ct)
    {
        if (request.Lines is null || request.Lines.Count == 0)
            return BadRequest(new { error = "At least one confirmation line is required." });

        if (!string.IsNullOrWhiteSpace(request.Status)
            && !Core.Constants.OrderConfirmationStatus.IsValid(request.Status))
            return BadRequest(new { error = $"Unknown confirmation status '{request.Status}'." });

        var orgId = _tenant.OrganisationId;

        var data = new ParsedConfirmationData(
            Status:            request.Status,
            SupplierReference: request.SupplierReference,
            Notes:             request.Notes,
            ReceivedAt:        request.ReceivedAt,
            Lines: request.Lines.Select(l => new ParsedConfirmationLineData(
                PurchaseOrderLineId:   l.PurchaseOrderLineId,
                LineNumber:            l.LineNumber,
                ConfirmedQuantity:     l.ConfirmedQuantity,
                ConfirmedUnitPrice:    l.ConfirmedUnitPrice,
                ConfirmedDeliveryDate: l.ConfirmedDeliveryDate,
                IsRejected:            l.IsRejected,
                Note:                  l.Note)).ToList());

        try
        {
            var confirmation = await _confirmations.RecordAsync(orgId, orderId, data, ct);
            _logger.LogInformation(
                "Order confirmation {ConfirmationId} recorded for order {OrderId} (status {Status}).",
                confirmation.Id, orderId, confirmation.Status);
            return CreatedAtAction(nameof(GetById), new { id = confirmation.Id }, Project(confirmation));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── POST /api/orders/{orderId}/confirmation/upload ────────────────────────────
    // Manual upload: store the supplier's confirmation document as a stub for human review.
    [HttpPost("api/orders/{orderId:guid}/confirmation/upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(Guid orderId, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedUploadExtensions.Contains(ext))
            return BadRequest(new
            {
                error = $"Unsupported file type '{ext}'. Supported: {string.Join(", ", AllowedUploadExtensions)}",
            });

        var orgId = _tenant.OrganisationId;

        try
        {
            await using var stream = file.OpenReadStream();
            var confirmation = await _confirmations.RecordUploadStubAsync(
                orgId, orderId, stream, file.FileName, file.ContentType ?? "application/octet-stream", ct);
            _logger.LogInformation(
                "Order confirmation {ConfirmationId} uploaded for order {OrderId} (needs review).",
                confirmation.Id, orderId);
            return CreatedAtAction(nameof(GetById), new { id = confirmation.Id }, Project(confirmation));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── GET /api/orders/{orderId}/confirmation ────────────────────────────────────
    // List all confirmations recorded against an order (newest first).
    [HttpGet("api/orders/{orderId:guid}/confirmation")]
    public async Task<IActionResult> ListForOrder(Guid orderId, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var list  = await _confirmations.ListForOrderAsync(orgId, orderId, ct);
        return Ok(list.Select(Project));
    }

    // ── GET /api/order-confirmations/{id} ─────────────────────────────────────────
    // Fetch a single confirmation (with lines) by its own id.
    [HttpGet("api/order-confirmations/{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var orgId        = _tenant.OrganisationId;
        var confirmation = await _confirmations.GetAsync(orgId, id, ct);
        if (confirmation is null) return NotFound();
        return Ok(Project(confirmation));
    }

    // ── Projection ────────────────────────────────────────────────────────────────

    private static object Project(Core.Entities.OrderConfirmationEntity c) => new
    {
        c.Id,
        c.PurchaseOrderId,
        c.Status,
        c.SupplierReference,
        c.Source,
        c.SourceFileName,
        c.ReceivedAt,
        c.Notes,
        c.CreatedAt,
        c.UpdatedAt,
        Lines = c.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new
            {
                l.Id,
                l.PurchaseOrderLineId,
                l.LineNumber,
                l.BuyerItemCode,
                l.SupplierItemCode,
                l.OrderedQuantity,
                l.OrderedUnitPrice,
                l.OrderedDeliveryDate,
                l.ConfirmedQuantity,
                l.ConfirmedUnitPrice,
                l.ConfirmedDeliveryDate,
                l.State,
                l.Note,
            }),
    };
}
