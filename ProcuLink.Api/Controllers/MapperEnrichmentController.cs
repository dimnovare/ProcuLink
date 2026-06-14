using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Phase-2b: the per-order enrichment endpoints the flexible-mapping UI consumes. These
/// are thin read/record endpoints over services that ALREADY exist — no new engines, no
/// new always-on AI calls:
///
/// <list type="bullet">
///   <item><c>GET  /api/orders/{id}/mapping-suggestions</c> — the supplier's SAVED PO mapping
///   (a learned source→canonical map) projected to accept/reject ghost wires. Deterministic +
///   high-confidence; no model call.</item>
///   <item><c>GET  /api/orders/{id}/validation</c> — <see cref="ISupplierAcceptanceService"/>
///   results mapped to per-field green/amber badges.</item>
///   <item><c>GET  /api/orders/{id}/catalog-hints</c> — per-line catalog price/code variance via
///   the shared catalog lookup + the EU-aware price parse (never numeric-corrupts a comma decimal).</item>
///   <item><c>POST /api/orders/{id}/ai-suggestion-decisions</c> — records an accept/reject into the
///   existing V9 calibration history (<see cref="IAiSuggestionDecisionService"/>). Best-effort.</item>
/// </list>
///
/// Every query is org-scoped and the order must belong to the caller's org (404 otherwise).
/// Each GET returns an honest EMPTY list when nothing applies — it never fabricates a wire,
/// a badge, or a price.
///
/// <para>Lives in its own controller (mirroring <see cref="MappingSuggestionsController"/> and
/// <see cref="AiCalibrationController"/>) so the route group evolves independently and the
/// heavily-tested <see cref="OrdersController"/> constructor is untouched.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class MapperEnrichmentController : ControllerBase
{
    private readonly ProcuLinkDbContext _db;
    private readonly ICurrentTenantService _tenant;
    private readonly ISupplierAcceptanceService _acceptance;
    private readonly IPoMappingService _poMappings;
    private readonly IFieldMappingSuggester _suggester;
    private readonly IAiSuggestionDecisionService _decisions;
    private readonly ILogger<MapperEnrichmentController> _logger;

    public MapperEnrichmentController(
        ProcuLinkDbContext db,
        ICurrentTenantService tenant,
        ISupplierAcceptanceService acceptance,
        IPoMappingService poMappings,
        IFieldMappingSuggester suggester,
        IAiSuggestionDecisionService decisions,
        ILogger<MapperEnrichmentController> logger)
    {
        _db = db;
        _tenant = tenant;
        _acceptance = acceptance;
        _poMappings = poMappings;
        _suggester = suggester;
        _decisions = decisions;
        _logger = logger;
    }

    // ── GET /api/orders/{id}/mapping-suggestions ──────────────────────────────

    /// <summary>
    /// Returns learned source→canonical mapping suggestions for the order, rendered as
    /// accept/reject ghost wires. The source is the supplier's SAVED PO mapping (high
    /// confidence — it was learned from this supplier's prior orders), NOT a fresh model
    /// call: deterministic and free. Empty when the supplier has no saved mapping yet.
    /// </summary>
    [HttpGet("{id:guid}/mapping-suggestions")]
    [ProducesResponseType(typeof(IReadOnlyList<MappingSuggestionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMappingSuggestions(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var order = await _db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == id && o.OrgId == orgId)
            .Select(o => new { o.SupplierId })
            .FirstOrDefaultAsync(ct);
        if (order is null)
            return NotFound();

        var suggestions = new List<MappingSuggestionDto>();

        // The supplier's saved PO mapping is a LEARNED source→canonical map: each entry that
        // names a real source column is a high-confidence suggested wire. FixedValue-only
        // entries have no source column to wire and are skipped (nothing to suggest).
        if (order.SupplierId != Guid.Empty)
        {
            var config = await _poMappings.GetAsync(orgId, order.SupplierId, ct);
            if (config is not null)
            {
                foreach (var (canonicalField, entry) in EnumerateMappingEntries(config))
                {
                    var sourceColumn = entry.ExternalField;
                    if (string.IsNullOrWhiteSpace(sourceColumn))
                        continue;

                    suggestions.Add(new MappingSuggestionDto(
                        TargetKey: canonicalField,
                        SourceId: sourceColumn,
                        Confidence: SavedMappingConfidence,
                        Reason: $"Saved supplier mapping: column '{sourceColumn}' → {canonicalField}",
                        SourceKind: "raw"));
                }
            }
        }

        // Note: the heuristic IFieldMappingSuggester is the SECONDARY path. It needs the order's
        // detected source columns (a file download), so it is intentionally NOT run on this hot
        // read path — the saved mapping already covers the learned wires, and the suggester is
        // surfaced by the existing POST /api/suppliers/{id}/mapping/suggest-fields. _suggester is
        // injected so a future cheap source-column source can light it up without a ctor change.
        _ = _suggester;

        return Ok(suggestions);
    }

    // ── GET /api/orders/{id}/validation ───────────────────────────────────────

    /// <summary>
    /// Returns per-field validation states for the order's mapper rows. Reuses
    /// <see cref="ISupplierAcceptanceService.ValidateOrderAsync"/>: a failing rule → an amber
    /// "review" badge (blocking when severity is error); everything else stays "valid".
    /// 404 when the order does not belong to the caller's org (the service returns null).
    /// </summary>
    [HttpGet("{id:guid}/validation")]
    [ProducesResponseType(typeof(IReadOnlyList<FieldValidationStateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetValidation(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // ValidateOrderAsync returns null when the order does not exist for this org → 404.
        var results = await _acceptance.ValidateOrderAsync(orgId, id, ct);
        if (results is null)
            return NotFound();

        var states = results
            .Select(r =>
            {
                var failed = string.Equals(r.Status, "fail", StringComparison.OrdinalIgnoreCase);
                var blocking = failed && string.Equals(r.Severity, "error", StringComparison.OrdinalIgnoreCase);
                return new FieldValidationStateDto(
                    Key: r.Code,
                    State: failed ? "review" : "valid",
                    Reason: failed && !string.IsNullOrWhiteSpace(r.Message) ? r.Message : null,
                    Blocking: blocking);
            })
            .ToList();

        return Ok(states);
    }

    // ── GET /api/orders/{id}/catalog-hints ────────────────────────────────────

    /// <summary>
    /// Returns per-line catalog price/code variance hints for resolved lines. Reuses the shared
    /// org+supplier-scoped catalog lookup (<see cref="OrderServiceShared.BuildCatalogLookupAsync"/>)
    /// keyed by supplier item code / barcode / manufacturer-part-number, and computes the variance
    /// with the EU-aware decimal parse so a comma-decimal catalog price is never read 100×/1000× off.
    /// Only lines that match a catalog product produce a hint; everything else is honestly omitted.
    /// </summary>
    [HttpGet("{id:guid}/catalog-hints")]
    [ProducesResponseType(typeof(IReadOnlyList<CatalogPriceHintDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCatalogHints(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var order = await _db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == id && o.OrgId == orgId)
            .Select(o => new { o.SupplierId, o.Currency })
            .FirstOrDefaultAsync(ct);
        if (order is null)
            return NotFound();

        var hints = new List<CatalogPriceHintDto>();
        if (order.SupplierId == Guid.Empty)
            return Ok(hints);

        var lines = await _db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.OrderId == id)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(ct);
        if (lines.Count == 0)
            return Ok(hints);

        // Batch-load the supplier's active catalog ONCE (keyed by code/barcode/MPN) — no N+1.
        var catalog = await OrderServiceShared.BuildCatalogLookupAsync(_db, orgId, order.SupplierId, ct);
        if (catalog.Count == 0)
            return Ok(hints);

        foreach (var line in lines)
        {
            // Resolve the line to a catalog product by supplier item code first, then by the
            // lossless manufacturer-part-number fallback (the catalog is also keyed by MPN/ExternalId).
            var key = !string.IsNullOrWhiteSpace(line.SupplierItemCode)
                ? line.SupplierItemCode
                : line.ManufacturerPartNumber;
            if (string.IsNullOrWhiteSpace(key) || !catalog.TryGetValue(key, out var product))
                continue;

            // Catalog price is advisory: parse it EU-aware (never InvariantCulture on a raw locale string).
            var (catalogPrice, variancePercent) =
                ComputeCatalogVariance(product.Price?.ToString(System.Globalization.CultureInfo.InvariantCulture), line.UnitPrice);

            hints.Add(new CatalogPriceHintDto(
                LineKey: $"line:{line.LineNumber}",
                CatalogCode: product.Code,
                CatalogPrice: catalogPrice,
                PoPrice: line.UnitPrice,
                VariancePercent: variancePercent,
                Currency: product.Currency ?? order.Currency));
        }

        return Ok(hints);
    }

    // ── POST /api/orders/{id}/ai-suggestion-decisions ─────────────────────────

    /// <summary>
    /// Records a user's accept/reject of an AI/learned mapping suggestion into the durable V9
    /// confidence-calibration history (<see cref="IAiSuggestionDecisionService"/>) so the
    /// per-org accept-rate curve sees the empirical outcome. Best-effort telemetry: a failure
    /// to persist never blocks the UI (the decision already applied client-side). The raw
    /// confidence is recorded verbatim. 404 only when the order isn't the caller's.
    /// </summary>
    [HttpPost("{id:guid}/ai-suggestion-decisions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordDecision(
        Guid id,
        [FromBody] RecordSuggestionDecisionRequest request,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var exists = await _db.PurchaseOrders.AsNoTracking()
            .AnyAsync(o => o.Id == id && o.OrgId == orgId, ct);
        if (!exists)
            return NotFound();

        if (request is null)
            return NoContent();

        var suggested = request.SourceId ?? string.Empty;
        var record = new AiSuggestionDecisionRecord(
            LineNumber: ExtractLineNumber(request.TargetKey),
            SuggestedSupplierItemCode: suggested,
            ChosenSupplierItemCode: request.Accepted ? suggested : null,
            // Preserve the full mapper-level provenance (target + source) that the line-oriented
            // schema can't otherwise hold, so nothing is lost for later analysis.
            CandidateSetJson: System.Text.Json.JsonSerializer.Serialize(
                new { request.TargetKey, request.SourceId }),
            Confidence: request.Confidence,
            ModelVersion: null,
            Decision: request.Accepted ? AiSuggestionDecisionKind.Accepted : AiSuggestionDecisionKind.Rejected,
            DecidedBy: string.IsNullOrWhiteSpace(_tenant.ClerkUserId) ? null : _tenant.ClerkUserId);

        try
        {
            await _decisions.RecordAsync(orgId, id, record, ct);
        }
        catch (Exception ex)
        {
            // Telemetry only — never surface to the user or fail the decision.
            _logger.LogWarning(ex,
                "Recording AI suggestion decision for order {OrderId} failed (non-fatal).", id);
        }

        return NoContent();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Confidence assigned to a wire derived from the supplier's SAVED (learned) PO mapping.</summary>
    private const double SavedMappingConfidence = 0.95;

    /// <summary>Enumerates a saved PO mapping's header + line entries as (canonicalField, entry) pairs.</summary>
    private static IEnumerable<(string CanonicalField, FieldMappingEntry Entry)> EnumerateMappingEntries(
        PoMappingConfig config)
    {
        foreach (var kv in config.Header)
            yield return (kv.Key, kv.Value);
        foreach (var kv in config.Lines)
            yield return (kv.Key, kv.Value);
    }

    /// <summary>
    /// Pure helper: parse a (possibly EU comma-decimal) raw catalog price EU-aware and compute the
    /// frontend's documented variance = (catalog − po)/po × 100. Returns (catalogPrice, variancePercent),
    /// either of which is null when the price is missing/unusable. Static + side-effect-free so the
    /// EU-comma correctness is unit-tested without a DB.
    /// </summary>
    public static (decimal? CatalogPrice, decimal? VariancePercent) ComputeCatalogVariance(
        string? rawCatalogPrice, decimal poUnitPrice)
    {
        var catalog = PriceVarianceGuard.ParseEuAware(rawCatalogPrice);
        if (catalog is null)
            return (null, null);

        if (poUnitPrice <= 0m)
            return (catalog, null);

        var variance = (catalog.Value - poUnitPrice) / poUnitPrice * 100m;
        return (catalog, decimal.Round(variance, 4));
    }

    /// <summary>
    /// Best-effort line-number extraction from a mapper target key such as
    /// "lines[2].supplierItemCode" / "line:2" / "line.2". A header-level target with no line
    /// index resolves to 0 (the durable schema requires a line number; 0 = order/header scope).
    /// </summary>
    internal static int ExtractLineNumber(string? targetKey)
    {
        if (string.IsNullOrWhiteSpace(targetKey))
            return 0;

        var match = System.Text.RegularExpressions.Regex.Match(targetKey, @"(\d+)");
        return match.Success && int.TryParse(match.Value, out var n) ? n : 0;
    }
}
