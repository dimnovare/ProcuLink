using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Canonical;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/suppliers")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierProfileRepository _supplierProfileRepository;
    private readonly IItemMappingService        _mappingService;
    private readonly ProcuLinkDbContext         _db;
    private readonly ICurrentTenantService      _tenant;

    public SuppliersController(
        ISupplierProfileRepository supplierProfileRepository,
        IItemMappingService        mappingService,
        ProcuLinkDbContext         db,
        ICurrentTenantService      tenant)
    {
        _supplierProfileRepository = supplierProfileRepository;
        _mappingService            = mappingService;
        _db                        = db;
        _tenant                    = tenant;
    }

    // ── GET /api/suppliers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns { id, name } for every supplier in the authenticated org.
    /// Used by the upload form to populate the supplier picker.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSuppliers(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var suppliers = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.OrgId == orgId)
            .OrderBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name })
            .ToListAsync(ct);
        return Ok(suppliers);
    }

    // ── GET /api/suppliers/profiles ───────────────────────────────────────────

    /// <summary>Get all supplier profiles with full details.</summary>
    [HttpGet("profiles")]
    [ProducesResponseType(typeof(IReadOnlyList<SupplierProfile>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfiles(CancellationToken ct)
    {
        var profiles = await _supplierProfileRepository.ListAsync(ct);
        return Ok(profiles);
    }

    /// <summary>Get a specific supplier profile by name.</summary>
    [HttpGet("profiles/{supplierName}")]
    [ProducesResponseType(typeof(SupplierProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(string supplierName, CancellationToken ct)
    {
        var profile = await _supplierProfileRepository.GetByNameAsync(supplierName, ct);
        if (profile == null) return NotFound();
        return Ok(profile);
    }

    // ── GET /api/suppliers/{supplierId}/mappings ──────────────────────────────

    /// <summary>
    /// Returns all item code mappings for the given supplier, scoped to the org.
    /// </summary>
    [HttpGet("{supplierId:guid}/mappings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMappings(Guid supplierId, CancellationToken ct)
    {
        var orgId    = _tenant.OrganisationId;
        var mappings = await _mappingService.GetForSupplierAsync(orgId, supplierId, ct);

        var result = mappings.Select(m => new
        {
            id               = m.Id,
            buyerItemCode    = m.BuyerItemCode,
            supplierItemCode = m.SupplierItemCode,
        });

        return Ok(result);
    }

    // ── DELETE /api/suppliers/{supplierId}/mappings/{mappingId} ───────────────

    /// <summary>Delete a single item code mapping, scoped to the org.</summary>
    [HttpDelete("{supplierId:guid}/mappings/{mappingId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteMapping(
        Guid supplierId,
        Guid mappingId,
        CancellationToken ct)
    {
        // supplierId in the route keeps URLs consistent; the service scopes by orgId.
        var orgId = _tenant.OrganisationId;
        await _mappingService.DeleteAsync(orgId, mappingId, ct);
        return NoContent();
    }
}
