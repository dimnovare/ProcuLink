using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Canonical;
using ProcuLink.Infrastructure.Repositories;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/supplier-profiles")]
public class SupplierProfilesController : ControllerBase
{
    private readonly ISupplierProfileRepository _repository;
    private readonly ILogger<SupplierProfilesController> _logger;

    public SupplierProfilesController(
        ISupplierProfileRepository repository,
        ILogger<SupplierProfilesController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// List all supplier profiles
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SupplierProfile>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var profiles = await _repository.ListAsync(ct);
        return Ok(profiles);
    }

    /// <summary>
    /// Get a supplier profile by name
    /// </summary>
    [HttpGet("{supplierName}")]
    [ProducesResponseType(typeof(SupplierProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string supplierName, CancellationToken ct)
    {
        var profile = await _repository.GetByNameAsync(supplierName, ct);
        if (profile == null)
            return NotFound();

        return Ok(profile);
    }

    /// <summary>
    /// Create a new supplier profile
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(SupplierProfile), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] SupplierProfile profile, CancellationToken ct)
    {
        if (profile == null)
            return BadRequest("Request body is required.");

        if (string.IsNullOrWhiteSpace(profile.SupplierName))
            return BadRequest("SupplierName is required.");

        var saved = await _repository.UpsertAsync(profile, ct);

        _logger.LogInformation("Created supplier profile: {SupplierName}", saved.SupplierName);

        return CreatedAtAction(
            nameof(Get),
            new { supplierName = saved.SupplierName },
            saved);
    }

    /// <summary>
    /// Update an existing supplier profile
    /// </summary>
    [HttpPut("{supplierName}")]
    [ProducesResponseType(typeof(SupplierProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(string supplierName, [FromBody] SupplierProfile profile, CancellationToken ct)
    {
        if (profile == null)
            return BadRequest("Request body is required.");

        if (string.IsNullOrWhiteSpace(supplierName))
            return BadRequest("SupplierName in route is required.");

        // Route wins - override body's supplierName with route parameter
        profile.SupplierName = supplierName;

        var saved = await _repository.UpsertAsync(profile, ct);

        _logger.LogInformation("Updated supplier profile: {SupplierName}", saved.SupplierName);

        return Ok(saved);
    }

    /// <summary>
    /// Delete a supplier profile
    /// </summary>
    [HttpDelete("{supplierName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string supplierName, CancellationToken ct)
    {
        var deleted = await _repository.DeleteAsync(supplierName, ct);
        if (!deleted)
            return NotFound();

        _logger.LogInformation("Deleted supplier profile: {SupplierName}", supplierName);

        return NoContent();
    }
}
