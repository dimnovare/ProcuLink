using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Helpers;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;

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
    private readonly IIdempotencyService       _idempotency;

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>Max accepted length for Idempotency-Key — guards against accidental garbage. </summary>
    private const int MaxIdempotencyKeyLength = 200;

    public OrdersController(
        IOrderService             orders,
        ICurrentTenantService     tenant,
        IBackgroundJobClient      jobs,
        ProcuLinkDbContext        db,
        ILogger<OrdersController> logger,
        IBillingService           billing,
        IIdempotencyService       idempotency)
    {
        _orders      = orders;
        _tenant      = tenant;
        _jobs        = jobs;
        _db          = db;
        _logger      = logger;
        _billing     = billing;
        _idempotency = idempotency;
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
        // Whitelist: CSV, XLSX, PDF (text), XML (cXML or UBL/Peppol), cXML (explicit), EDI (EDIFACT), TXT (EDIFACT-sniffed)
        if (extension != ".csv" && extension != ".xlsx" && extension != ".pdf"
            && extension != ".xml" && extension != ".cxml"
            && extension != ".edi" && extension != ".txt")
            return BadRequest(new { error = "Supported formats: CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT)." });

        if (supplierId == Guid.Empty)
            return BadRequest(new { error = "supplierId is required." });

        var orgId = _tenant.OrganisationId;

        // ── Idempotency-Key short-circuit ───────────────────────────────────
        // If the client retries an upload with the same Idempotency-Key + same
        // org inside the 24 h window, return the original order instead of
        // creating a duplicate. Outside the window the key is treated as new.
        var idempotencyKey = ExtractIdempotencyKey(Request);
        if (idempotencyKey is not null)
        {
            var existingOrderId = await _idempotency.TryGetExistingOrderIdAsync(idempotencyKey, orgId, ct);
            if (existingOrderId is not null)
            {
                var existingOrder = await _orders.GetByIdAsync(orgId, existingOrderId.Value, ct);
                if (existingOrder.IsSuccess)
                {
                    _logger.LogInformation(
                        "Idempotent upload replay for key {Key}, org {OrgId} → existing order {OrderId}",
                        idempotencyKey, orgId, existingOrderId.Value);

                    return Ok(new
                    {
                        order              = MapToDto(existingOrder.Value!),
                        validationMessages = Array.Empty<string>(),
                        idempotentReplay   = true,
                    });
                }

                // Mapped order vanished (e.g. hard-deleted) — fall through and
                // create a fresh one, then re-bind the key below.
                _logger.LogWarning(
                    "Idempotency key {Key} mapped to missing order {OrderId}; creating a new order",
                    idempotencyKey, existingOrderId.Value);
            }
        }

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

        // Bind the idempotency key to the new order. If a stale row exists
        // (>24h or pointing at a deleted order), refresh it in place; otherwise
        // insert a fresh row. We do this before enqueueing so a retry that
        // races the job sees the same order id.
        if (idempotencyKey is not null)
        {
            await _idempotency.BindAsync(idempotencyKey, orgId, stub.Id, ct);
        }

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

    /// <summary>
    /// Reads the optional <c>Idempotency-Key</c> header. Returns null for missing,
    /// blank, or absurdly long values — the caller treats those as a non-idempotent
    /// upload.
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

    // ── GET /api/orders ───────────────────────────────────────────────────────

    /// <summary>
    /// List orders for the authenticated organisation, newest first.
    /// Supports pagination and filtering by status, supplierId, date range, and
    /// a free-text search over PO number, supplier name, and buyer name.
    /// </summary>
    /// <param name="query">Pagination and filter parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResult<PurchaseOrderSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] OrderListQuery query, CancellationToken ct)
    {
        // Clamp page and pageSize to valid ranges.
        var page     = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var result = await _orders.ListPagedAsync(
            _tenant.OrganisationId,
            page,
            pageSize,
            query.Status,
            query.SupplierId,
            query.Search,
            query.DateFrom,
            query.DateTo,
            ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

        var (items, totalCount) = result.Value;
        return Ok(new PaginatedResult<PurchaseOrderSummary>(items, totalCount, page, pageSize));
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

        var entity = result.Value!;
        string? errorMessage = null;

        if (entity.Status is "failed" or "transform_failed" or "delivery_failed")
        {
            var payload = await _db.AuditEvents
                .AsNoTracking()
                .Where(e => e.EntityId == id
                         && e.OrgId == _tenant.OrganisationId
                         && e.EntityType == "Order"
                         && (e.Action == "ParseFailed"
                          || e.Action == "TransformFailed"
                          || e.Action == "DeliveryFailed"))
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.Payload)
                .FirstOrDefaultAsync(ct);

            if (payload != null)
            {
                try
                {
                    if (payload.RootElement.TryGetProperty("error", out var el))
                        errorMessage = el.GetString();
                }
                catch { /* malformed payload — ignore */ }
            }
        }

        return Ok(MapToDto(entity, errorMessage));
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
        if (formatStr != "xml" && formatStr != "csv" && formatStr != "json" && formatStr != "cxml")
            return BadRequest(new { error = "Format must be 'xml', 'csv', 'json', or 'cxml'." });

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

    // ── POST /api/orders/{id}/accept-ai-suggestions ──────────────────────────

    /// <summary>
    /// Bulk-accept AI mapping suggestions for all unresolved lines whose
    /// confidence is at or above the supplied threshold (default 0.85).
    /// Returns { accepted: N } — the count of lines that were accepted.
    /// </summary>
    [HttpPost("{id:guid}/accept-ai-suggestions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptAiSuggestions(
        Guid id,
        [FromQuery] double minConfidence = 0.85,
        CancellationToken ct = default)
    {
        var result = await _orders.AcceptAiSuggestionsAsync(
            _tenant.OrganisationId, id, minConfidence, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(new { accepted = result.Value });
    }

    // ── GET /api/orders/{id}/mapping-preview ─────────────────────────────────

    /// <summary>
    /// Read-only side-by-side mapping preview: source field → canonical PO field → AI-suggested
    /// supplier code with confidence and provenance.  Safe to call before transform/commit.
    /// Returns 404 when the order doesn't exist for the authenticated organisation.
    /// </summary>
    [HttpGet("{id:guid}/mapping-preview")]
    [ProducesResponseType(typeof(MappingPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMappingPreview(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // Org-scoped existence check — mirrors the pattern used by GetAudit.
        var order = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.Id == id && o.OrgId == orgId)
            .Select(o => new
            {
                o.Id,
                o.SourceFileKey,
            })
            .FirstOrDefaultAsync(ct);

        if (order is null)
            return NotFound();

        // Load lines with only the fields we need — no writes, no tracking.
        var lines = await _db.PurchaseOrderLines
            .AsNoTracking()
            .Where(l => l.OrderId == id)
            .OrderBy(l => l.LineNumber)
            .Select(l => new
            {
                l.LineNumber,
                l.BuyerItemCode,
                l.SupplierItemCode,
                l.Description,
                l.Quantity,
                l.Unit,
                l.UnitPrice,
                l.Confidence,
                l.AiSuggestedSupplierItemCode,
                l.AiSuggestionConfidence,
                l.AiSuggestionProvenance,
                l.AiSuggestionReason,
            })
            .ToListAsync(ct);

        // Derive source format the same way ListAsync does (from the file key extension).
        string? sourceFormat = null;
        if (!string.IsNullOrEmpty(order.SourceFileKey))
        {
            var ext = System.IO.Path.GetExtension(order.SourceFileKey).TrimStart('.').ToLowerInvariant();
            sourceFormat = ext switch
            {
                "pdf"            => "pdf",
                "csv"            => "csv",
                "xlsx" or "xls"  => "xlsx",
                "xml" or "cxml"  => "cxml",
                "edi" or "x12"   => "edi",
                _                => null,
            };
        }

        // Overall detected confidence = average of per-line Confidence values (only when lines exist).
        double? detectedConfidence = lines.Count > 0
            ? lines.Average(l => (double)l.Confidence)
            : null;

        var lineDtos = lines.Select(l =>
        {
            var sourceFields = new Dictionary<string, string?>
            {
                ["buyerItemCode"] = l.BuyerItemCode,
                ["description"]   = l.Description,
                ["quantity"]      = l.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unit"]          = l.Unit,
                ["unitPrice"]     = l.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            // Status: resolved > suggested > unresolved
            var status = !string.IsNullOrWhiteSpace(l.SupplierItemCode)
                ? "resolved"
                : !string.IsNullOrWhiteSpace(l.AiSuggestedSupplierItemCode)
                    ? "suggested"
                    : "unresolved";

            return new MappingPreviewLineDto(
                LineNumber:              l.LineNumber,
                SourceFields:            sourceFields,
                CanonicalField:          "supplierItemCode",
                BuyerItemCode:           l.BuyerItemCode,
                AiSuggestedSupplierCode: l.AiSuggestedSupplierItemCode,
                Confidence:              l.AiSuggestionConfidence.HasValue
                                             ? (double?)l.AiSuggestionConfidence.Value
                                             : null,
                Provenance:              l.AiSuggestionProvenance,
                Reason:                  l.AiSuggestionReason,
                Status:                  status
            );
        }).ToList();

        var dto = new MappingPreviewDto(
            OrderId:            order.Id.ToString(),
            SourceFormat:       sourceFormat,
            DetectedConfidence: detectedConfidence,
            Lines:              lineDtos
        );

        return Ok(dto);
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

    // ── POST /api/orders/{id}/redeliver ──────────────────────────────────────

    /// <summary>
    /// Re-enqueue delivery for an order that has already been transformed.
    /// Bypasses the AutoDeliver flag — use when the supplier was unreachable
    /// or the operator wants to force a manual retry.
    /// Valid source statuses: delivery_failed, ready_to_deliver.
    /// </summary>
    [HttpPost("{id:guid}/redeliver")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Redeliver(Guid id, CancellationToken ct)
    {
        var orgId     = _tenant.OrganisationId;
        var getResult = await _orders.GetByIdAsync(orgId, id, ct);

        if (!getResult.IsSuccess)
            return NotFound();

        var order = getResult.Value!;

        if (order.Status is not ("delivery_failed" or "ready_to_deliver"))
            return BadRequest(new
            {
                error = $"Order must be in 'delivery_failed' or 'ready_to_deliver' status to redeliver (current: '{order.Status}')."
            });

        var artifact = order.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        if (artifact is null)
            return BadRequest(new { error = "No outbound artifact found. Transform the order before redelivering." });

        order.Status = "delivering";
        await _db.SaveChangesAsync(ct);

        DeliverOrderJob.EnqueueRedeliver(_jobs, id, orgId, artifact.Id);

        _logger.LogInformation(
            "RedeliverOrderJob enqueued for order {OrderId}, artifact {ArtifactId}, org {OrgId}",
            id, artifact.Id, orgId);

        return Accepted(new { status = "delivering" });
    }

    // ── POST /api/orders/{id}/retry-delivery ─────────────────────────────────

    /// <summary>
    /// Operator-triggered delivery retry with dead-letter escalation.
    /// Enqueues <see cref="RetryDeliveryJob"/> that re-dispatches the latest artifact;
    /// after the attempt cap the order moves to <c>delivery_dead_letter</c>.
    /// Only valid from <c>delivery_failed</c>.
    /// </summary>
    [HttpPost("{id:guid}/retry-delivery")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryDelivery(Guid id, CancellationToken ct)
    {
        var orgId     = _tenant.OrganisationId;
        var getResult = await _orders.GetByIdAsync(orgId, id, ct);

        if (!getResult.IsSuccess)
            return NotFound();

        var order = getResult.Value!;

        if (order.Status == OrderStatusConstants.DeliveryDeadLetter)
            return BadRequest(new { error = "Order is in dead-letter state — delivery retries are exhausted." });

        if (order.Status != OrderStatusConstants.DeliveryFailed)
            return BadRequest(new
            {
                error = $"Order must be in 'delivery_failed' status to retry delivery (current: '{order.Status}')."
            });

        var artifact = order.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        if (artifact is null)
            return BadRequest(new { error = "No outbound artifact found. Transform the order before retrying delivery." });

        // Optimistic status flip so the UI reflects the retry immediately.
        var tracked = await _db.PurchaseOrders
            .Where(o => o.Id == id && o.OrgId == orgId)
            .FirstOrDefaultAsync(ct);
        if (tracked is not null)
        {
            tracked.Status = OrderStatusConstants.Delivering;
            tracked.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        RetryDeliveryJob.Enqueue(_jobs, id, orgId);

        _logger.LogInformation("RetryDeliveryJob enqueued for order {OrderId}, org {OrgId}", id, orgId);

        return Accepted(new { status = "delivering" });
    }

    // ── POST /api/orders/{id}/mark-rejected ──────────────────────────────────

    /// <summary>
    /// Manually mark an order as rejected by the supplier — for use when the
    /// rejection arrived out-of-band (email, phone, EDI acknowledgement) rather
    /// than as an HTTP 4xx response during automated delivery.
    /// Sets status to <c>rejected_by_supplier</c>, records the reason on the most
    /// recent delivery attempt, and writes a <c>MarkedRejected</c> audit event.
    /// </summary>
    [HttpPost("{id:guid}/mark-rejected")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRejected(
        Guid id,
        [FromBody] MarkRejectedRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = "Reason is required." });

        var result = await _orders.MarkRejectedAsync(
            _tenant.OrganisationId, id, request.Reason.Trim(), ct);

        if (!result.IsSuccess)
        {
            if (result.Error == "Order not found.")
                return NotFound();
            return BadRequest(new { error = result.Error });
        }

        return Ok(MapToDto(result.Value!));
    }

    // ── GET /api/orders/dead-letter-count ────────────────────────────────────

    /// <summary>
    /// Ops metric: count of orders in <c>delivery_dead_letter</c> state for this org.
    /// </summary>
    [HttpGet("dead-letter-count")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDeadLetterCount(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var count = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.Status == OrderStatusConstants.DeliveryDeadLetter, ct);
        return Ok(new { count });
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

    private static OrderDto MapToDto(PurchaseOrderEntity e, string? errorMessage = null) => new(
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
            .ToList(),
        BuyerName:    ExtractBuyerName(e),
        ErrorMessage: errorMessage
    );

    /// <summary>
    /// Extracts BuyerName from the canonical JSON if parsing has completed.
    /// Returns null if the order is still parsing or CanonicalJson is absent.
    /// </summary>
    private static string? ExtractBuyerName(PurchaseOrderEntity e)
    {
        if (e.CanonicalJson is null) return null;
        try
        {
            if (e.CanonicalJson.RootElement.TryGetProperty("buyerName", out var el))
                return el.GetString();
            // camelCase variant
            if (e.CanonicalJson.RootElement.TryGetProperty("BuyerName", out var el2))
                return el2.GetString();
        }
        catch { /* malformed JSON — ignore */ }
        return null;
    }

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
