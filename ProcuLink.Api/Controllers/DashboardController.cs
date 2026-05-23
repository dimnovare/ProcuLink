using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly ProcuLinkDbContext    _db;
    private readonly ICurrentTenantService _tenant;

    public DashboardController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    // ── GET /api/dashboard/stats ──────────────────────────────────────────────

    /// <summary>
    /// Returns high-level stats for the current month.
    /// { totalOrdersThisMonth, pendingReview, delivered }
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var orgId     = _tenant.OrganisationId;
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var totalThisMonth = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.CreatedAt >= monthStart, ct);

        var pendingReview = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.Status == "pending_review", ct);

        var delivered = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.Status == "delivered", ct);

        var totalOrders = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId, ct);

        return Ok(new
        {
            totalOrdersThisMonth = totalThisMonth,
            pendingReview,
            delivered,
            totalOrders,
        });
    }
}
