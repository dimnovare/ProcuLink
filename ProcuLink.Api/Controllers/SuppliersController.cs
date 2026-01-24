using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Canonical;
using ProcuLink.Infrastructure.Repositories;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/suppliers")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierProfileRepository _supplierProfileRepository;

    public SuppliersController(ISupplierProfileRepository supplierProfileRepository)
    {
        _supplierProfileRepository = supplierProfileRepository;
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
}
