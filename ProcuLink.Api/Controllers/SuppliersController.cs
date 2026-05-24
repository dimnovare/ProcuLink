using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Canonical;
using ProcuLink.Core.Entities;
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
    private readonly IBillingService            _billing;

    public SuppliersController(
        ISupplierProfileRepository supplierProfileRepository,
        IItemMappingService        mappingService,
        ProcuLinkDbContext         db,
        ICurrentTenantService      tenant,
        IBillingService            billing)
    {
        _supplierProfileRepository = supplierProfileRepository;
        _mappingService            = mappingService;
        _db                        = db;
        _tenant                    = tenant;
        _billing                   = billing;
    }

    // ── GET /api/suppliers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns { id, name } for every active supplier in the authenticated org.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSuppliers(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var suppliers = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && s.DeletedAt == null)
            .OrderBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name })
            .ToListAsync(ct);
        return Ok(suppliers);
    }

    // ── POST /api/suppliers ───────────────────────────────────────────────────

    /// <summary>Create a new supplier for the org. Name must be unique (case-insensitive).</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateSupplier(
        [FromBody] CreateSupplierRequest request,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // ── Billing limit check ────────────────────────────────────────────
        var limitCheck = await _billing.CheckSupplierLimitAsync(orgId, ct);
        if (!limitCheck.Allowed)
        {
            return StatusCode(429, new
            {
                error      = limitCheck.PilotExpired ? "pilot_expired" : "supplier_limit_reached",
                plan       = limitCheck.Plan,
                limit      = limitCheck.Limit,
                upgradeUrl = "/settings",
            });
        }

        var nameTrimmed = request.Name.Trim();

        var exists = await _db.Suppliers
            .AnyAsync(s => s.OrgId == orgId
                        && s.DeletedAt == null
                        && s.Name.ToLower() == nameTrimmed.ToLower(), ct);

        if (exists)
            return Conflict(new { error = $"A supplier named '{nameTrimmed}' already exists." });

        var supplier = new Supplier
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = nameTrimmed,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync(ct);

        var result = new { id = supplier.Id, name = supplier.Name };
        return CreatedAtAction(nameof(GetSuppliers), result);
    }

    // ── PUT /api/suppliers/{id} ───────────────────────────────────────────────

    /// <summary>Rename a supplier. New name must be unique (case-insensitive) within the org.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RenameSupplier(
        Guid id,
        [FromBody] RenameSupplierRequest request,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var nameTrimmed = request.Name.Trim();

        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId && s.DeletedAt == null, ct);

        if (supplier is null)
            return NotFound();

        // Check name uniqueness — exclude self
        var conflict = await _db.Suppliers
            .AnyAsync(s => s.OrgId == orgId
                        && s.Id != id
                        && s.DeletedAt == null
                        && s.Name.ToLower() == nameTrimmed.ToLower(), ct);

        if (conflict)
            return Conflict(new { error = $"A supplier named '{nameTrimmed}' already exists." });

        supplier.Name = nameTrimmed;
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = supplier.Id, name = supplier.Name });
    }

    // ── DELETE /api/suppliers/{id} ────────────────────────────────────────────

    /// <summary>Soft-deletes a supplier (sets deleted_at). Scoped to the org.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSupplier(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId && s.DeletedAt == null, ct);

        if (supplier is null)
            return NotFound();

        supplier.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return NoContent();
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

    // ── POST /api/suppliers/{id}/profiles ─────────────────────────────────────

    /// <summary>Create or update the supplier profile (output format, destination).</summary>
    [HttpPost("{id:guid}/profiles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpsertProfile(
        Guid id,
        [FromBody] UpsertSupplierProfileRequest request,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId && s.DeletedAt == null, ct);

        if (supplier is null)
            return NotFound();

        var existing = await _db.SupplierProfiles
            .FirstOrDefaultAsync(p => p.SupplierId == id && p.OrgId == orgId, ct);

        JsonDocument? destConfig = null;
        if (!string.IsNullOrWhiteSpace(request.DestinationConfig))
        {
            try { destConfig = JsonDocument.Parse(request.DestinationConfig); }
            catch (JsonException) { return BadRequest(new { error = "DestinationConfig must be valid JSON." }); }
        }

        var now = DateTime.UtcNow;

        if (existing is null)
        {
            _db.SupplierProfiles.Add(new SupplierProfileEntity
            {
                Id               = Guid.NewGuid(),
                SupplierId       = id,
                OrgId            = orgId,
                OutputFormat     = request.OutputFormat,
                DestinationType  = request.DestinationType,
                DestinationConfig = destConfig,
                AcceptedFormats  = request.AcceptedFormats ?? new List<string>(),
                CreatedAt        = now,
                UpdatedAt        = now,
            });
        }
        else
        {
            existing.OutputFormat      = request.OutputFormat;
            existing.DestinationType   = request.DestinationType;
            existing.DestinationConfig = destConfig;
            existing.AcceptedFormats   = request.AcceptedFormats ?? existing.AcceptedFormats;
            existing.UpdatedAt         = now;
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            supplierId      = id,
            outputFormat    = request.OutputFormat,
            destinationType = request.DestinationType,
        });
    }

    // ── GET /api/suppliers/{supplierId}/mappings ──────────────────────────────

    /// <summary>Returns all item code mappings for the given supplier, scoped to the org.</summary>
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
        var orgId = _tenant.OrganisationId;
        await _mappingService.DeleteAsync(orgId, mappingId, ct);
        return NoContent();
    }
}
