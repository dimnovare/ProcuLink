using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Helpers;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Phase 2/3 order lifecycle endpoints.
/// All routes are tenant-scoped — org is resolved from the Clerk JWT by
/// TenantResolutionMiddleware and injected via ICurrentTenantService.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService             _orders;
    private readonly ICurrentTenantService     _tenant;
    private readonly IBackgroundJobClient      _jobs;
    private readonly ProcuLinkDbContext        _db;
    private readonly ILogger<OrdersController> _logger;
    private readonly IBillingService           _billing;

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    public OrdersController(
        IOrderService             orders,
        ICurrentTenantService     tenant,
        IBackgroundJobClient      jobs,
        ProcuLinkDbContext        db,
        ILogger<OrdersController> logger,
        IBillingService           billing)
    {
        _orders  = orders;
        _tenant  = tenant;
        _jobs    = jobs;
        _db      = db;
        _logger  = logger;
        _billing = billing;
    }

    // ── POST /api/orders/upload ───────────────────────────────────────────────

    /// <summary>
    /// Upload a CSV, XLSX, or text-based PDF purchase order file.
    /// The file is stored to R2 and a parsing job is enqueued.
    /// Returns immediately with status "parsing" — poll GET /api/orders/{id}/status.
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
        if (extension != ".csv" && extension != ".xlsx" && extension != ".pdf")
            return BadRequest(new { error = "Only CSV, XLSX, and PDF files are supported." });

        if (supplierId == Guid.Empty)
            return BadRequest(new { error = "supplierId is required." });

        var orgId = _tenant.OrganisationId;

        // ── Billing limit check ────────────────────────────────────────────
        var limitCheck = await _billing.CheckOrderLimitAsync(orgId, ct);
        if (!limitCheck.Allowed)
        {
            return StatusCode(429, new
            {
                error      = limitCheck.PilotExpired ? "pilot_expired" : "order_limit_reached",
                plan       = limitCheck.Plan,
                limit      = limitCheck.Limit,
                upgradeUrl = "/settings",
            });
        }

        await using var stream = file.OpenReadStream();
        var result = await _orders.CreateStubAsync(
            orgId, supplierId, stream, file.FileName, file.ContentType, ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

        var stub = result.Value!;

        // Enqueue async parse — returns before parsing completes
        ParseOrderJob.Enqueue(_jobs, stub.Id, orgId);

        _logger.LogInformation(
            "Order stub {Id} created, ParseOrderJob enqueued, org {OrgId}",
            stub.Id, orgId);

        return Ok(new
        {
            order              = MapToDto(stub),
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

    // ── GET /api/orders/{id}/status ───────────────────────────────────────────

    /// <summary>
    /// Lightweight endpoint returning just { status }.
    /// Used by the frontend to poll while an order is in "parsing" or "transforming" state.
    /// </summary>
    [HttpGet("{id:guid}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(Guid id, CancellationToken ct)
    {
        var result = await _orders.GetByIdAsync(_tenant.OrganisationId, id, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(new { status = result.Value!.Status });
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
    /// Enqueue a transform job for a fully-resolved order.
    /// Returns immediately with { status: "transforming" }.
    /// Poll GET /api/orders/{id}/status until status changes.
    /// All lines must have NeedsReview = false — returns 422 otherwise.
    /// </summary>
    [HttpPost("{id:guid}/transform")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Transform(
        Guid id,
        [FromBody] TransformRequest request,
        CancellationToken ct)
    {
        var formatStr = request.Format?.ToLowerInvariant();
        if (formatStr != "xml" && formatStr != "csv")
            return BadRequest(new { error = "Format must be 'xml' or 'csv'." });

        var limitCheck = await _billing.CheckOrderLimitAsync(_tenant.OrganisationId, ct);
        if (!limitCheck.Allowed)
        {
            return StatusCode(429, new
            {
                error = limitCheck.PilotExpired ? "pilot_expired" : "order_limit_reached",
                plan = limitCheck.Plan,
                limit = limitCheck.Limit,
                upgradeUrl = "/settings",
            });
        }

        // Pre-flight: load the order to confirm it exists and is "ready"
        var getResult = await _orders.GetByIdAsync(_tenant.OrganisationId, id, ct);
        if (!getResult.IsSuccess)
            return NotFound();

        var order = getResult.Value!;
        var unresolvedLines = order.Lines.Where(l => l.NeedsReview).Select(l => l.LineNumber).ToList();
        if (unresolvedLines.Count > 0)
            return UnprocessableEntity(new
            {
                error = $"Resolve all lines before transforming. Unresolved: {string.Join(", ", unresolvedLines)}."
            });

        if (order.Status == "transforming")
            return Accepted(new { status = "transforming" }); // already in progress

        // Enqueue transform job
        TransformOrderJob.Enqueue(_jobs, id, _tenant.OrganisationId, formatStr!);

        _logger.LogInformation(
            "TransformOrderJob enqueued for order {OrderId}, format={Format}",
            id, formatStr);

        return Accepted(new { status = "transforming" });
    }

    // ── GET /api/orders/{id}/audit ────────────────────────────────────────────

    /// <summary>
    /// Returns audit events for this order, newest first.
    /// Each event: { action, payload, createdAt }
    /// </summary>
    [HttpGet("{id:guid}/audit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAudit(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // Verify the order belongs to this org
        var exists = await _db.PurchaseOrders
            .AnyAsync(o => o.Id == id && o.OrgId == orgId, ct);

        if (!exists)
            return NotFound();

        var events = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == id && e.OrgId == orgId && e.EntityType == "Order")
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new
            {
                action    = e.Action,
                payload   = e.Payload,
                createdAt = e.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(events);
    }

    // ── GET /api/orders/{id}/artifacts/{artifactId}/download ─────────────────

    /// <summary>
    /// Returns a 15-minute pre-signed URL for the given artifact.
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
                l.Confidence, l.NeedsReview, MapAiSuggestion(l)))
            .ToList(),
        Artifacts: e.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new ArtifactDto(a.Id, a.Format, a.FileKey, a.CreatedAt))
            .ToList()
    );

    private static AiMappingSuggestionDto? MapAiSuggestion(PurchaseOrderLineEntity line)
    {
        if (string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode))
            return null;

        return new AiMappingSuggestionDto(
            line.AiSuggestedSupplierItemCode,
            line.AiSuggestionConfidence ?? 0f,
            line.AiSuggestionReason ?? string.Empty,
            line.AiSuggestionProvenance ?? string.Empty);
    }
}
