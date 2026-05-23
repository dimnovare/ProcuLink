using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Helpers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Phase 2 order lifecycle endpoints.
/// All routes are tenant-scoped — org is resolved from the Clerk JWT by
/// TenantResolutionMiddleware and injected via ICurrentTenantService.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService         _orders;
    private readonly ICurrentTenantService _tenant;
    private readonly ILogger<OrdersController> _logger;

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    public OrdersController(
        IOrderService         orders,
        ICurrentTenantService tenant,
        ILogger<OrdersController> logger)
    {
        _orders = orders;
        _tenant = tenant;
        _logger = logger;
    }

    // ── POST /api/orders/upload ───────────────────────────────────────────────

    /// <summary>
    /// Upload a CSV or XLSX purchase order file.
    /// The file is stored to R2, parsed, and item codes auto-resolved from saved mappings.
    /// Rate-limited to 20 uploads per minute per authenticated user.
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] Guid supplierId,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "File is required." });

        if (file.Length > MaxUploadBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                new { error = "File exceeds the 10 MB upload limit." });

        var extension = FileNameSanitiser.GetExtension(file.FileName);
        if (extension != ".csv" && extension != ".xlsx")
            return BadRequest(new { error = "Only CSV and XLSX files are supported." });

        if (supplierId == Guid.Empty)
            return BadRequest(new { error = "supplierId is required." });

        var orgId = _tenant.OrganisationId;

        await using var stream = file.OpenReadStream();
        var result = await _orders.CreateFromFileAsync(
            orgId, supplierId, stream, file.FileName, file.ContentType, ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

        _logger.LogInformation("Order {Id} created via upload for org {OrgId}", result.Value!.Id, orgId);

        return Ok(new
        {
            order              = MapToDto(result.Value!),
            validationMessages = Array.Empty<string>()
        });
    }

    // ── GET /api/orders ───────────────────────────────────────────────────────

    /// <summary>List all orders for the authenticated organisation, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PurchaseOrderSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _orders.ListAsync(_tenant.OrganisationId, ct);
        // ListAsync always succeeds — no failure path for a plain list
        return Ok(result.Value);
    }

    // ── GET /api/orders/{id} ──────────────────────────────────────────────────

    /// <summary>Get a single order with its lines and artifacts.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await _orders.GetByIdAsync(_tenant.OrganisationId, id, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(MapToDto(result.Value!));
    }

    // ── POST /api/orders/{id}/resolve ─────────────────────────────────────────

    /// <summary>
    /// Apply user-supplied supplier item codes to unresolved lines.
    /// Optionally persists new mappings for future auto-resolution.
    /// </summary>
    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(
        Guid id,
        [FromBody] ResolveRequest request,
        CancellationToken ct)
    {
        if (request.LineResolutions is null || request.LineResolutions.Count == 0)
            return BadRequest(new { error = "At least one line resolution is required." });

        // Map the HTTP contract type to the Core service type
        var resolutions = request.LineResolutions
            .Select(r => new Core.Services.LineResolution(r.LineNumber, r.SupplierItemCode))
            .ToList();

        var result = await _orders.ResolveAsync(
            _tenant.OrganisationId, id,
            resolutions,
            request.SaveMappings,
            ct);

        if (!result.IsSuccess)
        {
            if (result.Error == "Order not found.")
                return NotFound();
            return BadRequest(new { error = result.Error });
        }

        return Ok(MapToDto(result.Value!));
    }

    // ── POST /api/orders/{id}/transform ──────────────────────────────────────

    /// <summary>
    /// Transform a fully-resolved order to XML or CSV.
    /// All lines must have NeedsReview = false — returns 422 otherwise.
    /// Uploads the artifact to R2 and advances the order status to "delivered".
    /// </summary>
    [HttpPost("{id:guid}/transform")]
    [ProducesResponseType(typeof(TransformResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Transform(
        Guid id,
        [FromBody] TransformRequest request,
        CancellationToken ct)
    {
        var format = request.Format?.ToLowerInvariant() switch
        {
            "xml" => (OutputFormat?)OutputFormat.Xml,
            "csv" => (OutputFormat?)OutputFormat.Csv,
            _     => null
        };

        if (format is null)
            return BadRequest(new { error = "Format must be 'xml' or 'csv'." });

        var result = await _orders.TransformAsync(_tenant.OrganisationId, id, format.Value, ct);

        if (!result.IsSuccess)
        {
            if (result.Error == "Order not found.")
                return NotFound();

            // Unresolved lines → 422
            if (result.Error!.StartsWith("Resolve all lines"))
                return UnprocessableEntity(new { error = result.Error });

            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    // ── GET /api/orders/{id}/artifacts/{artifactId}/download ─────────────────

    /// <summary>
    /// Returns a 15-minute pre-signed R2 URL for the given artifact.
    /// The frontend opens this URL directly — file bytes never flow through the API.
    /// </summary>
    [HttpGet("{id:guid}/artifacts/{artifactId:guid}/download")]
    [ProducesResponseType(typeof(DownloadUrl), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, Guid artifactId, CancellationToken ct)
    {
        var result = await _orders.GetDownloadUrlAsync(
            _tenant.OrganisationId, id, artifactId, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(result.Value);
    }

    // ── Mapping helper ────────────────────────────────────────────────────────

    private static OrderDto MapToDto(PurchaseOrderEntity e) => new(
        Id:            e.Id,
        PoNumber:      e.PoNumber,
        SupplierId:    e.SupplierId,
        SupplierName:  e.Supplier?.Name ?? string.Empty,
        OrderDate:     e.OrderDate.ToString("yyyy-MM-dd"),
        Currency:      e.Currency,
        Status:        e.Status,
        SourceFileKey: e.SourceFileKey,
        CreatedAt:     e.CreatedAt,
        UpdatedAt:     e.UpdatedAt,
        Lines:         e.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new OrderLineDto(
                l.Id, l.LineNumber, l.BuyerItemCode, l.SupplierItemCode,
                l.Description, l.Quantity, l.Unit, l.UnitPrice,
                l.Confidence, l.NeedsReview))
            .ToList(),
        Artifacts: e.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new ArtifactDto(a.Id, a.Format, a.FileKey, a.CreatedAt))
            .ToList()
    );
}
