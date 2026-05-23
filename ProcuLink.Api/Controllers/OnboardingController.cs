using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/onboarding")]
public class OnboardingController : ControllerBase
{
    private readonly ProcuLinkDbContext    _db;
    private readonly ICurrentTenantService _tenant;

    public OnboardingController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    // ── GET /api/onboarding/status ────────────────────────────────────────────

    /// <summary>
    /// Returns whether the org has completed key onboarding milestones.
    /// Used by the dashboard wizard to determine what to show.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var hasSupplier = await _db.Suppliers
            .AnyAsync(s => s.OrgId == orgId && s.DeletedAt == null, ct);

        var hasUpload = await _db.PurchaseOrders
            .AnyAsync(o => o.OrgId == orgId, ct);

        var hasDelivery = await _db.PurchaseOrders
            .AnyAsync(o => o.OrgId == orgId && o.Status == "delivered", ct);

        return Ok(new
        {
            hasSupplier,
            hasUpload,
            hasDelivery,
        });
    }
}
