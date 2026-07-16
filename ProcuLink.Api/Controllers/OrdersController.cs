using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Helpers;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Tokenizing;

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
    private readonly IOrderService                _orders;
    private readonly ICurrentTenantService        _tenant;
    private readonly IBackgroundJobClient         _jobs;
    private readonly ProcuLinkDbContext           _db;
    private readonly ILogger<OrdersController>    _logger;
    private readonly IBillingService              _billing;
    private readonly IIdempotencyService          _idempotency;
    private readonly IOrderExceptionService       _exceptionService;
    private readonly ISupplierAcceptanceService   _acceptance;
    private readonly IOrderMappingOverrideService _mappingOverrides;
    private readonly IPromoteMappingService        _promoteMapping;
    private readonly IFileStorageService          _fileStorage;
    private readonly ISourceTokenizer             _tokenizer;
    private readonly IEnumerable<ITransformService> _transformers;
    private readonly IAiSuggestionDecisionService _aiDecisions;
    private readonly ProcuLink.Transform.Conformance.IConformanceService _conformance;
    private readonly IPoMappingService            _poMappings;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;
    private readonly IConfidenceCalibrationService? _calibration;
    private readonly ICxmlCredentialResolver?      _cxmlResolver;

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>Max accepted length for Idempotency-Key — guards against accidental garbage. </summary>
    private const int MaxIdempotencyKeyLength = 200;

    /// <summary>
    /// Max accepted length for free-text header corrections (PO number, supplier display name)
    /// submitted via resolve. These are short business identifiers / names — bounding the length
    /// guards against an accidental large paste bloating the row.
    /// </summary>
    private const int MaxHeaderTextLength = 256;

    /// <summary>Output formats reachable via transform — each maps to a registered ITransformService.</summary>
    private static readonly HashSet<string> AllowedTransformFormats =
        new(StringComparer.OrdinalIgnoreCase) { "xml", "csv", "cxml", "json", "ubl", "x12" };

    public OrdersController(
        IOrderService                orders,
        ICurrentTenantService        tenant,
        IBackgroundJobClient         jobs,
        ProcuLinkDbContext           db,
        ILogger<OrdersController>    logger,
        IBillingService              billing,
        IIdempotencyService          idempotency,
        IOrderExceptionService       exceptionService,
        ISupplierAcceptanceService   acceptance,
        IOrderMappingOverrideService mappingOverrides,
        IPromoteMappingService       promoteMapping,
        IFileStorageService          fileStorage,
        ISourceTokenizer             tokenizer,
        IEnumerable<ITransformService> transformers,
        IAiSuggestionDecisionService? aiDecisions = null,
        ProcuLink.Transform.Conformance.IConformanceService? conformance = null,
        IPoMappingService?           poMappings = null,
        IEffectiveConnectionConfigResolver? effectiveConfig = null,
        IConfidenceCalibrationService? calibration = null,
        ICxmlCredentialResolver?       cxmlResolver = null)
    {
        _orders           = orders;
        _tenant           = tenant;
        _jobs             = jobs;
        _db               = db;
        _logger           = logger;
        _billing          = billing;
        _idempotency      = idempotency;
        _exceptionService = exceptionService;
        _acceptance       = acceptance;
        _mappingOverrides = mappingOverrides;
        _promoteMapping   = promoteMapping;
        _fileStorage      = fileStorage;
        _tokenizer        = tokenizer;
        _transformers     = transformers;
        _cxmlResolver     = cxmlResolver;
        // Optional in the ctor so existing positional test constructions stay valid; when DI does
        // not supply one we build the concrete recorder against the injected DbContext.
        _aiDecisions      = aiDecisions
            ?? new ProcuLink.Infrastructure.Services.AiSuggestionDecisionService(db);
        // Optional in the ctor so existing positional test constructions stay valid; the
        // conformance checker is pure/stateless, so a default instance is safe to construct.
        _conformance      = conformance
            ?? new ProcuLink.Transform.Conformance.ConformanceService();
        // Optional for the same reason; the concrete service only needs the injected DbContext.
        // Used by the mapping-override preview to resolve the SAME supplier-promoted-output
        // fallback the real transform applies (launch batch 4A), so preview == delivery.
        _poMappings       = poMappings
            ?? new ProcuLink.Infrastructure.Services.PoMappingService(db);
        // Read-parity (launch batch 7 follow-up) — revision authority for the READ surfaces: the
        // mapping-override preview and the conformance report resolve the pinned revision's bundle
        // through the SAME resolver the transform uses, so what they show equals what delivery
        // produces. Null (older positional test constructions / unregistered hosts) behaves exactly
        // like flag-OFF: the live path drives everything, byte-identical.
        _effectiveConfig  = effectiveConfig;
        // V9 confidence calibration — optional/display-only. When unsupplied (older positional test
        // constructions / unregistered hosts) every suggestion reports its RAW confidence as the
        // calibrated value with IsCalibrated=false, so behaviour is byte-identical to pre-V9.
        _calibration      = calibration;
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
        // Whitelist: CSV, XLSX, PDF (text), XML (cXML or UBL/Peppol), cXML (explicit),
        // EDI (EDIFACT or X12-sniffed), X12 (explicit ANSI X12 850), TXT (EDIFACT/X12-sniffed).
        // .x12 is included because X12OrderParser.CanParse(".x12") is true and the factory
        // content-sniffs ISA/ST*850 — without it the UI advertised "X12" but rejected .x12 files.
        if (extension != ".csv" && extension != ".xlsx" && extension != ".pdf"
            && extension != ".xml" && extension != ".cxml"
            && extension != ".edi" && extension != ".x12" && extension != ".txt")
            return BadRequest(new { error = "Supported formats: CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT/X12)." });

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
        const int MaxPageSize = 100;

        // Resolve the page window. REST-style offset/limit wins when supplied (the live bug
        // was that these were silently ignored, so any client using them got the default
        // first 25 rows). Otherwise fall back to page/pageSize. Either way, take is capped at
        // MaxPageSize so a `limit=3000` can't pull an unbounded page.
        int skip, take;
        if (query.Offset.HasValue || query.Limit.HasValue)
        {
            take = Math.Clamp(query.Limit ?? query.PageSize, 1, MaxPageSize);
            skip = Math.Max(0, query.Offset ?? 0);
        }
        else
        {
            var page     = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
            take = pageSize;
            skip = (page - 1) * pageSize;
        }

        var result = await _orders.ListWindowAsync(
            _tenant.OrganisationId,
            skip,
            take,
            query.Status,
            query.SupplierId,
            query.Search,
            query.DateFrom,
            query.DateTo,
            ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

        var (items, totalCount) = result.Value;

        // Report the window back as page/pageSize so the envelope is identical regardless of
        // which paging style the caller used. With a non-page-aligned offset, page is the
        // 1-based index of the window that starts at `skip`.
        var effectivePage = take > 0 ? (skip / take) + 1 : 1;
        return Ok(new PaginatedResult<PurchaseOrderSummary>(items, totalCount, effectivePage, take));
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

        // delivery_unconfirmed included: a parked order must surface its explanation ("we sent this
        // but never learned whether it arrived — send again or mark delivered"), or the operator
        // gets a status with no sentence and no guidance.
        if (entity.Status is "failed" or "transform_failed" or "delivery_failed"
                          or "rejected_by_supplier" or "delivery_dead_letter"
                          or OrderStatusConstants.DeliveryUnconfirmed)
        {
            var payload = await _db.AuditEvents
                .AsNoTracking()
                .Where(e => e.EntityId == id
                         && e.OrgId == _tenant.OrganisationId
                         && e.EntityType == "Order"
                         && (e.Action == "ParseFailed"
                          || e.Action == "TransformFailed"
                          || e.Action == "DeliveryFailed"
                          || e.Action == "DeliveryDeadLettered"))
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.Payload)
                .FirstOrDefaultAsync(ct);

            if (payload != null)
            {
                try
                {
                    // Parse/Transform/DeliveryFailed audit payloads use key "error"; the
                    // DeliveryDeadLettered payload uses "lastError" (DeliveryService.DeadLetterAsync).
                    if (payload.RootElement.TryGetProperty("error", out var el))
                        errorMessage = el.GetString();
                    if (errorMessage is null && payload.RootElement.TryGetProperty("lastError", out var le))
                        errorMessage = le.GetString();
                }
                catch { /* malformed payload — ignore */ }
            }

            // The branch that actually carries the park sentence: ParkUnconfirmedAsync writes it to
            // attempt.ErrorMessage, while its DeliveryUnconfirmed audit payload uses "detail" — a key
            // the block above never looks for — so this fallback is the only path that reaches it.
            if (errorMessage is null && entity.Status is "delivery_failed" or "delivery_dead_letter"
                                                      or OrderStatusConstants.DeliveryUnconfirmed)
            {
                errorMessage = await _db.DeliveryAttempts
                    .AsNoTracking()
                    .Where(a => a.OrderId == id && a.OrgId == _tenant.OrganisationId)
                    .OrderByDescending(a => a.AttemptedAt)
                    .Select(a => a.ErrorMessage)
                    .FirstOrDefaultAsync(ct);
            }

            if (errorMessage is null && entity.Status == "rejected_by_supplier")
            {
                errorMessage = await _db.DeliveryAttempts
                    .AsNoTracking()
                    .Where(a => a.OrderId == id && a.OrgId == _tenant.OrganisationId)
                    .OrderByDescending(a => a.AttemptedAt)
                    .Select(a =>
                        a.RejectionReason ??
                        a.ResponseBody ??
                        a.ErrorMessage)
                    .FirstOrDefaultAsync(ct);
            }
        }

        // V9: derive a per-line confidence-calibration overlay for any line that still carries a raw
        // AI confidence (the resolve UI surface). Display-only — it never mutates the stored raw
        // confidence. No-op (empty map → raw passthrough) when calibration is unwired.
        var calibrations = await BuildLineCalibrationsAsync(entity, ct);

        return Ok(MapToDto(entity, errorMessage, calibrations));
    }

    /// <summary>
    /// Builds the per-line confidence-calibration overlay for an order's AI suggestions, keyed by
    /// line number. Org-scoped (the calibration service only ever reads the order's own org). Only
    /// lines that carry a raw AI confidence are calibrated; everything else is omitted (the mapper
    /// then falls back to raw passthrough). Returns an empty map when calibration is not wired, so
    /// the read path is byte-identical to pre-V9 in that case.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, ProcuLink.Core.Services.CalibrationResult>>
        BuildLineCalibrationsAsync(PurchaseOrderEntity entity, CancellationToken ct)
    {
        var empty = (IReadOnlyDictionary<int, ProcuLink.Core.Services.CalibrationResult>)
            new Dictionary<int, ProcuLink.Core.Services.CalibrationResult>();

        if (_calibration is null)
            return empty;

        var map = new Dictionary<int, ProcuLink.Core.Services.CalibrationResult>();
        foreach (var line in entity.Lines)
        {
            // Only calibrate lines that actually have an AI suggestion WITH a raw confidence —
            // calibration must never invent confidence for a suggestion that had none.
            if (string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode)) continue;
            if (line.AiSuggestionConfidence is not { } raw) continue;

            map[line.LineNumber] = await _calibration.CalibrateAsync(entity.OrgId, raw, ct);
        }

        return map;
    }

    // ── GET /api/orders/{id}/status ───────────────────────────────────────────

    /// <summary>
    /// Lightweight endpoint returning just { status }.
    /// Used by the frontend to poll while an order is in "parsing" or "transforming" state.
    /// </summary>
    [HttpGet("{id:guid}/status")]
    [ProducesResponseType(typeof(OrderStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(Guid id, CancellationToken ct)
    {
        var result = await _orders.GetByIdAsync(_tenant.OrganisationId, id, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(new OrderStatusResponse(result.Value!.Status));
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
        // A resolve must do something: either resolve at least one line OR correct a
        // header field (order date / buyer / currency / PO number / supplier name).
        // Header-only edits are valid.
        var hasHeaderEdit =
            !string.IsNullOrWhiteSpace(request.OrderDate)
            || !string.IsNullOrWhiteSpace(request.BuyerName)
            || !string.IsNullOrWhiteSpace(request.Currency)
            || !string.IsNullOrWhiteSpace(request.PoNumber)
            || !string.IsNullOrWhiteSpace(request.SupplierName);

        if ((request.LineResolutions is null || request.LineResolutions.Count == 0) && !hasHeaderEdit)
            return BadRequest(new { error = "At least one line resolution or header correction is required." });

        var resolutions = request.LineResolutions is null
            ? new List<Core.Services.LineResolution>()
            : request.LineResolutions
                .Select(r => new Core.Services.LineResolution(r.LineNumber, r.SupplierItemCode))
                .ToList();

        // Validate + parse optional header corrections. PO number + the document/display
        // supplier name ARE accepted here now; the order's ROUTING supplier (SupplierId)
        // stays read-only and is never changed by this endpoint.
        DateOnly? orderDate = null;
        if (request.OrderDate is not null && !string.IsNullOrWhiteSpace(request.OrderDate))
        {
            if (!DateOnly.TryParse(
                    request.OrderDate.Trim(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsedDate))
                return BadRequest(new { error = $"OrderDate '{request.OrderDate}' is not a valid date (expected yyyy-MM-dd)." });
            orderDate = parsedDate;
        }

        string? currency = null;
        if (request.Currency is not null && !string.IsNullOrWhiteSpace(request.Currency))
        {
            var c = request.Currency.Trim();
            if (c.Length != 3 || !c.All(char.IsLetter))
                return BadRequest(new { error = "Currency must be a 3-letter alpha code (e.g. EUR)." });
            currency = c.ToUpperInvariant();
        }

        // Buyer name: trim; whitespace-only is treated as no-change (null).
        var buyerName = string.IsNullOrWhiteSpace(request.BuyerName) ? null : request.BuyerName.Trim();

        // PO number: trim; whitespace-only is no-change. Bound the length so a garbage
        // paste can't bloat the row (po_number is a short business identifier).
        string? poNumber = null;
        if (!string.IsNullOrWhiteSpace(request.PoNumber))
        {
            poNumber = request.PoNumber.Trim();
            if (poNumber.Length > MaxHeaderTextLength)
                return BadRequest(new { error = $"PoNumber must be {MaxHeaderTextLength} characters or fewer." });
        }

        // Supplier display name: trim; whitespace-only is no-change. Bounded for the same
        // reason. This is the as-printed value only — it never changes order routing.
        string? supplierName = null;
        if (!string.IsNullOrWhiteSpace(request.SupplierName))
        {
            supplierName = request.SupplierName.Trim();
            if (supplierName.Length > MaxHeaderTextLength)
                return BadRequest(new { error = $"SupplierName must be {MaxHeaderTextLength} characters or fewer." });
        }

        var header = new Core.Services.ResolveHeaderFields(
            orderDate, buyerName, currency, poNumber, supplierName);

        var result = await _orders.ResolveAsync(
            _tenant.OrganisationId, id,
            resolutions,
            request.SaveMappings,
            ct,
            header);

        if (!result.IsSuccess)
        {
            if (result.Error == "Order not found.")
                return NotFound();
            return BadRequest(new { error = result.Error });
        }

        return Ok(MapToDto(result.Value!));
    }

    // ── POST /api/orders/{id}/assign-supplier ─────────────────────────────────

    /// <summary>
    /// Routing (Phase 1): assign a supplier to an order parked <c>unrouted</c> (ingested on a
    /// content-routed channel with no known supplier). Atomically claims the order
    /// (<c>unrouted</c> → <c>parsing</c>), pins the supplier's active connection revision, and
    /// re-enqueues the parse job — which then resolves the lines against the chosen supplier through
    /// the normal path. Only an <c>unrouted</c> order can be assigned; any other state returns 409.
    /// Org-scoped; a cross-tenant / unknown order returns 404, an unknown supplier 400.
    /// </summary>
    [HttpPost("{id:guid}/assign-supplier")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignSupplier(
        Guid id, [FromBody] AssignSupplierRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (request is null || request.SupplierId == Guid.Empty)
            return BadRequest(new { error = "A supplierId is required." });

        // Validate the target supplier belongs to this org and is not soft-deleted.
        var supplierOk = await _db.Suppliers.AsNoTracking()
            .AnyAsync(s => s.Id == request.SupplierId && s.OrgId == orgId && s.DeletedAt == null, ct);
        if (!supplierOk)
            return BadRequest(new { error = "Supplier not found." });

        // Pin the supplier's active connection revision (mirrors ingest-time pinning); null = live config.
        var revisionId = await _db.SupplierConnections.AsNoTracking()
            .Where(c => c.OrgId == orgId && c.SupplierId == request.SupplierId)
            .Select(c => c.ActiveRevisionId)
            .FirstOrDefaultAsync(ct);

        // Atomic claim: only an UNROUTED order can be assigned (prevents racing / double-assign).
        var now = DateTime.UtcNow;
        var claimed = await _db.PurchaseOrders
            .Where(o => o.Id == id && o.OrgId == orgId && o.Status == OrderStatusConstants.Unrouted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.SupplierId, request.SupplierId)
                .SetProperty(o => o.ConnectionRevisionId, revisionId)
                .SetProperty(o => o.Status, OrderStatusConstants.Parsing)
                .SetProperty(o => o.UpdatedAt, now), CancellationToken.None);

        if (claimed == 0)
        {
            var exists = await _db.PurchaseOrders.AsNoTracking()
                .AnyAsync(o => o.Id == id && o.OrgId == orgId, ct);
            if (!exists)
                return NotFound();
            return Conflict(new { error = "Order is not awaiting routing (it already has a supplier or is in another state)." });
        }

        // Re-run the parse with the chosen supplier → resolves the lines through the normal path.
        ParseOrderJob.Enqueue(_jobs, id, orgId);

        var order = await _db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Supplier)
            .FirstAsync(o => o.Id == id && o.OrgId == orgId, ct);
        return Ok(MapToDto(order));
    }

    // ── GET /api/orders/{id}/mapping-override ─────────────────────────────────

    /// <summary>
    /// Returns the per-order mapping/override (heart-piece-flex Phase 1) stored in this order's
    /// canonical_json, or <c>null</c> when the order has no override. The override never changes the
    /// default transform unless an output mapping is present and the format is supported.
    /// Org-scoped: a cross-tenant order id returns 404.
    /// </summary>
    [HttpGet("{id:guid}/mapping-override")]
    [ProducesResponseType(typeof(OrderMappingOverride), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMappingOverride(Guid id, CancellationToken ct)
    {
        // Confirm the order exists for this org first so an absent override on a real order (200 null)
        // is distinguishable from a non-existent / cross-tenant order (404).
        var exists = await _db.PurchaseOrders
            .AsNoTracking()
            .AnyAsync(x => x.Id == id && x.OrgId == _tenant.OrganisationId, ct);

        if (!exists)
            return NotFound();

        var @override = await _mappingOverrides.GetAsync(_tenant.OrganisationId, id, ct);
        return Ok(@override); // 200 with null body when no override is set
    }

    // ── PUT /api/orders/{id}/mapping-override ─────────────────────────────────

    /// <summary>
    /// Upserts the per-order mapping/override into this order's canonical_json (no new table).
    /// Every <see cref="OutputFieldRule.FieldManipulators"/> entry is validated against
    /// <c>ManipulatorRegistry</c> (resolve + a dry apply) BEFORE the write, so a bad/unknown
    /// manipulator returns 400 here and can NEVER reach the transform path. Org-scoped: a
    /// cross-tenant order id returns 404.
    /// </summary>
    [HttpPut("{id:guid}/mapping-override")]
    [ProducesResponseType(typeof(OrderMappingOverride), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutMappingOverride(
        Guid id,
        [FromBody] OrderMappingOverride request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { error = "A mapping override body is required." });

        // F-1 defensive guard: "::" is the RESERVED namespace separator for bound source-token keys
        // (src::{tokenId}). A user-defined custom-field key may never contain it, so the reserved
        // namespace can never be spoofed by a custom field. (The typed OutputFieldRule.SourceToken holds
        // a BARE token id — the src:: prefix is applied only at lookup — so it needs no such guard.)
        var reservedKeyError = ValidateReservedCustomFieldKeys(request);
        if (reservedKeyError is not null)
            return BadRequest(new { error = reservedKeyError });

        // Validate every manipulator on every output rule (header + line) by attempting to
        // resolve it and apply it against an empty row. Fail at edit time, never at deliver time.
        var validationError = ValidateOverrideManipulators(request);
        if (validationError is not null)
            return BadRequest(new { error = validationError });

        var saved = await _mappingOverrides.UpsertAsync(_tenant.OrganisationId, id, request, ct);
        if (!saved)
            return NotFound();

        // Echo back the persisted override so the frontend can confirm the exact stored shape.
        var stored = await _mappingOverrides.GetAsync(_tenant.OrganisationId, id, ct);
        return Ok(stored);
    }

    /// <summary>
    /// F-1 defensive guard: a custom-field key may never contain the reserved <c>"::"</c> namespace
    /// separator (used by bound source-token keys, <c>src::{tokenId}</c>). Returns null when all keys
    /// are clean, or a human-readable error for the first offending key. Fail at edit time.
    /// </summary>
    private static string? ValidateReservedCustomFieldKeys(OrderMappingOverride @override)
    {
        foreach (var cf in @override.CustomFields ?? new List<CustomField>())
        {
            if (cf.Key is not null && cf.Key.Contains("::", StringComparison.Ordinal))
                return $"Custom field key '{cf.Key}' may not contain '::' (a reserved namespace separator).";
        }
        return null;
    }

    /// <summary>
    /// Validates every manipulator in the override's output rules AND SourceMap rules against
    /// <c>ManipulatorRegistry</c>. Returns null when all rules are valid, or a human-readable
    /// error string for the first bad rule. A bad manipulator type or bad ctor params throw here
    /// (resolve) or on the dry apply — both caught. Fail at edit time, never at deliver time.
    /// </summary>
    private static string? ValidateOverrideManipulators(OrderMappingOverride @override)
    {
        var emptyRow = new Dictionary<string, string>();

        // Validate output-side manipulators.
        if (@override.Output is not null)
        {
            foreach (var (outputKey, rule) in @override.Output.Header.Concat(@override.Output.Lines))
            {
                foreach (var entry in rule.FieldManipulators ?? new List<ManipulatorEntry>())
                {
                    try
                    {
                        var manipulator = ManipulatorRegistry.Resolve(entry.Type, entry.Params);
                        // Dry apply with an empty value + empty row — surfaces ctor/param errors that
                        // only throw on apply (e.g. an index or format param the manipulator validates).
                        _ = manipulator.Apply(null, emptyRow);
                    }
                    catch (Exception ex)
                    {
                        return $"Invalid manipulator '{entry.Type}' on output field '{rule.OutputPath}' " +
                               $"(rule key '{outputKey}'): {ex.Message}";
                    }
                }
            }
        }

        // Validate SourceMap-side manipulators (SourceFieldRule.Manipulators).
        if (@override.SourceMap is not null)
        {
            foreach (var (fieldName, sourceRule) in @override.SourceMap)
            {
                foreach (var entry in sourceRule.Manipulators ?? new List<ManipulatorEntry>())
                {
                    try
                    {
                        var manipulator = ManipulatorRegistry.Resolve(entry.Type, entry.Params);
                        _ = manipulator.Apply(null, emptyRow);
                    }
                    catch (Exception ex)
                    {
                        return $"Invalid manipulator '{entry.Type}' in SourceMap rule for field " +
                               $"'{fieldName}': {ex.Message}";
                    }
                }
            }
        }

        return null;
    }

    // ── POST /api/orders/{id}/mapping-override/preview ────────────────────────

    /// <summary>
    /// Dry-run (heart-piece-flex Phase 3): applies an override to this order IN MEMORY and returns the
    /// would-be output document. NEVER writes, NEVER changes status, NEVER delivers, NEVER touches
    /// <c>canonical_json</c>.
    ///
    /// <para><b>Two preview modes — template takes precedence:</b></para>
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       <b>WHOLE-DOCUMENT TEMPLATE</b> — when the effective override carries a non-blank
    ///       <see cref="OrderMappingOverride.OutputTemplate"/>, the draft template is rendered with
    ///       <see cref="ScribanTemplateTransformService.Render"/> (the preview overload — it does NOT
    ///       enforce the review guard, so the editor previews while lines are still being resolved).
    ///       The <c>format</c> query is IGNORED in this mode. Returns
    ///       <c>{ ok:true, output, contentType }</c> on success; on a compile/render error it returns
    ///       <b>HTTP 200</b> with <c>{ ok:false, error }</c> (NOT 400/500) so the editor can show the
    ///       error inline as the user types.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>FIELD-BY-FIELD</b> (unchanged) — no template present → the existing per-format preview
    ///       runs over <c>format=csv|json|xml|cxml|ubl|x12</c> and returns
    ///       <c>{ format, contentType, content }</c> (or <c>{ format, warning, content:null }</c> when
    ///       a transform-time guard like an unresolved line trips).
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// <para><b>Draft vs stored override.</b> The request body is OPTIONAL. When a draft
    /// <see cref="OrderMappingOverride"/> is supplied, the preview uses IT (so the editor can preview a
    /// template / field-mapping the user is still typing, before saving). When the body is omitted, the
    /// order's STORED override is used. Either way the preview is non-mutating.</para>
    ///
    /// <para><b>Pinned-revision parity (read-parity, flag-gated).</b> When
    /// <c>Connections:RevisionAuthority</c> is ON and the order is pinned to a published revision, the
    /// field-by-field preview resolves the SAME effective priority the transform applies (per-order
    /// override → the PINNED revision's output snapshot → fixed transformer; the live supplier-promoted
    /// mapping is NOT consulted) and renders in the revision's snapshotted format regardless of the
    /// <c>format</c> query — so the preview equals the delivered bytes. Flag off / unpinned is
    /// byte-identical to before.</para>
    ///
    /// <para><b>Exploratory format (<c>honorFormat=true</c>).</b> The pinned-revision swap above is the
    /// DEFAULT so the operator sees exactly what is delivered. Passing <c>honorFormat=true</c> opts into a
    /// "what would this order look like as X" preview: the swap is skipped and the <c>format</c> query is
    /// rendered from the canonical/effective order. This is read-only — it NEVER changes delivery, which
    /// stays governed by the pinned revision — and the frontend labels it clearly as not the delivered
    /// format. The default (<c>honorFormat=false</c>) is byte-for-byte the delivered-bytes preview.</para>
    ///
    /// An unknown <c>format</c> (field-by-field mode only) returns 400. A bad manipulator returns 400.
    /// Org-scoped: a cross-tenant or unknown order id returns 404.
    /// </summary>
    [HttpPost("{id:guid}/mapping-override/preview")]
    // Deterministic in-memory dry-run — NEVER writes, NEVER delivers, does NO LLM work. The
    // live mapping editor calls this repeatedly (debounced as the user types), so give it the
    // generous "preview" policy rather than competing on the shared global limiter.
    [EnableRateLimiting("preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewMappingOverride(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        OrderMappingOverride? request,
        [FromQuery] string format = "csv",
        [FromQuery] bool honorFormat = false,
        CancellationToken ct = default)
    {
        // Resolve the effective override: a supplied DRAFT body wins; otherwise the STORED override.
        // Either way this is read-only — the draft is never persisted and the stored read never mutates.
        var draftSupplied   = request is not null;
        var effectiveOverride = request
            ?? await _mappingOverrides.GetAsync(_tenant.OrganisationId, id, ct);

        // Load the order WITH lines, org-scoped, read-only (needed by BOTH preview modes).
        // Phase 2: also include the persisted SourceCapture so the native-override preview re-derives
        // SourceMap rules from the token universe — matching the actual delivered transform output.
        var order = await _db.PurchaseOrders
            .Include(o => o.Lines)
            .Include(o => o.SourceCapture)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.OrgId == _tenant.OrganisationId, ct);
        if (order is null)
            return NotFound();

        // Supplier name feeds the structured transforms / template model (Supplier?.Name). Load it
        // separately + defensively so a missing supplier can never filter the order out (an
        // Include of the required Supplier nav drops the row under the in-memory provider).
        if (order.Supplier is null && order.SupplierId != Guid.Empty)
        {
            var supplier = await _db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == order.SupplierId && s.OrgId == _tenant.OrganisationId, ct);
            if (supplier is not null)
                order.Supplier = supplier;
        }

        // ── Mode 0: STRUCTURED OUTPUT TREE (Phase B — HIGHEST precedence) ─────────
        // When the effective override carries an OutputNode tree, render the whole document from it via
        // the SAME emitter the delivery path uses (same value machinery, source tokens, and catalog), so
        // the preview equals the delivered bytes. Errors surface as { ok:false, error } at HTTP 200.
        if (effectiveOverride?.OutputTree is not null)
        {
            try
            {
                var treeTokens = ProcuLink.Transform.Output.SourceTokenSerialization
                    .FromTokensJson(order.SourceCapture?.TokensJson);
                var treeCatalog = await ProcuLink.Api.Services.OrderServiceShared.BuildCatalogLookupAsync(
                    _db, _tenant.OrganisationId, order.SupplierId ?? Guid.Empty, ct);
                var treeResult = new OutputTemplateEmitter().Emit(
                    effectiveOverride.OutputTree, order, effectiveOverride, treeTokens, treeCatalog);
                using var treeReader = new StreamReader(treeResult.Content);
                var treeContent = await treeReader.ReadToEndAsync(ct);
                return Ok(new
                {
                    format      = effectiveOverride.OutputTree.Format.ToString(),
                    contentType = treeResult.ContentType,
                    content     = treeContent,
                });
            }
            catch (TransformValidationException ex)
            {
                return Ok(new { ok = false, error = ex.Message }); // unresolved order — show inline
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Order {OrderId}: OutputTree preview failed.", id);
                return Ok(new { ok = false, error = $"Output preview failed: {ex.Message}" });
            }
        }

        // ── Mode 1: WHOLE-DOCUMENT TEMPLATE (takes precedence) ────────────────────
        // When the effective override carries a usable template, render it. A compile/render error is
        // returned as { ok:false, error } at HTTP 200 (never 400/500) so the editor shows it inline.
        if (OrderMappingOverrideReader.HasUsableTemplate(effectiveOverride))
        {
            var outcome = new ScribanTemplateTransformService()
                .Render(effectiveOverride!.OutputTemplate!, order, effectiveOverride);

            if (!outcome.Ok)
                return Ok(new { ok = false, error = outcome.Error });

            var contentType = string.IsNullOrWhiteSpace(effectiveOverride.OutputTemplateContentType)
                ? ScribanTemplateTransformService.DefaultContentType
                : effectiveOverride.OutputTemplateContentType!.Trim();

            return Ok(new { ok = true, output = outcome.Text, contentType });
        }

        // ── Mode 2: FIELD-BY-FIELD preview ─────────────────────────────────────────
        // Read-parity (launch batch 7 follow-up): resolve the pinned revision's bundle ONCE through
        // the SAME resolver the transform uses. Live bundle when the flag is off / the order is
        // unpinned / the pin is orphaned / no resolver — byte-identical to the pre-read-parity path.
        var effectiveConfig = _effectiveConfig is null
            ? EffectiveConnectionConfig.Live
            : await _effectiveConfig.ResolveAsync(_tenant.OrganisationId, order.ConnectionRevisionId, ct);

        // Config fallback — parity with OrderTransformService when neither the draft nor the stored
        // override carries a USABLE output mapping:
        //   • PINNED (flag on): the pinned revision's output snapshot drives delivery, and the live
        //     supplier-promoted mapping is NOT consulted (reproducibility) — mirror exactly that.
        //   • LIVE: the supplier's promoted PoMappingConfig.Output drives delivery (launch batch 4A).
        // Defensive: a missing/malformed/unusable mapping on either side yields null and the
        // existing behaviour (including the body-required 400 below) is unchanged.
        OrderMappingOverride? configFallback = null;
        if (!OrderMappingOverrideReader.HasUsableOutput(effectiveOverride))
            configFallback = effectiveConfig.IsRevision
                ? TryBuildPinnedRevisionOverride(effectiveConfig, effectiveOverride, id)
                : await TryBuildSupplierPromotedOverrideAsync(order.SupplierId ?? Guid.Empty, effectiveOverride, ct);

        // This path needs an override. Preserve the original contract: a request without a body (and
        // with no stored template/override AND no revision/supplier-promoted output mapping) still
        // requires a body for the field-mapping preview.
        if (!draftSupplied && effectiveOverride is null && configFallback is null)
            return BadRequest(new { error = "A mapping override body is required." });

        var fieldOverride = configFallback ?? effectiveOverride ?? request!;

        // Preview supports every entity-based output format an override can influence.
        var fmt = (format?.Trim().ToLowerInvariant()) switch
        {
            "csv"  => OutputFormat.Csv,
            "json" => OutputFormat.Json,
            "xml"  => OutputFormat.Xml,
            "cxml" => OutputFormat.CXml,
            "ubl"  => OutputFormat.Ubl,
            "x12"  => OutputFormat.X12,
            _      => (OutputFormat?)null,
        };
        if (fmt is null || !MappedTransformService.SupportsOverrideFormat(fmt.Value))
            return BadRequest(new { error = "Override preview supports csv, json, xml, cxml, ubl, and x12." });

        // Read-parity: a PINNED order delivers in the revision's snapshotted format regardless of the
        // requested one (OrderTransformService swaps it the same way), so the preview must too —
        // otherwise preview ≠ delivered bytes. Null/unparseable/unsupported snapshot → the requested
        // format stands (granular fallback, logged), exactly like the transform.
        //
        // EXPLORATORY OPT-OUT (honorFormat=true): the founder asked for a "what would this order look
        // like as X" preview. When the caller explicitly opts in, the swap is SKIPPED and the requested
        // format is rendered from the canonical/effective order — this NEVER touches delivery (still
        // governed by the pinned revision), it only changes what this read-only preview renders. The
        // default (honorFormat=false) is byte-for-byte identical to before.
        if (!honorFormat && effectiveConfig.IsRevision && !string.IsNullOrWhiteSpace(effectiveConfig.OutputFormat))
        {
            if (Enum.TryParse<OutputFormat>(effectiveConfig.OutputFormat, ignoreCase: true, out var revisionFormat)
                && MappedTransformService.SupportsOverrideFormat(revisionFormat))
            {
                if (revisionFormat != fmt)
                    _logger.LogInformation(
                        "Order {OrderId}: preview format {RevisionFormat} taken from pinned {Source} (requested {Requested}).",
                        id, revisionFormat, effectiveConfig.Source, fmt);
                fmt = revisionFormat;
            }
            else
            {
                _logger.LogWarning(
                    "Order {OrderId}: pinned {Source} output format '{Format}' is not previewable — using the requested format {Requested}.",
                    id, effectiveConfig.Source, effectiveConfig.OutputFormat, fmt);
            }
        }
        else if (!honorFormat && !effectiveConfig.IsRevision)
        {
            // LIVE (non-revision) parity (MV-4): the transform/deliver path resolves the format as
            // explicit-request ?? supplier delivery-config format ?? safe default (the transform action
            // above, lines ~1244-1257). The live mapping editor always POSTs the FE default format=csv,
            // so without this a non-revision supplier that DELIVERS non-CSV (xml/json/cxml/ubl/x12)
            // previewed as CSV — preview ≠ delivered bytes. Resolve the SAME delivery-config format and
            // swap to it so the default preview equals what "send to supplier" produces. honorFormat=true
            // (the user explicitly exploring a format) skips this, exactly like the revision swap above.
            string? deliveryFormat = null;
            try
            {
                deliveryFormat = await (
                    from c in _db.SupplierDeliveryConfigs.AsNoTracking()
                    where c.OrgId == _tenant.OrganisationId && c.SupplierId == order.SupplierId
                    select c.OutputFormat
                ).FirstOrDefaultAsync(ct);
            }
            catch (Exception ex)
            {
                // A delivery-config read failure must NEVER break the read-only preview — keep the
                // requested format (the FE csv default). Defensive parity with the other preview
                // fallbacks (TryBuildSupplierPromotedOverrideAsync / TryBuildPinnedRevisionOverride,
                // which also swallow and fall back rather than 500 the editor).
                _logger.LogWarning(ex,
                    "Order {OrderId}: could not read the supplier's live delivery format for preview — using the requested format {Requested}.",
                    id, fmt);
            }

            if (!string.IsNullOrWhiteSpace(deliveryFormat)
                && Enum.TryParse<OutputFormat>(deliveryFormat, ignoreCase: true, out var liveFormat)
                && MappedTransformService.SupportsOverrideFormat(liveFormat))
            {
                if (liveFormat != fmt)
                    _logger.LogInformation(
                        "Order {OrderId}: preview format {LiveFormat} taken from the supplier's live delivery config (requested {Requested}).",
                        id, liveFormat, fmt);
                fmt = liveFormat;
            }
            // No delivery-config format, or one that cannot be previewed → keep the requested fmt
            // (the FE csv default) exactly as before. Never a 400 here; fmt already validated above.
        }

        // Same manipulator guard as PUT — surface a bad rule at edit time, never at transform time.
        var validationError = ValidateOverrideManipulators(fieldOverride);
        if (validationError is not null)
            return BadRequest(new { error = validationError });

        // Decide native-override vs fixed-transform EXACTLY like the real transform path
        // (OrderTransformService.TransformAsync): the native CSV/JSON builder only runs when the
        // override carries a USABLE output config. An override with a null/empty Output (e.g. the
        // live preview harness POSTs '{}', or a custom-fields-only override) must NOT reach
        // MappedTransformService.Build — it throws ArgumentException("Override has no output
        // mapping config.") on a null Output, which the preview did not catch → HTTP 500. Falling
        // back to the fixed transformer yields the byte-identical preview the order would deliver.
        var useNativeOverride =
            MappedTransformService.SupportsOverride(fmt.Value)
            && OrderMappingOverrideReader.HasUsableOutput(fieldOverride);

        try
        {
            TransformResult result;
            if (useNativeOverride)
            {
                // CSV/JSON WITH a usable output mapping — the override builder emits the document natively.
                // Phase 2: rebuild the persisted token universe so SourceMap rules re-derive in the
                // preview exactly as they will at delivery time (empty when there is no capture).
                var previewTokens = ProcuLink.Transform.Output.SourceTokenSerialization
                    .FromTokensJson(order.SourceCapture?.TokensJson);
                // Phase 2: load the same supplier catalog the transform uses so a LoadCatalogProduct
                // rule previews the REAL catalog value, not "" — otherwise preview ≠ delivered bytes.
                var previewCatalog = await ProcuLink.Api.Services.OrderServiceShared.BuildCatalogLookupAsync(
                    _db, _tenant.OrganisationId, order.SupplierId ?? Guid.Empty, ct);
                result = new MappedTransformService().Build(
                    order, fieldOverride, fmt.Value, sourceTokens: previewTokens, catalogLookup: previewCatalog);
            }
            else
            {
                // Either a structured format (XML/cXML/UBL/X12), OR a CSV/JSON request with no usable
                // output config. In both cases resolve an effective entity (canonical-field overrides
                // applied; an identity clone when there are none) and run the EXISTING fixed transform —
                // byte-for-byte the no-override output for that format.
                var transformer = _transformers.FirstOrDefault(t => t.CanTransform(fmt.Value));
                if (transformer is null)
                    return BadRequest(new { error = $"No transform service registered for format '{fmt.Value}'." });

                var effective = EffectiveEntityResolver.Resolve(order, fieldOverride);
                // cXML preview parity: resolve the SAME network credentials the delivery transform
                // uses, so the previewed <Credential> identities equal what is actually sent — not the
                // legacy OrgId/SupplierId GUIDs the preview showed before.
                CxmlCredentialConfig? previewCxmlCreds = null;
                if (fmt.Value == OutputFormat.CXml && _cxmlResolver is not null)
                    previewCxmlCreds = await _cxmlResolver.ResolveAsync(_tenant.OrganisationId, order.SupplierId ?? Guid.Empty, ct);
                result = await transformer.TransformAsync(effective, fmt.Value, ct, previewCxmlCreds);
            }

            using var sr = new StreamReader(result.Content);
            var content  = await sr.ReadToEndAsync(ct);
            return Ok(new { format = fmt.Value.ToString(), contentType = result.ContentType, content });
        }
        catch (TransformValidationException ex)
        {
            // The dry-run hit the same validation a real transform would (e.g. an unresolved line).
            // Return it as a preview warning, never a 500 — the editor shows it inline.
            return Ok(new { format = fmt.Value.ToString(), warning = ex.Message, content = (string?)null });
        }
    }

    /// <summary>
    /// Preview-side twin of <c>OrderTransformService.TryReadSupplierPromotedOutputAsync</c> (launch
    /// batch 4A): reads the supplier's reusable <see cref="PoMappingConfig"/> and wraps a USABLE
    /// promoted output mapping as a synthetic per-order-shaped override (the effective override's
    /// custom fields are preserved so promoted rules referencing them still resolve; SourceMap /
    /// template are intentionally NOT carried). Returns null — leaving the existing preview
    /// behaviour untouched — when the supplier has no usable promoted output, when reading it fails,
    /// or when its manipulators would not survive a real transform (which falls back to the fixed
    /// transformer; a misleading supplier-driven preview must not be shown for that case).
    /// </summary>
    private async Task<OrderMappingOverride?> TryBuildSupplierPromotedOverrideAsync(
        Guid supplierId, OrderMappingOverride? effectiveOverride, CancellationToken ct)
    {
        try
        {
            var supplierConfig = await _poMappings.GetAsync(_tenant.OrganisationId, supplierId, ct);
            if (!OrderMappingOverrideReader.HasUsablePromotedOutput(supplierConfig))
                return null;

            var synthetic = new OrderMappingOverride
            {
                CustomFields = effectiveOverride?.CustomFields ?? new List<CustomField>(),
                Output       = supplierConfig!.Output,
            };

            return ValidateOverrideManipulators(synthetic) is null ? synthetic : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read the supplier-promoted output mapping for supplier {SupplierId} during preview.",
                supplierId);
            return null;
        }
    }

    /// <summary>
    /// Preview-side twin of <c>OrderTransformService.TryBuildRevisionOutputOverride</c> (read-parity):
    /// wraps the PINNED revision's <c>output_mapping_json</c> snapshot as a synthetic per-order-shaped
    /// override via the SAME deserialize + wrap helpers the replay engine uses
    /// (<see cref="ReplayService.DeserializeOutputConfig"/> / <see cref="ReplayService.BuildRevisionOverride"/>
    /// — custom fields preserved, SourceMap/template intentionally not carried). Returns null — leaving
    /// the fixed transformer / body-required contract untouched — for a null/blank/empty/malformed
    /// snapshot, or when its manipulators would not survive a real transform (which falls back to the
    /// fixed transformer; a misleading revision-driven preview must not be shown for that case).
    /// Never throws.
    /// </summary>
    private OrderMappingOverride? TryBuildPinnedRevisionOverride(
        EffectiveConnectionConfig effective, OrderMappingOverride? effectiveOverride, Guid orderId)
    {
        var synthetic = ReplayService.BuildRevisionOverride(
            effectiveOverride, ReplayService.DeserializeOutputConfig(effective.OutputMappingJson));
        if (synthetic is null)
            return null;

        if (ValidateOverrideManipulators(synthetic) is { } error)
        {
            _logger.LogWarning(
                "Order {OrderId}: pinned {Source} output mapping failed manipulator validation during preview ({Error}) — using the fixed transformer.",
                orderId, effective.Source, error);
            return null;
        }

        _logger.LogInformation(
            "Order {OrderId}: preview output mapping taken from pinned {Source}.", orderId, effective.Source);
        return synthetic;
    }

    // ── POST /api/orders/{id}/mapping-override/promote ───────────────────────

    /// <summary>
    /// Promotes the per-order <see cref="OrderMappingOverride"/> into the supplier's reusable PO
    /// mapping (<see cref="PoMappingConfig"/>), so the SAME layout auto-applies to future orders
    /// from this supplier without further user review. This backs the review-screen
    /// "Save mappings for &lt;supplier&gt;" button.
    ///
    /// <para>Semantics:</para>
    /// <list type="bullet">
    ///   <item>Reads the <c>SourceMap</c> AND <c>Output</c> mapping stored in this order's <c>canonical_json</c>.</item>
    ///   <item>Translates each SourceMap entry to a <see cref="FieldMappingEntry"/> (SourceToken→ExternalField,
    ///        FixedValue→FixedValue, Manipulators→FieldManipulators) and copies the output mapping verbatim.</item>
    ///   <item>Merges both into the supplier's existing <see cref="PoMappingConfig"/>
    ///        (additive — fields NOT in the override are preserved).</item>
    ///   <item>Idempotent — re-promoting the same override overwrites with identical data.</item>
    ///   <item>NEVER a silent no-op — when the order has neither a SourceMap nor an output mapping,
    ///        the supplier mapping is left unchanged and the response carries
    ///        <c>nothingToPromote: true</c> with a clear <c>message</c>.</item>
    /// </list>
    ///
    /// <para>Response shape:</para>
    /// <c>{ supplierId, headerFieldsPromoted, lineFieldsPromoted, outputHeaderFieldsPromoted,
    /// outputLineFieldsPromoted, totalFieldsPromoted, nothingToPromote, schemaFingerprintHash,
    /// message }</c>. The original four keys are preserved for backward compatibility; the rest are
    /// additive.
    ///
    /// <para>Output side:</para>
    /// The supplier-level <c>Output</c> mapping is PERSISTED + REPORTED here AND CONSUMED at
    /// transform time (launch batch 4A): when a future order from this supplier carries no usable
    /// per-order override, <c>OrderTransformService</c> (and the mapping-override preview) apply the
    /// promoted output mapping. The per-order override always stays the higher-priority seam.
    ///
    /// <para>Schema fingerprint note:</para>
    /// The mapping is stored at the supplier level, not per-fingerprint. The returned
    /// <c>schemaFingerprintHash</c> is informational — it identifies the layout this order was
    /// parsed with, but the promoted rules apply to ALL future orders from this supplier.
    ///
    /// Org-scoped: a cross-tenant or unknown order id returns 404.
    /// </summary>
    [HttpPost("{id:guid}/mapping-override/promote")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PromoteMappingOverride(Guid id, CancellationToken ct)
    {
        var result = await _promoteMapping.PromoteAsync(_tenant.OrganisationId, id, ct);

        if (result is null)
            return NotFound();

        _logger.LogInformation(
            "Mapping override promote for order {OrderId} (org {OrgId}): " +
            "supplier {SupplierId}, {Header} header + {Lines} line + {OutHeader} out-header + " +
            "{OutLines} out-line fields, nothingToPromote={Nothing}, fingerprint {Hash}",
            id, _tenant.OrganisationId,
            result.SupplierId,
            result.HeaderFieldsPromoted,
            result.LineFieldsPromoted,
            result.OutputHeaderFieldsPromoted,
            result.OutputLineFieldsPromoted,
            result.NothingToPromote,
            result.SchemaFingerprintHash ?? "(none)");

        return Ok(new
        {
            supplierId                 = result.SupplierId,
            headerFieldsPromoted       = result.HeaderFieldsPromoted,
            lineFieldsPromoted         = result.LineFieldsPromoted,
            outputHeaderFieldsPromoted = result.OutputHeaderFieldsPromoted,
            outputLineFieldsPromoted   = result.OutputLineFieldsPromoted,
            totalFieldsPromoted        = result.TotalFieldsPromoted,
            nothingToPromote           = result.NothingToPromote,
            schemaFingerprintHash      = result.SchemaFingerprintHash,
            message                    = result.Message,
        });
    }

    // ── GET /api/orders/{id}/source-tokens ────────────────────────────────────

    /// <summary>
    /// Returns the tokenizer output (every addressable value) for the order's stored source file.
    /// Used by the SourceMap editor to let users pick which source cell / XML element maps to each
    /// canonical field.
    ///
    /// <list type="bullet">
    ///   <item>Org-scoped: a cross-tenant or unknown order id returns 404.</item>
    ///   <item>Returns an empty array when the order has no stored source file, or when the file
    ///         format is not yet supported for tokenisation (e.g. XLSX, PDF, EDI).</item>
    ///   <item>Never throws for an unsupported format — the tokenizer returns an empty list.</item>
    /// </list>
    /// </summary>
    [HttpGet("{id:guid}/source-tokens")]
    [ProducesResponseType(typeof(IReadOnlyList<SourceTokenDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSourceTokens(Guid id, CancellationToken ct)
    {
        // Org-scoped existence + the persisted lossless capture.
        var order = await _db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.SourceCapture)
            .FirstOrDefaultAsync(o => o.Id == id && o.OrgId == _tenant.OrganisationId, ct);

        if (order is null)
            return NotFound();

        // T1b — prefer the PERSISTED SourceCapture captured at ingest (structured tokens OR the
        // PDF/email LLM raw_fields). This is the only way PDF/email orders expose their extracted
        // fields (the rendered file can't be re-tokenised), it survives a purged / API-pushed order
        // with no stored file, and it avoids an R2 round-trip. Only when nothing was captured do we
        // fall back to live re-tokenisation of the stored file below.
        var persisted = ProcuLink.Transform.Output.SourceTokenSerialization.FromTokensJson(order.SourceCapture?.TokensJson);
        if (persisted.Count > 0)
            return Ok(SourceTokenDto.FromMany(persisted));

        // No stored source file → empty list (valid, not an error).
        if (string.IsNullOrEmpty(order.SourceFileKey))
            return Ok(Array.Empty<SourceTokenDto>());

        // Source blob purged per the org's retention policy → same graceful empty list
        // (the SourceMap editor degrades to manual entry), never a 500 or a pointless
        // storage call for a blob we know is gone.
        if (order.SourceFilePurgedAt is not null)
            return Ok(Array.Empty<SourceTokenDto>());

        // Derive file extension from the stored R2 key.
        var ext = System.IO.Path.GetExtension(order.SourceFileKey);
        if (string.IsNullOrEmpty(ext))
            return Ok(Array.Empty<SourceTokenDto>());

        // Download the source file from R2 / local storage.
        byte[] bytes;
        try
        {
            await using var stream = await _fileStorage.DownloadAsync(order.SourceFileKey, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not download source file {Key} for tokenisation", order.SourceFileKey);
            return Ok(Array.Empty<SourceTokenDto>());
        }

        // Tokenise. Unsupported formats return an empty list (no exception).
        var tokens = await _tokenizer.TokenizeAsync(bytes, ext, ct);
        return Ok(SourceTokenDto.FromMany(tokens));
    }

    // ── POST /api/orders/{id}/transform ──────────────────────────────────────

    /// <summary>
    /// Enqueue a transform job for a fully-resolved order.
    /// Returns immediately with { status: "transforming" }.
    /// Poll GET /api/orders/{id}/status until status changes.
    /// All lines must have NeedsReview = false — returns 422 otherwise.
    /// Only a 'ready' order can start a transform (atomic ready→transforming claim);
    /// an in-flight order returns 202, any other state returns 409.
    /// </summary>
    [HttpPost("{id:guid}/transform")]
    [EnableRateLimiting("transform")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Transform(
        Guid id,
        [FromBody] TransformRequest request,
        CancellationToken ct)
    {
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
        if (order.Status == OrderStatusConstants.Parsing || order.Lines.Count == 0)
            return Conflict(new
            {
                error = "Order is still parsing. Wait until parsing finishes before transforming."
            });

        if (order.Status == OrderStatusConstants.Failed)
            return BadRequest(new
            {
                error = "Order parsing failed. Upload a corrected file before transforming."
            });

        var unresolvedLines = order.Lines.Where(l => l.NeedsReview).Select(l => l.LineNumber).ToList();
        if (unresolvedLines.Count > 0)
            return UnprocessableEntity(new
            {
                error = $"Resolve all lines before transforming. Unresolved: {string.Join(", ", unresolvedLines)}."
            });

        // Resolve the output format: an explicit request format wins (e.g. a manual download);
        // otherwise fall back to the supplier's configured delivery format so "send to supplier"
        // auto-uses the format that supplier requires; otherwise a safe default.
        var formatStr = request.Format?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(formatStr))
        {
            var supplierFormat = await (
                from o in _db.PurchaseOrders.AsNoTracking()
                join c in _db.SupplierDeliveryConfigs.AsNoTracking()
                    on new { o.OrgId, o.SupplierId } equals new { c.OrgId, SupplierId = (Guid?)c.SupplierId }
                where o.Id == id && o.OrgId == _tenant.OrganisationId
                select c.OutputFormat
            ).FirstOrDefaultAsync(ct);
            formatStr = supplierFormat?.Trim().ToLowerInvariant();
        }
        if (string.IsNullOrEmpty(formatStr))
            formatStr = "xml"; // safe default when neither the request nor the supplier specifies one

        if (!AllowedTransformFormats.Contains(formatStr))
            return BadRequest(new { error = "Format must be one of: xml, csv, cxml, json, ubl, x12." });

        // Atomically claim ready → transforming BEFORE enqueueing — the same transition
        // TransformAsync enforces. A racy read-then-enqueue here let two concurrent requests
        // both see "ready" and enqueue two transform jobs for the same order.
        //
        // CancellationToken.None (NOT the request ct) on purpose: there is no await between
        // this committed claim and the synchronous Enqueue below, so a client disconnect can
        // only interrupt the claim itself. If it cancelled the claim AFTER the row committed
        // 'transforming' server-side but BEFORE this call returned, the method would unwind
        // without enqueuing — stranding the order in 'transforming' with no job. Making the
        // claim uninterruptible means it either fully happens (→ we enqueue) or never started
        // (→ the order stays 'ready'); the rare strand window is closed.
        var claimed = await _db.PurchaseOrders
            .Where(o => o.Id == id && o.OrgId == _tenant.OrganisationId
                     && o.Status == OrderStatusConstants.Ready)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatusConstants.Transforming)
                .SetProperty(o => o.UpdatedAt, DateTime.UtcNow), CancellationToken.None);

        if (claimed == 0)
        {
            var currentStatus = await _db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == id && o.OrgId == _tenant.OrganisationId)
                .Select(o => o.Status)
                .FirstOrDefaultAsync(ct);

            if (currentStatus == OrderStatusConstants.Transforming)
                return Accepted(new { status = "transforming" }); // already in progress

            return Conflict(new
            {
                error = $"Order is not ready to transform (status '{currentStatus}'). " +
                        "Only a 'ready' order can start a transform; re-resolve its lines to return it to 'ready'."
            });
        }

        // Enqueue transform job
        try
        {
            TransformOrderJob.Enqueue(_jobs, id, _tenant.OrganisationId, formatStr);
        }
        catch
        {
            // Release the claim — a failed enqueue must not strand the order in
            // 'transforming' (nothing else would ever pick it up again). CancellationToken.None
            // so a client disconnect that triggered this path cannot also abort the
            // compensating release.
            await _db.PurchaseOrders
                .Where(o => o.Id == id && o.OrgId == _tenant.OrganisationId
                         && o.Status == OrderStatusConstants.Transforming)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatusConstants.Ready)
                    .SetProperty(o => o.UpdatedAt, DateTime.UtcNow), CancellationToken.None);
            throw;
        }

        _logger.LogInformation(
            "TransformOrderJob enqueued for order {OrderId}, format={Format}",
            id, formatStr);

        return Accepted(new { status = "transforming" });
    }

    // ── POST /api/orders/{id}/infer-output-structure ──────────────────────────

    /// <summary>
    /// Phase D: infer an output-structure template from a SAMPLE of the file a supplier requires
    /// (paste/upload). Deterministic — JSON + CSV in v1; leaves are pre-bound to canonical fields by
    /// name. Returned with camelCase string enum node types (the frontend wire contract). The order id
    /// scopes the request to the tenant; the sample alone drives the shape.
    /// </summary>
    [HttpPost("{id:guid}/infer-output-structure")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult InferOutputStructure(Guid id, [FromBody] InferOutputStructureRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Sample))
            return BadRequest(new { error = "Paste a sample of the file your supplier requires." });

        var fmt = (request.Format?.Trim().ToLowerInvariant()) switch
        {
            "json" => OutputFormat.Json,
            "csv"  => OutputFormat.Csv,
            "xml"  => OutputFormat.Xml,
            "cxml" => OutputFormat.CXml,
            "ubl"  => OutputFormat.Ubl,
            _      => (OutputFormat?)null,
        };
        if (fmt is null)
            return BadRequest(new { error = "Sample inference supports json, csv, and xml." });

        try
        {
            var tree = OutputNodeTemplateInferrer.FromSample(request.Sample!, fmt.Value);
            var json = System.Text.Json.JsonSerializer.Serialize(tree, OutputTreeJsonOptions);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Could not read that sample as {fmt.Value.ToString().ToLowerInvariant()}: {ex.Message}" });
        }
    }

    public sealed record InferOutputStructureRequest(string? Sample, string? Format);

    private static readonly System.Text.Json.JsonSerializerOptions OutputTreeJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    // ── POST /api/orders/{id}/validate ────────────────────────────────────────

    /// <summary>Validate the order against the supplier's active acceptance profile.</summary>
    [HttpPost("{id:guid}/validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Validate(Guid id, [FromQuery] string? format, CancellationToken ct)
    {
        // Optional output format → also surfaces the format-mandatory buyer-code output check; when
        // omitted the price/structural output checks still run (JSON default imposes no extra reqs).
        ProcuLink.Core.Services.OutputFormat? outputFormat =
            !string.IsNullOrWhiteSpace(format)
            && Enum.TryParse<ProcuLink.Core.Services.OutputFormat>(format, ignoreCase: true, out var fmt)
                ? fmt : null;

        var results = await _acceptance.ValidateOrderAsync(_tenant.OrganisationId, id, ct, outputFormat);
        if (results is null) return NotFound();
        return Ok(results.Select(r => new OrderValidationResultDto(
            r.LineNumber, r.Severity, r.Status, r.Code, r.Message,
            ProcuLink.Api.Services.AcceptanceMessages.TitleForCode(r.Code))));
    }

    // ── POST /api/orders/{id}/accept-ai-suggestions ──────────────────────────

    /// <summary>
    /// Bulk-accept AI mapping suggestions for all unresolved lines whose
    /// confidence is at or above the supplied threshold (default 0.85).
    /// Returns { accepted: N } — the count of lines that were accepted.
    /// </summary>
    [HttpPost("{id:guid}/accept-ai-suggestions")]
    [EnableRateLimiting("ai")]
    [ProducesResponseType(typeof(AcceptAiSuggestionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> AcceptAiSuggestions(
        Guid id,
        [FromQuery] double minConfidence = 0.85,
        CancellationToken ct = default)
    {
        var result = await _orders.AcceptAiSuggestionsAsync(
            _tenant.OrganisationId, id, minConfidence, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(new AcceptAiSuggestionsResponse(result.Value));
    }

    // ── GET /api/orders/{id}/mapping-preview ─────────────────────────────────

    /// <summary>
    /// Read-only side-by-side mapping preview: source field → canonical PO field → AI-suggested
    /// supplier code with confidence and provenance.  Safe to call before transform/commit.
    /// Returns 404 when the order doesn't exist for the authenticated organisation.
    /// </summary>
    [HttpGet("{id:guid}/mapping-preview")]
    // Deterministic read — does NO LLM work (just reads stored suggestions). The live
    // mapping editor polls this (debounced) while an order parses, so it must NOT share
    // the tight "ai" cap or it trips "Rate limit exceeded". Use the generous "preview" policy.
    [EnableRateLimiting("preview")]
    [ProducesResponseType(typeof(MappingPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
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
                o.Status,
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
                ResolvedSupplierCode:    l.SupplierItemCode,
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
            OrderStatus:        order.Status,
            SourceFormat:       sourceFormat,
            DetectedConfidence: detectedConfidence,
            Lines:              lineDtos
        );

        return Ok(dto);
    }

    // ── GET /api/orders/{id}/ai-decisions ─────────────────────────────────────

    /// <summary>
    /// Returns the durable AI-suggestion decision history for this order, newest first.
    /// Each row records what happened to one AI mapping suggestion on one line — accepted,
    /// rejected (a different code was chosen), superseded, or resolved manually — along with
    /// the suggestion's confidence and candidate-set evidence. This is the history the live
    /// resolve flow would otherwise discard (the line's Ai* fields are cleared on resolution);
    /// persisting it lets confidence be calibrated later. Org-scoped: a cross-tenant or unknown
    /// order id returns 404.
    /// </summary>
    [HttpGet("{id:guid}/ai-decisions")]
    [ProducesResponseType(typeof(IReadOnlyList<AiSuggestionDecisionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAiDecisions(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // Confirm the order exists for this org so an order with no decisions (200 empty)
        // is distinguishable from a non-existent / cross-tenant order (404).
        var exists = await _db.PurchaseOrders
            .AsNoTracking()
            .AnyAsync(o => o.Id == id && o.OrgId == orgId, ct);

        if (!exists)
            return NotFound();

        var rows = await _aiDecisions.ListForOrderAsync(orgId, id, ct);

        return Ok(rows.Select(d => new AiSuggestionDecisionDto(
            d.Id,
            d.LineNumber,
            d.SuggestedSupplierItemCode,
            d.ChosenSupplierItemCode,
            d.CandidateSetJson,
            d.Confidence,
            d.ModelVersion,
            d.Decision,
            d.DecidedBy,
            d.DecidedAt)).ToList());
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

    // ── GET /api/orders/{id}/exceptions ───────────────────────────────────────

    /// <summary>Returns all exceptions (any state) for this order, newest first.</summary>
    [HttpGet("{id:guid}/exceptions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetExceptions(Guid id, CancellationToken ct)
    {
        var rows = await _exceptionService.ListForOrderAsync(_tenant.OrganisationId, id, ct);
        return Ok(rows.Select(e => new OrderExceptionDto(
            e.Id, e.OrderId, e.LineId, e.Stage, e.Code, e.Severity, e.State, e.Message, e.CreatedAt, e.ResolvedAt)));
    }

    // ── POST /api/orders/{id}/redeliver ──────────────────────────────────────

    /// <summary>
    /// Re-enqueue delivery for an order that has already been transformed.
    /// Bypasses the AutoDeliver flag — use when the supplier was unreachable
    /// or the operator wants to force a manual retry.
    /// Valid source statuses: delivery_failed, ready_to_deliver, delivery_unconfirmed.
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

        // Centralised in the order-status state machine (W2): a manual redeliver is
        // valid only from a stalled-but-recoverable delivery state.
        if (!ProcuLink.Core.Constants.OrderStatusMachine.RedeliverableFrom.Contains(order.Status))
            return BadRequest(new
            {
                // Derived from the set, never a literal: adding a redeliverable status must not
                // leave this sentence quietly lying about which statuses are valid.
                error = $"Order must be in one of these statuses to redeliver: "
                      + $"{string.Join(", ", ProcuLink.Core.Constants.OrderStatusMachine.RedeliverableFrom.OrderBy(s => s, StringComparer.Ordinal))} "
                      + $"(current: '{order.Status}')."
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

    // ── POST /api/orders/{id}/mark-delivered ─────────────────────────────────

    /// <summary>
    /// Operator confirmation that a parked (<c>delivery_unconfirmed</c>) order DID reach the
    /// supplier — established out-of-band, e.g. the supplier confirmed by phone or the order is
    /// visible in their portal. Closes the order truthfully without re-sending it.
    /// <para>
    /// The delivery ATTEMPT row stays <c>unconfirmed</c>: its outcome was never observed and this
    /// endpoint does not change that. What is new is the operator's assertion, recorded as its own
    /// audit event with the acting user. Only valid from <c>delivery_unconfirmed</c>.
    /// </para>
    /// <para>
    /// Billing: metering counts <c>delivered</c>/<c>rejected_by_supplier</c> by query, so this is
    /// the point at which the order becomes chargeable — correct, since the operator has just
    /// confirmed the supplier received it. No metering call belongs here.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/mark-delivered")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkDelivered(Guid id, CancellationToken ct)
    {
        var orgId     = _tenant.OrganisationId;
        var getResult = await _orders.GetByIdAsync(orgId, id, ct);

        if (!getResult.IsSuccess)
            return NotFound();

        var order = getResult.Value!;

        if (order.Status != OrderStatusConstants.DeliveryUnconfirmed)
            return BadRequest(new
            {
                error = $"Only an order whose delivery is unconfirmed can be marked delivered "
                      + $"(current: '{order.Status}')."
            });

        var now = DateTime.UtcNow;
        order.Status = OrderStatusConstants.Delivered;
        order.UpdatedAt = now;
        // A confirmed delivery closes the SLA window — mirrors DeliveryService's success path.
        order.DeliveryDueAt = null;
        order.SlaBreached = false;

        // ICurrentTenantService only exposes the Clerk "sub" string, never the internal Guid — the
        // acting user's Guid FK (AuditEvent.UserId) has to be resolved via the AppUser row so the
        // human who asserted delivery is genuinely on the record, not just implied.
        var actingUserId = await _db.Users
            .AsNoTracking()
            .Where(u => u.ClerkUserId == _tenant.ClerkUserId)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            orderId = id,
            fromStatus = OrderStatusConstants.DeliveryUnconfirmed,
            toStatus = OrderStatusConstants.Delivered,
            confirmedAt = now,
            detail = "Operator confirmed out-of-band that the supplier received this order. "
                   + "The delivery attempt itself was never acknowledged; this records the "
                   + "operator's assertion, not an observed supplier ACK.",
        });

        _db.AuditEvents.Add(new ProcuLink.Core.Entities.AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            UserId = actingUserId,
            EntityType = "Order",
            EntityId = id,
            Action = "DeliveryConfirmedManually",
            Payload = System.Text.Json.JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "DeliveryConfirmedManually: order {OrderId} (org {OrgId}) marked delivered by operator {UserId} "
            + "after an unconfirmed delivery.",
            id, orgId, _tenant.ClerkUserId);

        return Accepted(new { status = "delivered" });
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
    [ProducesResponseType(typeof(DeadLetterCountResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDeadLetterCount(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var count = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.Status == OrderStatusConstants.DeliveryDeadLetter, ct);
        return Ok(new DeadLetterCountResponse(count));
    }

    // ── GET /api/orders/{id}/artifacts/{artifactId}/download ─────────────────

    /// <summary>
    /// Returns a 15-minute pre-signed URL for the given artifact.
    /// The frontend opens this URL directly — file bytes never flow through the API.
    /// Returns 410 Gone (not a 500, not a dead signed URL) when the artifact's blob was
    /// purged per the org's data-retention policy — the row, hash and audit trail remain.
    /// </summary>
    [HttpGet("{id:guid}/artifacts/{artifactId:guid}/download")]
    [EnableRateLimiting("signed-url")]
    [ProducesResponseType(typeof(DownloadUrl), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Download(Guid id, Guid artifactId, CancellationToken ct)
    {
        var result = await _orders.GetDownloadUrlAsync(
            _tenant.OrganisationId, id, artifactId, ct);

        if (!result.IsSuccess)
        {
            // Retention honesty: a purged blob is a deliberate, explainable state — 410 Gone
            // with the policy message, distinct from "no such artifact" (404).
            if (result.Error == RetentionConstants.BlobPurgedError)
                return StatusCode(StatusCodes.Status410Gone, new { error = result.Error });

            return NotFound();
        }

        return Ok(result.Value);
    }

    // ── GET /api/orders/{id}/conformance ──────────────────────────────────────

    /// <summary>
    /// Group V8 — standards conformance report for an order's would-be OUTBOUND
    /// document. Transforms the order to the named standards <c>format</c>
    /// (cxml / ubl / x12 — the entity formats that map to a named profile) and
    /// validates the produced bytes against that profile (cXML 1.2 OrderRequest /
    /// UBL 2.1 Order / X12 850), returning the overall pass plus a named,
    /// profile-referenced per-check row for each mandatory element / segment /
    /// cardinality.
    ///
    /// <para>
    /// Read-only and NON-MUTATING: it never writes, never changes status, never
    /// delivers, never persists an artifact — it produces the document in memory
    /// purely to check it. "Supported" therefore means <em>validated against a
    /// named profile</em>.
    /// </para>
    ///
    /// <para>
    /// <c>format</c> resolution mirrors transform: an explicit <c>?format=</c> wins;
    /// otherwise the supplier's configured delivery format is used; a format with no
    /// named standards profile (csv / json / xml) returns 400. Pass
    /// <c>?download=md</c> for a <c>text/markdown</c> downloadable report instead of
    /// JSON. Org-scoped: a cross-tenant or unknown order id returns 404.
    /// </para>
    ///
    /// <para>
    /// <b>Pinned-revision parity (read-parity, flag-gated).</b> When
    /// <c>Connections:RevisionAuthority</c> is ON and the order is pinned to a published
    /// revision, the report covers the document delivery would ACTUALLY produce: the
    /// revision's snapshotted output format governs (outranking the explicit query,
    /// exactly as the transform swaps it), and the document is rendered with the same
    /// effective priority (per-order override → the pinned revision's output snapshot →
    /// fixed transformer; the live supplier-promoted mapping is NOT consulted). A pinned
    /// format with no named profile returns 400 honestly. Flag off / unpinned is
    /// byte-identical to before.
    /// </para>
    /// </summary>
    [HttpGet("{id:guid}/conformance")]
    [ProducesResponseType(typeof(ConformanceReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetConformance(
        Guid id,
        [FromQuery] string? format = null,
        [FromQuery] string? download = null,
        CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;

        // Load the order WITH lines, org-scoped, read-only. A missing supplier nav must
        // never filter the row out, so the supplier is loaded separately + defensively
        // (mirrors the preview endpoint).
        var order = await _db.PurchaseOrders
            .Include(o => o.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.OrgId == orgId, ct);
        if (order is null)
            return NotFound();

        if (order.Supplier is null && order.SupplierId != Guid.Empty)
        {
            var supplier = await _db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == order.SupplierId && s.OrgId == orgId, ct);
            if (supplier is not null)
                order.Supplier = supplier;
        }

        // Read-parity (launch batch 7 follow-up): resolve the pinned revision's bundle ONCE through
        // the SAME resolver the transform uses. Live bundle when the flag is off / the order is
        // unpinned / the pin is orphaned / no resolver — byte-identical to the pre-read-parity path.
        var effectiveConfig = _effectiveConfig is null
            ? EffectiveConnectionConfig.Live
            : await _effectiveConfig.ResolveAsync(orgId, order.ConnectionRevisionId, ct);

        // Resolve the format: a PINNED order delivers in the revision's snapshotted format, which
        // outranks the explicit query exactly as the transform swaps it (read-parity — the report
        // must cover the document that would actually go out); else explicit query wins; else the
        // supplier's configured delivery format.
        var formatStr     = format?.Trim().ToLowerInvariant();
        var formatFromPin = false;
        if (effectiveConfig.IsRevision && !string.IsNullOrWhiteSpace(effectiveConfig.OutputFormat)
            && Enum.TryParse<OutputFormat>(effectiveConfig.OutputFormat, ignoreCase: true, out _))
        {
            formatStr     = effectiveConfig.OutputFormat.Trim().ToLowerInvariant();
            formatFromPin = true;
        }
        if (string.IsNullOrEmpty(formatStr))
        {
            var supplierFormat = await (
                from o in _db.PurchaseOrders.AsNoTracking()
                join c in _db.SupplierDeliveryConfigs.AsNoTracking()
                    on new { o.OrgId, o.SupplierId } equals new { c.OrgId, SupplierId = (Guid?)c.SupplierId }
                where o.Id == id && o.OrgId == orgId
                select c.OutputFormat
            ).FirstOrDefaultAsync(ct);
            formatStr = supplierFormat?.Trim().ToLowerInvariant();
        }
        if (string.IsNullOrEmpty(formatStr))
            return BadRequest(new
            {
                error = "No format to validate. Pass ?format=cxml|ubl|x12 or configure the supplier's delivery format.",
            });

        // Only the entity-transformable formats that have a NAMED standards profile.
        var outputFormat = formatStr switch
        {
            "cxml" => (OutputFormat?)OutputFormat.CXml,
            "ubl"  => OutputFormat.Ubl,
            "x12"  => OutputFormat.X12,
            _      => null,
        };
        if (outputFormat is null || !_conformance.SupportsFormat(outputFormat.Value))
            return BadRequest(new
            {
                error = formatFromPin
                    ? $"This order is pinned to a connection revision that delivers '{formatStr}', " +
                      "which has no named standards profile. Conformance reports cover cxml, ubl, x12."
                    : "Conformance reports are available for the named standards formats: cxml, ubl, x12. " +
                      "Generic csv/json/xml templates have no named profile.",
            });

        // Transform the order to the standards document IN MEMORY. The transform applies the
        // existing review / required-field guard, so an order with unresolved lines surfaces a
        // 422 (resolve first) rather than a misleading conformance failure on a half-formed doc.
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(outputFormat.Value));
        if (transformer is null)
            return BadRequest(new { error = $"No transform service registered for format '{formatStr}'." });

        string documentText;
        try
        {
            // Read-parity: a PINNED order's document is rendered with the SAME effective priority
            // delivery applies (per-order override → pinned revision output → fixed). Flag off /
            // unpinned keeps the original fixed-transform render, byte-identical.
            documentText = effectiveConfig.IsRevision
                ? await RenderPinnedEffectiveDocumentAsync(order, effectiveConfig, transformer, outputFormat.Value, ct)
                : await ReadTransformResultAsync(
                      await transformer.TransformAsync(order, outputFormat.Value, ct), ct);
        }
        catch (TransformValidationException ex)
        {
            return UnprocessableEntity(new
            {
                error = "The order cannot be transformed to a complete document yet, so it cannot be " +
                        "checked for conformance. Resolve the flagged lines first.",
                detail = ex.Message,
                unresolvedLines = ex.UnresolvedLineNumbers,
            });
        }
        catch (TransformTemplateException ex)
        {
            // Only reachable on the pinned path when the per-order override carries a broken
            // whole-document template — the order could not be delivered from it either, so the
            // honest answer is "no document to check", not a misleading fixed-transform report.
            return UnprocessableEntity(new
            {
                error = "The order's output template does not render, so there is no document to " +
                        "check for conformance. Fix the template first.",
                detail = ex.Message,
            });
        }

        var report = _conformance.Check(documentText, outputFormat.Value);

        // Markdown download variant.
        if (string.Equals(download?.Trim(), "md", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(report.ToMarkdown());
            var fileName = $"conformance-{order.PoNumber ?? id.ToString()}-{report.Profile}.md";
            return File(bytes, "text/markdown", fileName);
        }

        var dto = new ConformanceReportDto(
            OrderId:        id,
            Format:         formatStr,
            Profile:        report.Profile.ToString(),
            ProfileName:    report.ProfileName,
            ProfileVersion: report.ProfileVersion,
            OverallPass:    report.OverallPass,
            ErrorCount:     report.ErrorCount,
            WarningCount:   report.WarningCount,
            Checks: report.Checks
                .Select(c => new ConformanceCheckDto(
                    c.Code, c.Severity.ToString(), c.Passed, c.Message, c.ProfileRef))
                .ToList());

        return Ok(dto);
    }

    /// <summary>
    /// Read-parity render for a PINNED order's conformance document: the SAME mode precedence
    /// <c>OrderTransformService.TransformAsync</c> applies at delivery, in memory, for the named
    /// standards formats (cxml/ubl/x12 — structured, so the native CSV/JSON override builder never
    /// applies here):
    /// <list type="number">
    ///   <item>whole-document template (per-order override, highest-priority seam) — a broken
    ///        template throws <see cref="TransformTemplateException"/> (mapped to 422 by the caller,
    ///        mirroring "the order could not deliver from it");</item>
    ///   <item>per-order usable output override → effective entity → fixed transform;</item>
    ///   <item>the PINNED revision's output snapshot (the live supplier-promoted mapping is
    ///        intentionally NOT consulted) → effective entity → fixed transform, with the transform's
    ///        defensive fall-to-fixed on any non-validation failure;</item>
    ///   <item>the fixed transformer.</item>
    /// </list>
    /// </summary>
    private async Task<string> RenderPinnedEffectiveDocumentAsync(
        PurchaseOrderEntity order,
        EffectiveConnectionConfig effective,
        ITransformService transformer,
        OutputFormat outputFormat,
        CancellationToken ct)
    {
        var mappingOverride = OrderMappingOverrideReader.Read(order.CanonicalJson);

        // Mode 1 — whole-document template.
        if (OrderMappingOverrideReader.HasUsableTemplate(mappingOverride))
            return await ReadTransformResultAsync(
                new ScribanTemplateTransformService().Build(order, mappingOverride!), ct);

        // Mode 3 — per-order usable output override (structured: canonical-field changes resolved
        // into an effective entity, then the existing fixed transform).
        if (OrderMappingOverrideReader.HasUsableOutput(mappingOverride)
            && MappedTransformService.SupportsOverrideFormat(outputFormat))
        {
            var effectiveEntity = EffectiveEntityResolver.Resolve(order, mappingOverride!);
            return await ReadTransformResultAsync(
                await transformer.TransformAsync(effectiveEntity, outputFormat, ct), ct);
        }

        // Mode 4 — the pinned revision's output snapshot, wrapped via the SAME deserialize + wrap
        // helpers the replay engine uses (no second copy of the snapshot logic).
        var revisionOverride = ReplayService.BuildRevisionOverride(
            mappingOverride, ReplayService.DeserializeOutputConfig(effective.OutputMappingJson));
        if (revisionOverride is not null && MappedTransformService.SupportsOverrideFormat(outputFormat))
        {
            try
            {
                var effectiveEntity = EffectiveEntityResolver.Resolve(order, revisionOverride);
                return await ReadTransformResultAsync(
                    await transformer.TransformAsync(effectiveEntity, outputFormat, ct), ct);
            }
            catch (Exception ex) when (ex is not TransformValidationException and not TransformTemplateException)
            {
                // Defensive parity with the transform: a pinned revision's output snapshot must
                // never break the report — fall back to the fixed transformer (which is what
                // delivery would produce in the same situation).
                _logger.LogWarning(ex,
                    "Pinned revision output mapping failed for order {OrderId} ({Source}) during conformance; falling back to the fixed transformer.",
                    order.Id, effective.Source);
            }
        }

        // Mode 6 — fixed transformer (byte-identical default).
        return await ReadTransformResultAsync(
            await transformer.TransformAsync(order, outputFormat, ct), ct);
    }

    /// <summary>Reads a transform result's content stream to a string (in-memory, never persisted).</summary>
    private static async Task<string> ReadTransformResultAsync(TransformResult result, CancellationToken ct)
    {
        using var sr = new StreamReader(result.Content);
        return await sr.ReadToEndAsync(ct);
    }

    // ── Mapping helper ────────────────────────────────────────────────────────

    private static OrderDto MapToDto(
        PurchaseOrderEntity e,
        string? errorMessage = null,
        IReadOnlyDictionary<int, ProcuLink.Core.Services.CalibrationResult>? calibrations = null) => new(
        Id:            e.Id,
        PoNumber:      e.PoNumber,
        // Phase-0 routing: SupplierId is nullable on the entity; an unrouted order has none yet.
        // The DTO contract stays Guid for now (no FE change) — coalesced to Empty. Phase 1 makes the
        // DTO nullable when the FE renders unrouted orders.
        SupplierId:    e.SupplierId ?? Guid.Empty,
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
                l.Confidence, l.NeedsReview, MapAiSuggestion(l, calibrations),
                // Phase 4 per-line enrichment (null for parsers that don't emit it).
                LineAmount:   l.LineAmount,
                TaxRate:      l.TaxRate,
                TaxAmount:    l.TaxAmount,
                DeliveryDate: l.DeliveryDate?.ToString("yyyy-MM-dd"),
                // P2 hardening: why the line was flagged for review (null when never flagged).
                ReviewReason: l.ReviewReason))
            .ToList(),
        Artifacts: e.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new ArtifactDto(a.Id, a.Format, a.FileKey, a.CreatedAt))
            .ToList(),
        BuyerName:    ExtractBuyerName(e),
        ErrorMessage: errorMessage,
        // Phase 4 header enrichment. DocumentSupplierName is the extracted (as-printed)
        // supplier name — distinct from the resolved SupplierName (e.Supplier.Name) above.
        SubTotal:             e.SubTotal,
        TaxTotal:             e.TaxTotal,
        GrandTotal:           e.GrandTotal,
        PaymentTerms:         e.PaymentTerms,
        DocumentType:         e.DocumentType,
        DocumentSupplierName: e.SupplierName,
        // Buyer tax id + extracted ship-to/bill-to/contact — surfaced for the order-review UI
        // transparency feature. Nullable; additive — no behaviour change.
        BuyerTaxId:           e.BuyerTaxId,
        ShipToName:           e.ShipToName,
        ShipToDeliverTo:      e.ShipToDeliverTo,
        ShipToStreet:         e.ShipToStreet,
        ShipToCity:           e.ShipToCity,
        ShipToPostalCode:     e.ShipToPostalCode,
        ShipToCountry:        e.ShipToCountry,
        ShipToEmail:          e.ShipToEmail,
        ShipToPhone:          e.ShipToPhone,
        BillToName:           e.BillToName,
        BillToDeliverTo:      e.BillToDeliverTo,
        BillToStreet:         e.BillToStreet,
        BillToCity:           e.BillToCity,
        BillToPostalCode:     e.BillToPostalCode,
        BillToCountry:        e.BillToCountry,
        BillToEmail:          e.BillToEmail,
        BillToPhone:          e.BillToPhone,
        ContactName:          e.ContactName,
        ContactEmail:         e.ContactEmail,
        ContactPhone:         e.ContactPhone
    );

    /// <summary>
    /// Extracts BuyerName from the denormalized column (set by the async parse job)
    /// or falls back to CanonicalJson for orders created via the sync path.
    ///
    /// COLUMN-FIRST CONTRACT: this is the canonical header reader for the order DTO.
    /// The typed <see cref="PurchaseOrderEntity.BuyerName"/> column is the source of
    /// truth (written by the async parse path, which never writes canonical_json);
    /// canonical_json is consulted ONLY as a legacy fallback when the column is null.
    /// Inverting this order would make every async-parsed order read a stale/absent
    /// JSON value. Pinned by <c>AsyncParseColumnFirstContractTests</c>.
    /// <c>internal</c> (not <c>private</c>) so that guard test can call it directly.
    /// </summary>
    internal static string? ExtractBuyerName(PurchaseOrderEntity e)
    {
        // Prefer the denormalized column — always populated by ParseStoredFileAsync.
        if (!string.IsNullOrWhiteSpace(e.BuyerName)) return e.BuyerName;

        // Fall back to CanonicalJson for orders created via the sync (non-file) path.
        if (e.CanonicalJson is null) return null;
        try
        {
            if (e.CanonicalJson.RootElement.TryGetProperty("buyerName", out var el))
                return el.GetString();
            if (e.CanonicalJson.RootElement.TryGetProperty("BuyerName", out var el2))
                return el2.GetString();
        }
        catch { /* malformed JSON — ignore */ }
        return null;
    }

    private static AiMappingSuggestionDto? MapAiSuggestion(
        PurchaseOrderLineEntity line,
        IReadOnlyDictionary<int, ProcuLink.Core.Services.CalibrationResult>? calibrations = null)
    {
        if (string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode))
            return null;

        var rawConfidence = line.AiSuggestionConfidence ?? 0f;

        // V9 calibration overlay (display-only). When a calibration result was computed for this
        // line, surface it; otherwise fall back to raw passthrough (calibrated == raw, off). The
        // raw Confidence field below is ALWAYS the unmodified model confidence.
        float calibratedConfidence = rawConfidence;
        bool isCalibrated = false;
        string calibrationBasis = ProcuLink.Core.Services.ConfidenceCalibration.UncalibratedBasis;

        if (calibrations is not null
            && calibrations.TryGetValue(line.LineNumber, out var calibration))
        {
            calibratedConfidence = (float)calibration.CalibratedConfidence;
            isCalibrated = calibration.IsCalibrated;
            calibrationBasis = calibration.Basis;
        }

        return new AiMappingSuggestionDto(
            line.AiSuggestedSupplierItemCode,
            rawConfidence,
            line.AiSuggestionReason ?? string.Empty,
            line.AiSuggestionProvenance ?? string.Empty,
            CalibratedConfidence: calibratedConfidence,
            IsCalibrated: isCalibrated,
            CalibrationBasis: calibrationBasis);
    }
}
