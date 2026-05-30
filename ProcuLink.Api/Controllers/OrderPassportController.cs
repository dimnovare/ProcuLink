using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// PO Passport — a read-only evidence trail for a single order, showing how a source
/// purchase order was turned into a supplier-ready delivery (source → canonical →
/// validation → mapping → transform → delivery → supplier response).
///
/// Tenant-scoped: the org is resolved from the Clerk JWT by TenantResolutionMiddleware
/// and injected via <see cref="ICurrentTenantService"/>. An order belonging to another
/// org is reported as 404, never disclosed.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class OrderPassportController : ControllerBase
{
    private readonly IPassportService      _passport;
    private readonly ICurrentTenantService _tenant;

    public OrderPassportController(
        IPassportService      passport,
        ICurrentTenantService tenant)
    {
        _passport = passport;
        _tenant   = tenant;
    }

    /// <summary>
    /// GET /api/orders/{orderId}/passport — returns the full PO Passport for the order.
    /// Returns 404 when the order does not exist for the authenticated organisation.
    /// </summary>
    [HttpGet("{orderId:guid}/passport")]
    [ProducesResponseType(typeof(PassportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPassport(Guid orderId, CancellationToken ct)
    {
        var result = await _passport.GetAsync(_tenant.OrganisationId, orderId, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(result.Value);
    }
}
