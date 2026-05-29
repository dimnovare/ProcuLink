using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// "Magic mapping" support: suggests which canonical PO field each of a supplier's
/// detected source columns most likely maps to. Powers the onboarding mapping UI.
///
/// Kept in its own controller (not <c>SuppliersController</c>) deliberately so the
/// route group can evolve independently.
/// </summary>
[Authorize]
[ApiController]
[Route("api/suppliers")]
public sealed class MappingSuggestionsController : ControllerBase
{
    private readonly IFieldMappingSuggester _suggester;
    private readonly ProcuLinkDbContext _db;
    private readonly ICurrentTenantService _tenant;

    public MappingSuggestionsController(
        IFieldMappingSuggester suggester,
        ProcuLinkDbContext db,
        ICurrentTenantService tenant)
    {
        _suggester = suggester;
        _db = db;
        _tenant = tenant;
    }

    // ── POST /api/suppliers/{id}/mapping/suggest-fields ───────────────────────

    /// <summary>
    /// Given the source column headers detected in a supplier's file, suggest the
    /// best-matching canonical PO field for each, with confidence, reason, and
    /// provenance ("heuristic" or "ai").
    /// </summary>
    /// <remarks>
    /// Always works without an AI key (deterministic heuristic). An empty
    /// <c>columns</c> array returns <c>200 OK</c> with an empty result.
    /// </remarks>
    [HttpPost("{id:guid}/mapping/suggest-fields")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType(typeof(IReadOnlyList<FieldMappingSuggestion>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuggestFields(
        Guid id,
        [FromBody] SuggestFieldsRequest request,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // Org-scoped supplier existence check (mirrors SuppliersController pattern).
        var supplierExists = await _db.Suppliers
            .AnyAsync(s => s.Id == id && s.OrgId == orgId && s.DeletedAt == null, ct);
        if (!supplierExists)
            return NotFound();

        var columns = request?.Columns ?? Array.Empty<string>();

        // Bound the work: the heuristic scores every (canonical field × column) pair and runs
        // several full-length Contains scans per column, so an oversized request would burn CPU/GC.
        // A real PO file never has hundreds of distinct headers; reject the absurd up front.
        if (columns.Count > MaxColumns)
            return ValidationProblem(
                detail: $"Too many columns: {columns.Count}. A maximum of {MaxColumns} is allowed.",
                statusCode: StatusCodes.Status400BadRequest);

        // Drop pathologically long individual headers — a column name that long is never a real
        // header, and a single huge string is the cheapest way to force a Contains-scan spike.
        var bounded = columns
            .Where(c => c is not null && c.Length <= MaxColumnNameLength)
            .ToList();

        if (bounded.Count == 0)
            return Ok(Array.Empty<FieldMappingSuggestion>());

        var suggestions = await _suggester.SuggestFieldMappingsAsync(orgId, id, bounded, ct);
        return Ok(suggestions);
    }

    /// <summary>~256 KB per-request body cap. Overrides the 30 MB Kestrel default for this action.</summary>
    private const int MaxRequestBytes = 262144;

    /// <summary>Maximum number of source columns accepted in one request (anything more is rejected 400).</summary>
    private const int MaxColumns = 512;

    /// <summary>Individual column names longer than this are ignored — a header that long is never real.</summary>
    private const int MaxColumnNameLength = 200;
}

/// <summary>Request body for <c>POST /api/suppliers/{id}/mapping/suggest-fields</c>.</summary>
public sealed record SuggestFieldsRequest(IReadOnlyList<string> Columns);
