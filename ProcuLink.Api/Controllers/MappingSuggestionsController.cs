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
    [ProducesResponseType(typeof(IReadOnlyList<FieldMappingSuggestion>), StatusCodes.Status200OK)]
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
        if (columns.Count == 0)
            return Ok(Array.Empty<FieldMappingSuggestion>());

        var suggestions = await _suggester.SuggestFieldMappingsAsync(orgId, id, columns, ct);
        return Ok(suggestions);
    }
}

/// <summary>Request body for <c>POST /api/suppliers/{id}/mapping/suggest-fields</c>.</summary>
public sealed record SuggestFieldsRequest(IReadOnlyList<string> Columns);
