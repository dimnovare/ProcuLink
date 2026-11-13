using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// The read-only order archive: list, detail and artifact download for orders received before the
/// document engine replaced the order pipeline. Nothing here writes, and nothing else lives on
/// <c>api/orders</c>.
/// All routes are tenant-scoped — org is resolved from the Clerk JWT by
/// TenantResolutionMiddleware and injected via ICurrentTenantService.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderQueryService    _orders;
    private readonly ICurrentTenantService _tenant;
    private readonly ProcuLinkDbContext    _db;

    public OrdersController(IOrderQueryService orders, ICurrentTenantService tenant, ProcuLinkDbContext db)
    {
        _orders = orders;
        _tenant = tenant;
        _db     = db;
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

        var (items, totalCount, sampleCount) = result.Value;

        // Report the window back as page/pageSize so the envelope is identical regardless of
        // which paging style the caller used. With a non-page-aligned offset, page is the
        // 1-based index of the window that starts at `skip`.
        //
        // sampleCount rides along because this list RETURNS onboarding practice orders while the
        // billing quota excludes them. totalCount - sampleCount is the metered population, so the
        // caller can reconcile the two instead of contradicting itself.
        var effectivePage = take > 0 ? (skip / take) + 1 : 1;
        return Ok(new PaginatedResult<PurchaseOrderSummary>(items, totalCount, effectivePage, take, sampleCount));
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
        string? failureCause = null;
        int?    retryAfterSeconds = null;

        // delivery_unconfirmed included: a parked order must surface its explanation ("we sent this
        // but never learned whether it arrived — send again or mark delivered"), or the operator
        // gets a status with no sentence and no guidance.
        if (entity.Status is "failed" or "transform_failed" or "delivery_failed"
                          or "rejected_by_supplier" or "delivery_dead_letter"
                          or OrderStatusConstants.DeliveryUnconfirmed)
        {
            // A parked order skips this branch entirely. None of these actions can carry the park
            // sentence (the DeliveryUnconfirmed payload keys it "detail"), but a SURVIVING one from
            // an unrelated earlier episode — a fixed-then-delivered ParseFailed, or a
            // DeliveryDeadLettered from an ops requeue that crashed and parked — would win the
            // assignment and show the operator a stale error instead of why the order is parked.
            var payload = entity.Status == OrderStatusConstants.DeliveryUnconfirmed
                ? null
                : await _db.AuditEvents
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

            // ONE newest-attempt read, shared by every consumer below. It REPLACES the two
            // conditional single-column lookups this ladder used to run (one for the
            // failed/dead-letter/park trio, one for rejected_by_supplier), so a delivery-failure
            // order costs at most ONE delivery-attempt query here — the same ceiling as before,
            // now paid once rather than per branch. Skipped entirely for the non-delivery failure
            // statuses (failed / transform_failed), which have no attempt to read.
            var newestAttempt = entity.Status is "delivery_failed" or "delivery_dead_letter"
                                              or "rejected_by_supplier"
                                              or OrderStatusConstants.DeliveryUnconfirmed
                ? await _db.DeliveryAttempts
                    .AsNoTracking()
                    .Where(a => a.OrderId == id && a.OrgId == _tenant.OrganisationId)
                    .OrderByDescending(a => a.AttemptedAt)
                    .Select(a => new
                    {
                        a.Status,
                        a.ErrorMessage,
                        a.RejectionReason,
                        a.ResponseBody,
                        a.ResponseCode,
                        a.RetryAfterSeconds,
                    })
                    .FirstOrDefaultAsync(ct)
                : null;

            // The branch that actually carries the park sentence: ParkUnconfirmedAsync writes it to
            // attempt.ErrorMessage. For a parked order this is the ONLY path that can reach it —
            // hence the skip above rather than a mere ordering preference.
            if (errorMessage is null && entity.Status is "delivery_failed" or "delivery_dead_letter"
                                                      or OrderStatusConstants.DeliveryUnconfirmed)
            {
                errorMessage = newestAttempt?.ErrorMessage;
            }

            if (errorMessage is null && entity.Status == "rejected_by_supplier")
            {
                errorMessage = newestAttempt?.RejectionReason
                            ?? newestAttempt?.ResponseBody
                            ?? newestAttempt?.ErrorMessage;
            }

            // …and the same verdict as something a client can BRANCH on. Derived from the stored
            // columns by the same table that classified the response live, so the sentence above and
            // the slug here can never describe two different failures. Only a FAILED attempt carries
            // a cause: a park ('unconfirmed') is an unknown outcome, not a supplier verdict, and
            // asking the table about it would answer "unreachable" for a response nobody observed.
            if (newestAttempt is { Status: DeliveryAttempt.StatusFailed })
            {
                failureCause = SupplierResponseClassification.CauseFor(
                    newestAttempt.ResponseCode, newestAttempt.RejectionReason, newestAttempt.ResponseBody);
                retryAfterSeconds = newestAttempt.RetryAfterSeconds;
            }
        }

        return Ok(MapToDto(entity, errorMessage, failureCause, retryAfterSeconds));
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

    // ── Mapping helper ────────────────────────────────────────────────────────

    private static OrderDto MapToDto(
        PurchaseOrderEntity e,
        string? errorMessage,
        // WP-19 recovery — the delivery failure as something a client can branch on.
        string? failureCause,
        int? retryAfterSeconds) => new(
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
                l.Confidence, l.NeedsReview, MapAiSuggestion(l),
                // Phase 4 per-line enrichment (null for parsers that don't emit it).
                LineAmount:   l.LineAmount,
                TaxRate:      l.TaxRate,
                TaxAmount:    l.TaxAmount,
                DeliveryDate: l.DeliveryDate?.ToString("yyyy-MM-dd"),
                // P2 hardening: why the line was flagged for review (null when never flagged).
                ReviewReason: l.ReviewReason,
                // The identifiers a punchout order actually carries — the review UI shows these
                // beside the (meaningless) buyer item code so the operator can check a match.
                ManufacturerPartNumber: l.ManufacturerPartNumber,
                ManufacturerName:       l.ManufacturerName,
                ResolvedVia:            l.ResolvedVia,
                AiSuggestionBasis:      l.AiSuggestionBasis))
            .ToList(),
        Artifacts: e.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new ArtifactDto(a.Id, a.Format, a.FileKey, a.CreatedAt))
            .ToList(),
        BuyerName:    ExtractBuyerName(e),
        ErrorMessage: errorMessage,
        FailureCause:      failureCause,
        RetryAfterSeconds: retryAfterSeconds,
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
        ContactPhone:         e.ContactPhone,
        // WP-27: the review screen's "practice order" framing reads this, not a query parameter.
        IsSample:             e.IsSample,
        DueDate:              e.DueDate?.ToString("yyyy-MM-dd"),
        SellerTaxId:          e.SellerTaxId,
        PaymentIban:          e.PaymentIban,
        PaymentReference:     e.PaymentReference,
        HoldForReview:        e.HoldForReview
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
        // Prefer the denormalized column — the parse path populated it and never wrote canonical_json.
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

    private static AiMappingSuggestionDto? MapAiSuggestion(PurchaseOrderLineEntity line)
    {
        if (string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode))
            return null;

        // Null stays null. This was `?? 0f` into a non-nullable field, so a suggestion nothing
        // scored — every deterministic catalog / source-document match — reached the mapper as a
        // hard 0%, drawn on the same AI-violet confidence ramp as a real model score. An absent
        // measurement is not a measurement of zero.
        var rawConfidence = line.AiSuggestionConfidence;

        // The archive shows the raw confidence only: calibrated == raw, never calibrated.
        return new AiMappingSuggestionDto(
            line.AiSuggestedSupplierItemCode,
            rawConfidence,
            line.AiSuggestionReason ?? string.Empty,
            line.AiSuggestionProvenance ?? string.Empty,
            CalibratedConfidence: rawConfidence,
            IsCalibrated: false,
            CalibrationBasis: ProcuLink.Core.Services.ConfidenceCalibration.UncalibratedBasis,
            Basis: BasisFor(line));
    }

    /// <summary>
    /// Names the evidence behind a line's stored AI suggestion, for a UI that must decide whether it
    /// may print a percentage and call it AI.
    ///
    /// <para><b>That decision rests entirely on whether a confidence was recorded</b>, not on the
    /// provenance text. Since the deterministic producers stopped stamping 0.95, a stored confidence
    /// exists if and only if a scorer produced one, so <c>HasValue</c> is the whole safety property
    /// and it is a fact about the row, not a parse.</para>
    ///
    /// <para>The provenance sniff below only chooses between two labels that are BOTH non-model, so
    /// getting it wrong can downgrade a label but can never invent an AI attribution or a number.
    /// It is a display nicety sitting on the safe side of the decision that matters.</para>
    /// </summary>
    private static string BasisFor(PurchaseOrderLineEntity line)
    {
        if (line.AiSuggestionConfidence.HasValue)
            return ProcuLink.Core.Services.Ai.AiMappingSuggestionBasis.Model;

        var provenance = line.AiSuggestionProvenance ?? string.Empty;
        return provenance.StartsWith("catalog", StringComparison.OrdinalIgnoreCase)
            ? ProcuLink.Core.Services.Ai.AiMappingSuggestionBasis.Catalog
            : ProcuLink.Core.Services.Ai.AiMappingSuggestionBasis.SourceDocument;
    }
}
