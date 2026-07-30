using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services.StarterTemplates;
using ProcuLink.Core.Canonical;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Transform.Catalog;
using ProcuLink.Transform.Mapping;

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
    private readonly IPoMappingService          _poMappingService;
    private readonly IDeliveryConfigService     _deliveryConfigService;
    private readonly IDeliveryService           _deliveryService;
    private readonly IAnalyticsService          _analytics;
    private readonly IFileStorageService        _fileStorage;
    private readonly ISourceColumnExtractor     _sourceColumns;
    private readonly IStarterTemplateService    _starterTemplates;
    private readonly ISupplierCatalogService     _catalog;
    private readonly ISupplierConnectionService  _connections;

    public SuppliersController(
        ISupplierProfileRepository supplierProfileRepository,
        IItemMappingService        mappingService,
        ProcuLinkDbContext         db,
        ICurrentTenantService      tenant,
        IBillingService            billing,
        IPoMappingService          poMappingService,
        IDeliveryConfigService     deliveryConfigService,
        IDeliveryService           deliveryService,
        IAnalyticsService          analytics,
        IFileStorageService        fileStorage,
        ISourceColumnExtractor     sourceColumns,
        IStarterTemplateService    starterTemplates,
        ISupplierCatalogService    catalog,
        ISupplierConnectionService connections)
    {
        _supplierProfileRepository = supplierProfileRepository;
        _mappingService            = mappingService;
        _db                        = db;
        _tenant                    = tenant;
        _billing                   = billing;
        _poMappingService          = poMappingService;
        _deliveryConfigService     = deliveryConfigService;
        _deliveryService           = deliveryService;
        _analytics                 = analytics;
        _fileStorage               = fileStorage;
        _sourceColumns             = sourceColumns;
        _starterTemplates          = starterTemplates;
        _catalog                   = catalog;
        _connections               = connections;
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
            .Select(s => new
            {
                id = s.Id,
                name = s.Name,
                // Identity fields (D1). Null until an org fills them in; the supplier-profile form
                // that writes them is a separate FE chip, but the contract is live now so that chip
                // has something to bind to and supplier auto-detect has a right-hand side.
                vatNumber = s.VatNumber,
                registrationNumber = s.RegistrationNumber,
                ediCode = s.EdiCode,
                primaryDomain = s.PrimaryDomain,
            })
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

        var hadSuppliers = await _db.Suppliers
            .AnyAsync(s => s.OrgId == orgId && s.DeletedAt == null, ct);

        var supplier = new Supplier
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = nameTrimmed,
            CreatedAt = DateTime.UtcNow,
            VatNumber          = Trimmed(request.VatNumber),
            RegistrationNumber = Trimmed(request.RegistrationNumber),
            EdiCode            = Trimmed(request.EdiCode),
            PrimaryDomain      = NormalizedDomain(request.PrimaryDomain),
        };

        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync(ct);

        if (!hadSuppliers)
        {
            await _analytics.CaptureAsync(
                organisationId: orgId,
                userId: null,
                eventName: "first_supplier_added",
                properties: new Dictionary<string, object?>
                {
                    ["supplier_id"] = supplier.Id,
                },
                ct: ct);
        }

        var result = SupplierDetails(supplier);
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

        // Patch-style: only a value that was actually supplied is written. An empty string is the
        // explicit "clear this" and normalises to null. See RenameSupplierRequest for why a plain
        // overwrite would be destructive here.
        if (request.VatNumber is not null)          supplier.VatNumber = Trimmed(request.VatNumber);
        if (request.RegistrationNumber is not null) supplier.RegistrationNumber = Trimmed(request.RegistrationNumber);
        if (request.EdiCode is not null)            supplier.EdiCode = Trimmed(request.EdiCode);
        if (request.PrimaryDomain is not null)      supplier.PrimaryDomain = NormalizedDomain(request.PrimaryDomain);

        await _db.SaveChangesAsync(ct);

        return Ok(SupplierDetails(supplier));
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
            confidence       = m.Confidence,
            source           = m.Source,
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

    // ── POST /api/suppliers/{supplierId}/mappings ────────────────────────────────

    public sealed record CreateMappingRequest(string BuyerItemCode, string SupplierItemCode);

    /// <summary>Create a new item code mapping for the given supplier.</summary>
    [HttpPost("{supplierId:guid}/mappings")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateMapping(
        Guid supplierId,
        [FromBody] CreateMappingRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BuyerItemCode) ||
            string.IsNullOrWhiteSpace(request.SupplierItemCode))
            return BadRequest("BuyerItemCode and SupplierItemCode are required.");

        var orgId   = _tenant.OrganisationId;
        var mapping = await _mappingService.CreateAsync(
            orgId, supplierId,
            request.BuyerItemCode, request.SupplierItemCode,
            MappingSource.Manual, ct);

        return StatusCode(201, new
        {
            id               = mapping.Id,
            buyerItemCode    = mapping.BuyerItemCode,
            supplierItemCode = mapping.SupplierItemCode,
            confidence       = mapping.Confidence,
            source           = mapping.Source,
        });
    }

    // ── PUT /api/suppliers/{supplierId}/mappings/{mappingId} ─────────────────────

    public sealed record UpdateMappingRequest(string BuyerItemCode, string SupplierItemCode);

    /// <summary>Update an existing item code mapping.</summary>
    [HttpPut("{supplierId:guid}/mappings/{mappingId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMapping(
        Guid supplierId,
        Guid mappingId,
        [FromBody] UpdateMappingRequest request,
        CancellationToken ct)
    {
        var orgId   = _tenant.OrganisationId;
        var mapping = await _mappingService.UpdateByIdAsync(
            orgId, mappingId,
            request.BuyerItemCode, request.SupplierItemCode,
            MappingSource.Manual, ct);

        if (mapping is null) return NotFound();

        return Ok(new
        {
            id               = mapping.Id,
            buyerItemCode    = mapping.BuyerItemCode,
            supplierItemCode = mapping.SupplierItemCode,
            confidence       = mapping.Confidence,
            source           = mapping.Source,
        });
    }

    // ── POST /api/suppliers/{supplierId}/mappings/import ─────────────────────────

    /// <summary>Bulk-import item code mappings from a CSV file (buyer_code, supplier_code columns).</summary>
    [HttpPost("{supplierId:guid}/mappings/import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportMappings(
        Guid supplierId,
        IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        var orgId   = _tenant.OrganisationId;
        var created = 0;
        var updated = 0;

        using var reader = new System.IO.StreamReader(file.OpenReadStream());
        var header = await reader.ReadLineAsync(ct); // skip header
        if (header is null) return BadRequest("Empty file.");

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',');
            if (parts.Length < 2) continue;

            var buyerCode    = parts[0].Trim().Trim('"');
            var supplierCode = parts[1].Trim().Trim('"');
            if (string.IsNullOrEmpty(buyerCode) || string.IsNullOrEmpty(supplierCode)) continue;

            var existing = await _db.ItemMappings
                .Where(m => m.OrgId == orgId && m.SupplierId == supplierId && m.BuyerItemCode == buyerCode)
                .FirstOrDefaultAsync(ct);

            if (existing is null)
            {
                await _mappingService.CreateAsync(orgId, supplierId, buyerCode, supplierCode, MappingSource.Imported, ct);
                created++;
            }
            else
            {
                await _mappingService.UpdateByIdAsync(orgId, existing.Id, buyerCode, supplierCode, MappingSource.Imported, ct);
                updated++;
            }
        }

        return Ok(new { created, updated });
    }

    // ── GET /api/suppliers/{id}/po-mapping ────────────────────────────────────

    [HttpGet("{id:guid}/po-mapping")]
    public async Task<IActionResult> GetPoMapping(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id && s.DeletedAt == null).FirstOrDefaultAsync(ct);
        if (supplier is null) return NotFound();
        var config = await _poMappingService.GetAsync(orgId, id, ct);
        if (config is null) return NoContent();
        return Ok(config);
    }

    // ── PUT /api/suppliers/{id}/po-mapping ────────────────────────────────────

    [HttpPut("{id:guid}/po-mapping")]
    public async Task<IActionResult> UpsertPoMapping(Guid id, [FromBody] PoMappingConfig config, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id && s.DeletedAt == null).FirstOrDefaultAsync(ct);
        if (supplier is null) return NotFound();
        var saved = await _poMappingService.UpsertAsync(orgId, id, config, ct);
        return Ok(saved);
    }

    // ── DELETE /api/suppliers/{id}/po-mapping ─────────────────────────────────

    [HttpDelete("{id:guid}/po-mapping")]
    public async Task<IActionResult> DeletePoMapping(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id && s.DeletedAt == null).FirstOrDefaultAsync(ct);
        if (supplier is null) return NotFound();
        await _poMappingService.DeleteAsync(orgId, id, ct);
        return NoContent();
    }

    // ── POST /api/suppliers/{id}/po-mapping/test ──────────────────────────────

    [HttpPost("{id:guid}/po-mapping/test")]
    public async Task<IActionResult> TestPoMapping(Guid id, [FromBody] TestPoMappingRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id && s.DeletedAt == null).FirstOrDefaultAsync(ct);
        if (supplier is null) return NotFound();
        var result = PoMappingEngine.Apply(request.HeaderRow, request.LineRows, request.Config);
        return Ok(result);
    }

    // ── POST /api/suppliers/{id}/po-mapping/apply-template ────────────────────

    /// <summary>
    /// Copies a read-only starter mapping template (e.g. "erply", "directo") into
    /// this supplier's PO mapping config via the existing upsert path. One-click,
    /// zero-code: the persisted config is identical to PUT-ing the template config.
    /// Returns the saved <see cref="PoMappingConfig"/> so the editor can show it for review.
    /// </summary>
    [HttpPost("{id:guid}/po-mapping/apply-template")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApplyPoMappingTemplate(
        Guid id,
        [FromBody] ApplyPoMappingTemplateRequest request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.TemplateId))
            return BadRequest(new { error = "templateId is required." });

        var orgId = _tenant.OrganisationId;
        var supplier = await _db.Suppliers
            .Where(s => s.OrgId == orgId && s.Id == id && s.DeletedAt == null)
            .FirstOrDefaultAsync(ct);
        if (supplier is null) return NotFound();

        var template = _starterTemplates.GetAll()
            .FirstOrDefault(t => string.Equals(t.Id, request.TemplateId, StringComparison.OrdinalIgnoreCase));
        if (template is null)
            return NotFound(new { error = $"Unknown starter template '{request.TemplateId}'." });

        var saved = await _poMappingService.UpsertAsync(orgId, id, template.Config, ct);
        return Ok(saved);
    }

    // ── GET /api/suppliers/{id}/mapping/source-columns ────────────────────────

    /// <summary>
    /// Returns the SOURCE field names detected in this supplier's most-recent uploaded order
    /// file, for ANY supported format (CSV/XLSX headers, XML leaf elements + attributes, EDI
    /// segment tags). Powers the magic-mapping UI so the user can map their own file's columns
    /// onto the canonical PO model without re-uploading.
    ///
    /// Returns 200 with an empty <c>columns</c> array and a <c>hint</c> when the supplier has no
    /// order with a stored source file yet (e.g. only API/email-extracted orders, or no orders) —
    /// it does NOT 404 for that case. 404 is reserved for an unknown/foreign supplier id.
    /// </summary>
    [HttpGet("{id:guid}/mapping/source-columns")]
    [ProducesResponseType(typeof(SourceColumnsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMappingSourceColumns(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        if (!await SupplierExistsAsync(orgId, id, ct))
            return NotFound();

        // Most-recent order for this supplier that actually has a stored source file.
        // Orders created via the API/email-body path have a null SourceFileKey and are skipped,
        // as are orders whose source blob was purged per the org's retention policy (the key
        // remains on the row, but the blob is gone — pick the newest NON-purged file instead).
        var source = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId
                     && o.SupplierId == id
                     && o.SourceFileKey != null
                     && o.SourceFilePurgedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Id, o.SourceFileKey })
            .FirstOrDefaultAsync(ct);

        if (source is null || string.IsNullOrWhiteSpace(source.SourceFileKey))
        {
            return Ok(new SourceColumnsResponse(
                Format: "unknown",
                Columns: Array.Empty<string>(),
                SourceOrderId: null,
                Hint: "Upload a sample order for this supplier to detect its columns."));
        }

        // Download + extract. Storage/extraction failures degrade gracefully to an empty
        // result with a hint rather than surfacing a 500 to the mapping UI.
        try
        {
            await using var fileStream = await _fileStorage.DownloadAsync(source.SourceFileKey, ct);
            var extracted = await _sourceColumns.ExtractAsync(fileStream, source.SourceFileKey, null, ct);

            var hint = extracted.Columns.Count == 0
                ? "We could not detect column names in the latest file for this supplier."
                : null;

            return Ok(new SourceColumnsResponse(
                Format: extracted.Format,
                Columns: extracted.Columns,
                SourceOrderId: source.Id,
                Hint: hint));
        }
        catch (Exception)
        {
            return Ok(new SourceColumnsResponse(
                Format: "unknown",
                Columns: Array.Empty<string>(),
                SourceOrderId: source.Id,
                Hint: "The latest source file for this supplier could not be read."));
        }
    }

    // ── GET /api/suppliers/{id}/delivery-config ──────────────────────────────

    [HttpGet("{id:guid}/delivery-config")]
    [ProducesResponseType(typeof(DeliveryConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeliveryConfig(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var config = await _deliveryConfigService.GetAsync(orgId, id, ct);
        return config is null ? NoContent() : Ok(await WithGovernanceAsync(orgId, config, ct));
    }

    // ── PUT /api/suppliers/{id}/delivery-config ──────────────────────────────

    [HttpPut("{id:guid}/delivery-config")]
    [ProducesResponseType(typeof(DeliveryConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpsertDeliveryConfig(
        Guid id,
        [FromBody] UpsertDeliveryConfigRequest request,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        try
        {
            var saved = await _deliveryConfigService.UpsertAsync(orgId, id, request, ct);

            // Honest + route-to-versioned: when revision authority governs this supplier's
            // delivery, the live row we just saved does NOT drive pinned orders on its own — so
            // publish a new connection revision snapshotting the edit, making it actually take
            // effect for future orders. No-op when the supplier is not revision-governed.
            await _connections.RepublishLiveDeliveryAsync(orgId, id, _tenant.ClerkUserId, ct);

            return Ok(await WithGovernanceAsync(orgId, saved, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Annotates a delivery-config response with how delivery is GOVERNED: when revision authority
    /// routes this supplier's delivery via a published connection revision, the editor must show the
    /// operator that delivery follows that versioned snapshot (and whether the live row is in sync),
    /// not the raw row alone.
    /// </summary>
    private async Task<DeliveryConfigResponse> WithGovernanceAsync(
        Guid orgId, DeliveryConfigResponse config, CancellationToken ct)
    {
        var gov = await _connections.DescribeDeliveryGovernanceAsync(orgId, config.SupplierId, ct);
        return config with
        {
            RevisionGoverned                  = gov.RevisionGoverned,
            ActiveRevisionVersionNo           = gov.ActiveVersionNo,
            LiveMatchesActiveRevisionDelivery = gov.LiveMatchesActiveDelivery,
        };
    }

    // ── DELETE /api/suppliers/{id}/delivery-config ───────────────────────────

    [HttpDelete("{id:guid}/delivery-config")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDeliveryConfig(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        await _deliveryConfigService.DeleteAsync(orgId, id, ct);
        return NoContent();
    }

    // ── POST /api/suppliers/{id}/delivery-config/test-fire ───────────────────

    [HttpPost("{id:guid}/delivery-config/test-fire")]
    [ProducesResponseType(typeof(DeliveryTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestFireDeliveryConfig(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var config = await _deliveryConfigService.GetAsync(orgId, id, ct);
        if (config is null) return NotFound(new { error = "Delivery config not found." });

        var result = await _deliveryService.TestFireAsync(orgId, id, ct);
        return Ok(result);
    }

    // ── GET /api/suppliers/{id}/catalog ───────────────────────────────────────

    /// <summary>
    /// Lists the supplier's product catalog (active rows) — the authoritative set of real codes.
    /// Optional <c>?q=</c> filters by code/name/barcode (case-insensitive contains); <c>?take=</c>
    /// caps the page size. Returns 200 with <c>{ total, items: [{ id, code, name, unit, price,
    /// currency, barcode, externalId }] }</c>. 404 for an unknown/foreign supplier.
    /// </summary>
    [HttpGet("{id:guid}/catalog")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCatalog(
        Guid id,
        [FromQuery] string? q,
        [FromQuery] int? take,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var total = await _catalog.CountAsync(orgId, id, ct);
        var items = await _catalog.ListAsync(orgId, id, q, take, ct);

        return Ok(new
        {
            total,
            items = items.Select(p => new
            {
                id         = p.Id,
                code       = p.Code,
                name       = p.Name,
                unit       = p.Unit,
                price      = p.Price,
                currency   = p.Currency,
                barcode    = p.Barcode,
                externalId = p.ExternalId,
                manufacturerPartNumber = p.ManufacturerPartNumber,
                manufacturerName       = p.ManufacturerName,
            }),
        });
    }

    // ── POST /api/suppliers/{id}/catalog/import ───────────────────────────────

    /// <summary>
    /// Bulk-imports catalog products from a CSV or XLSX file. Columns are auto-detected
    /// case-insensitively against common aliases: code (required), name, unit, price,
    /// currency, barcode, external_id. Rows are idempotently upserted by (org, supplier, code).
    /// Returns <c>{ created, updated, skipped, total }</c>. 404 for an unknown/foreign supplier.
    /// </summary>
    [HttpPost("{id:guid}/catalog/import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportCatalog(
        Guid id,
        IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        List<SupplierProduct> drafts;
        try
        {
            // Shared parser (extension routing identical to the previous in-controller logic;
            // no/unknown extension falls back to CSV parsing). Also enforces the row cap
            // (SupplierCatalogFileParser.MaxCatalogRows) and the XLSX zip-bomb guard.
            await using var stream = file.OpenReadStream();
            var parseResult = await SupplierCatalogFileParser.ParseByFileNameAsync(stream, file.FileName, ct);
            drafts = parseResult.Drafts;
        }
        catch (CatalogTooLargeException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception)
        {
            return BadRequest("Could not read the catalog file. Provide a CSV, XLSX, JSON, XML, or CIF catalog with a 'code' column.");
        }

        var parsed   = drafts.Count;
        var withCode = drafts.Count(d => !string.IsNullOrWhiteSpace(d.Code));
        if (withCode == 0)
            return BadRequest("No rows with a product code were found. Ensure the file has a 'code' column.");

        var (created, updated) = await _catalog.UpsertManyAsync(orgId, id, drafts, ct);

        return Ok(new
        {
            created,
            updated,
            skipped = parsed - withCode,
            total   = created + updated,
        });
    }

    // ── DELETE /api/suppliers/{id}/catalog ────────────────────────────────────

    /// <summary>Clears the supplier's entire product catalog. Returns <c>{ deleted }</c>.
    /// 404 for an unknown/foreign supplier.</summary>
    [HttpDelete("{id:guid}/catalog")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearCatalog(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var deleted = await _catalog.DeleteAsync(orgId, id, ct);
        return Ok(new { deleted });
    }

    // ── Catalog pull source (SFTP / FTP / FTPS) — plan 2026-06-12 ──────────────
    // New dependencies arrive via [FromServices] so the (already large) constructor —
    // and every existing controller test that builds it — stays untouched.

    /// <summary>
    /// Returns the supplier's catalog pull-source config, or <c>{ source: null }</c> when
    /// none is configured. Secrets are masked (<c>hasPassword</c> + <c>"********"</c>);
    /// neither the plaintext nor the ciphertext ever leaves the server.
    /// </summary>
    [HttpGet("{id:guid}/catalog/source")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCatalogSource(
        Guid id,
        [FromServices] ICatalogSourceSettingsService settings,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var source = await settings.GetAsync(orgId, id, ct);
        return Ok(new { source });
    }

    /// <summary>
    /// Upserts the supplier's catalog pull-source config. Password semantics:
    /// null = keep stored, "" = clear, value = re-encrypt (AES-256-GCM, write-only).
    /// Enabling is billing-gated; the host passes a save-time SSRF pre-check; flipping
    /// IsEnabled false→true enqueues the first sync (deduped against running/recent syncs).
    /// </summary>
    [HttpPut("{id:guid}/catalog/source")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpsertCatalogSource(
        Guid id,
        [FromBody] UpsertCatalogSourceRequest request,
        [FromServices] ICatalogSourceSettingsService settings,
        [FromServices] OutboundRequestGuard guard,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var protocol = request.Protocol?.Trim().ToLowerInvariant();
        if (protocol is not ("sftp" or "ftp" or "ftps" or "http" or "https" or "logicom"))
            return BadRequest(new { error = "Protocol must be one of: sftp, ftp, ftps, http, https, logicom." });

        var isVendor = protocol is "logicom";
        var isHttp = protocol is "http" or "https";
        var isUrlBased = isHttp || isVendor;

        var fileFormat = string.IsNullOrWhiteSpace(request.FileFormat)
            ? "auto"
            : request.FileFormat.Trim().ToLowerInvariant();
        if (fileFormat is not ("auto" or "csv" or "xlsx" or "json" or "xml" or "cif"))
            return BadRequest(new { error = "File format must be one of: auto, csv, xlsx, json, xml, cif." });

        // Per-source column mapping (plan 2026-07-02 D3): values must be canonical fields
        // (plus the __noheader__ / __encoding__ directives). Reject unknown targets early.
        if (request.ColumnMapping is not null)
        {
            // Derived from the parser's own list rather than restated here: this check used to be
            // a hand-copied duplicate, so every field added to the parser silently 400'd until
            // someone remembered to update it too.
            var validTargets = ProcuLink.Transform.Catalog.SupplierCatalogFileParser.CanonicalFields.ToArray();
            foreach (var (key, value) in request.ColumnMapping)
            {
                if (key is "__noheader__" or "__encoding__") continue;
                if (!validTargets.Contains(value?.Trim().ToLowerInvariant()))
                    return BadRequest(new
                    {
                        error = $"Column mapping target '{value}' is not valid. Valid targets: {string.Join(", ", validTargets)}.",
                    });
            }
        }

        // ── URL-based branches — auth never in the URL ─────────────────────────────
        if (isUrlBased)
        {
            if (string.IsNullOrWhiteSpace(request.Url))
                return BadRequest(new { error = "URL is required for http, https, and vendor connectors." });
            if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                return BadRequest(new { error = "URL must be an absolute http or https URL." });
            // SECURITY: credentials must never live in the URL (they would leak to logs/Sentry/db).
            if (!string.IsNullOrEmpty(url.UserInfo))
                return BadRequest(new { error = "credentials_in_url_not_allowed" });

            if (isHttp)
            {
                var authMethod = request.AuthMethod?.Trim().ToLowerInvariant();
                if (authMethod is not (null or "" or "none" or "apikey" or "bearer" or "basic" or "oauth2_client_credentials"))
                    return BadRequest(new { error = "Auth method must be one of: none, apikey, bearer, basic, oauth2_client_credentials." });
                // v2: GET only (POST-with-body is out of scope).
                var httpMethod = request.HttpMethod?.Trim().ToUpperInvariant();
                if (httpMethod is not (null or "" or "GET"))
                    return BadRequest(new { error = "Only the GET http method is supported." });
            }
            else if (protocol == "logicom")
            {
                // Vendor creds required — either provided now, or kept from a previous save.
                var current = await settings.GetAsync(orgId, id, ct);
                var provided = request.VendorConfig is { } vc
                    && !string.IsNullOrWhiteSpace(vc.CustomerId)
                    && !string.IsNullOrWhiteSpace(vc.ConsumerKey)
                    && !string.IsNullOrWhiteSpace(vc.ConsumerSecret)
                    && !string.IsNullOrWhiteSpace(vc.AccessTokenKey);
                var kept = request.VendorConfig is null && current?.HasAuthConfig == true;
                if (!provided && !kept)
                    return BadRequest(new { error = "Logicom requires CustomerID, ConsumerKey, ConsumerSecret, and AccessTokenKey." });
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return BadRequest(new { error = "Host is required." });
            if (string.IsNullOrWhiteSpace(request.RemotePath))
                return BadRequest(new { error = "Remote file path is required." });
            if (protocol is "sftp" or "ftps" && string.IsNullOrWhiteSpace(request.Username))
                return BadRequest(new { error = "Username is required for sftp and ftps." });

            // sftp/ftps need a password: either provided now, or kept from a previous save.
            if (protocol is "sftp" or "ftps")
            {
                var current = await settings.GetAsync(orgId, id, ct);
                var hasPassword = !string.IsNullOrWhiteSpace(request.Password)
                                  || (request.Password is null && current?.HasPassword == true);
                if (!hasPassword)
                    return BadRequest(new { error = "Password is required for sftp and ftps." });
            }
        }

        // Billing gate on enablement (pull reuses the SFTP-ingestion feature tier).
        if (request.IsEnabled && !await _billing.HasFeatureAsync(orgId, BillingFeature.SftpIngestion, ct))
            return StatusCode(StatusCodes.Status403Forbidden,
                new
                {
                    error = BillingGateErrors.RequiresPlan("catalog_sync", BillingFeature.SftpIngestion),
                    upgradeUrl = "/settings"
                });

        // Save-time SSRF pre-check — fail fast in the UI instead of at the first poll.
        // The pull pipeline re-validates immediately before EVERY connect regardless.
        if (isUrlBased)
        {
            var urlGuard = await guard.ValidateAsync(request.Url!.Trim(), ct);
            if (!urlGuard.Allowed)
                return BadRequest(new { error = "host_not_allowed" });
        }
        else
        {
            var port = request.Port > 0 ? request.Port : (protocol == "sftp" ? 22 : 21);
            var guardResult = await guard.ValidateHostAsync(request.Host.Trim(), port, ct);
            if (!guardResult.Allowed)
                return BadRequest(new { error = "host_not_allowed" });
        }

        var result = await settings.UpsertAsync(orgId, id, request, ct);
        return Ok(new { source = result.Source, syncEnqueued = result.SyncEnqueued });
    }

    /// <summary>Deletes the supplier's catalog pull-source config. Returns <c>{ deleted }</c>.</summary>
    [HttpDelete("{id:guid}/catalog/source")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCatalogSource(
        Guid id,
        [FromServices] ICatalogSourceSettingsService settings,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var deleted = await settings.DeleteAsync(orgId, id, ct);
        return Ok(new { deleted });
    }

    /// <summary>
    /// Read-only honesty probe: fetches the SAVED source through the exact production pull
    /// path (SSRF guard, timeouts, bounded read, shared parse) and reports what would be
    /// imported — mapped/unmapped columns, row counts, and ≤5 sample rows. Never writes:
    /// no upsert, no last-sync mutation. Failures return <c>ok: false</c> with the same
    /// sanitized message a real sync would persist.
    /// </summary>
    [HttpPost("{id:guid}/catalog/source/test-fetch")]
    [EnableRateLimiting("upload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestFetchCatalogSource(
        Guid id,
        [FromServices] ICatalogPullService pull,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await SupplierExistsAsync(orgId, id, ct)) return NotFound();

        var result = await pull.TestFetchAsync(orgId, id, ct);
        return Ok(result);
    }

    // Catalog file parsing lives in ProcuLink.Transform\Catalog\SupplierCatalogFileParser —
    // shared verbatim with the API-key push endpoint and the Worker pull channel.

    private Task<bool> SupplierExistsAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        _db.Suppliers.AnyAsync(s => s.Id == supplierId && s.OrgId == orgId && s.DeletedAt == null, ct);

    // ── Supplier identity fields (D1) ─────────────────────────────────────────

    /// <summary>Trims an optional identity value; blank becomes null so "" reads as "cleared", not as an empty match key.</summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Stores a primary domain in the same canonical form inbound sender domains are stored in, so
    /// the two are comparable by plain equality. A user who types "https://www.Acme.com/" or
    /// "redacted@example.invalid" gets "acme.com" either way.
    /// </summary>
    private static string? NormalizedDomain(string? value)
    {
        var trimmed = Trimmed(value);
        if (trimmed is null) return null;

        // Tolerate a pasted URL: drop scheme and anything from the first slash on.
        var withoutScheme = trimmed
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        var slash = withoutScheme.IndexOf('/');
        if (slash >= 0) withoutScheme = withoutScheme[..slash];

        return SupplierSuggestionScoring.NormalizeDomain(withoutScheme);
    }

    /// <summary>The supplier shape returned by create and update — same fields the list returns.</summary>
    private static object SupplierDetails(Supplier s) => new
    {
        id = s.Id,
        name = s.Name,
        vatNumber = s.VatNumber,
        registrationNumber = s.RegistrationNumber,
        ediCode = s.EdiCode,
        primaryDomain = s.PrimaryDomain,
    };
}

public record TestPoMappingRequest(
    IReadOnlyDictionary<string, string> HeaderRow,
    IReadOnlyList<IReadOnlyDictionary<string, string>> LineRows,
    PoMappingConfig Config);

/// <summary>Request to copy a starter mapping template into a supplier's PO mapping config.</summary>
public record ApplyPoMappingTemplateRequest(string TemplateId);

/// <summary>
/// Response for <c>GET /api/suppliers/{id}/mapping/source-columns</c>.
/// </summary>
/// <param name="Format">Detected format label (csv/xlsx/cxml/ubl/xml/edifact/x12/pdf/unknown).</param>
/// <param name="Columns">De-duplicated source field names found in the latest source file.</param>
/// <param name="SourceOrderId">The order whose source file was inspected, or null when none was available.</param>
/// <param name="Hint">Short user-facing guidance when columns are empty; null when columns were found.</param>
public record SourceColumnsResponse(
    string Format,
    IReadOnlyList<string> Columns,
    Guid? SourceOrderId,
    string? Hint);
