using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/buyers")]
public class BuyersController : ControllerBase
{
    private readonly IBuyerService      _buyers;
    private readonly ICurrentTenantService _tenant;

    public BuyersController(IBuyerService buyers, ICurrentTenantService tenant)
    {
        _buyers = buyers;
        _tenant = tenant;
    }

    // ── GET /api/buyers ───────────────────────────────────────────────────────

    /// <summary>Returns all active buyers for the authenticated org.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBuyers(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var list  = await _buyers.ListAsync(orgId, ct);

        var result = list.Select(b => new
        {
            id           = b.Id,
            name         = b.Name,
            code         = b.Code,
            orderCount   = b.OrderCount,
            lastOrderAge = b.LastOrderAge,
            formats      = b.Formats,
        });

        return Ok(result);
    }

    // ── POST /api/buyers ──────────────────────────────────────────────────────

    public sealed record CreateBuyerRequest(string Name, string Code);

    /// <summary>Create a new buyer for the org.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateBuyer(
        [FromBody] CreateBuyerRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { error = "Code is required." });

        var orgId = _tenant.OrganisationId;
        var buyer = await _buyers.CreateAsync(orgId, request.Name, request.Code, ct);

        var result = new
        {
            id           = buyer.Id,
            name         = buyer.Name,
            code         = buyer.Code,
            orderCount   = 0,
            lastOrderAge = (string?)null,
            formats      = Array.Empty<string>(),
        };

        return StatusCode(201, result);
    }

    // ── PUT /api/buyers/{id} ──────────────────────────────────────────────────

    public sealed record UpdateBuyerRequest(string Name, string Code);

    /// <summary>Update name and code of an existing buyer.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateBuyer(
        Guid id,
        [FromBody] UpdateBuyerRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { error = "Code is required." });

        var orgId = _tenant.OrganisationId;
        var buyer = await _buyers.UpdateAsync(orgId, id, request.Name, request.Code, ct);

        if (buyer is null) return NotFound();

        return Ok(new
        {
            id           = buyer.Id,
            name         = buyer.Name,
            code         = buyer.Code,
            orderCount   = 0,
            lastOrderAge = (string?)null,
            formats      = Array.Empty<string>(),
        });
    }

    // ── DELETE /api/buyers/{id} ───────────────────────────────────────────────

    /// <summary>Soft-deletes a buyer (sets deleted_at). Scoped to the org.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteBuyer(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var found = await _buyers.DeleteAsync(orgId, id, ct);

        return found ? NoContent() : NotFound();
    }
}
