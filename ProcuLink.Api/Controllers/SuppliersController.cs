using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Canonical;
using ProcuLink.Infrastructure.Repositories;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/suppliers")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierProfileRepository _supplierProfileRepository;
    private readonly IItemMappingRepository _itemMappingRepository;

    public SuppliersController(
        ISupplierProfileRepository supplierProfileRepository,
        IItemMappingRepository itemMappingRepository)
    {
        _supplierProfileRepository = supplierProfileRepository;
        _itemMappingRepository = itemMappingRepository;
    }

    /// <summary>
    /// Get list of available supplier names from configured profiles
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSuppliers(CancellationToken ct)
    {
        var profiles = await _supplierProfileRepository.ListAsync(ct);
        var supplierNames = profiles.Select(p => p.SupplierName).ToArray();
        return Ok(supplierNames);
    }

    /// <summary>
    /// Get all supplier profiles with full details
    /// </summary>
    [HttpGet("profiles")]
    [ProducesResponseType(typeof(IReadOnlyList<SupplierProfile>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfiles(CancellationToken ct)
    {
        var profiles = await _supplierProfileRepository.ListAsync(ct);
        return Ok(profiles);
    }

    /// <summary>
    /// Get a specific supplier profile by name
    /// </summary>
    [HttpGet("profiles/{supplierName}")]
    [ProducesResponseType(typeof(SupplierProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(string supplierName, CancellationToken ct)
    {
        var profile = await _supplierProfileRepository.GetByNameAsync(supplierName, ct);
        if (profile == null)
            return NotFound();

        return Ok(profile);
    }

    /// <summary>
    /// Get all item code mappings for a supplier
    /// </summary>
    [HttpGet("{supplierName}/mappings")]
    [ProducesResponseType(typeof(IReadOnlyList<ItemCodeMapping>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMappings(string supplierName, CancellationToken ct)
    {
        var mappings = await _itemMappingRepository.ListAsync(supplierName, ct);
        return Ok(mappings);
    }

    /// <summary>
    /// Create or update an item code mapping for a supplier
    /// </summary>
    [HttpPost("{supplierName}/mappings")]
    [ProducesResponseType(typeof(IReadOnlyList<ItemCodeMapping>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpsertMapping(
        string supplierName,
        [FromBody] CreateMappingRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BuyerItemCode))
            return BadRequest("BuyerItemCode is required.");

        if (string.IsNullOrWhiteSpace(request.SupplierItemCode))
            return BadRequest("SupplierItemCode is required.");

        await _itemMappingRepository.UpsertAsync(supplierName, request.BuyerItemCode, request.SupplierItemCode, ct);

        // Return updated list
        var mappings = await _itemMappingRepository.ListAsync(supplierName, ct);
        return Ok(mappings);
    }
}
