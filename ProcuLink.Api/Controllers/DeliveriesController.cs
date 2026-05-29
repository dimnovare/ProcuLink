using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Read-only delivery-attempts history endpoint.
/// Surfaces the retry/dead-letter attempt history for a given order — used by the ops UI.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class DeliveriesController : ControllerBase
{
    private readonly ProcuLinkDbContext    _db;
    private readonly ICurrentTenantService _tenant;

    public DeliveriesController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    /// <summary>
    /// Returns the org-scoped delivery attempts for the given order, newest first.
    /// Returns an empty list when no attempts exist for the order.
    /// </summary>
    [HttpGet("{id:guid}/delivery-attempts")]
    [ProducesResponseType(typeof(IReadOnlyList<DeliveryAttemptDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDeliveryAttempts(
        Guid id,
        CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;

        var attempts = await _db.DeliveryAttempts
            .AsNoTracking()
            .Where(a => a.OrgId == orgId && a.OrderId == id)
            .OrderByDescending(a => a.AttemptedAt)
            .Select(a => new DeliveryAttemptDto(
                a.AttemptNumber,
                a.Channel,
                a.Destination,
                a.Status,
                a.AttemptedAt,
                a.ResponseCode,
                a.ErrorMessage))
            .ToListAsync(ct);

        return Ok(attempts);
    }
}

/// <summary>DTO returned by <see cref="DeliveriesController.GetDeliveryAttempts"/>.</summary>
public sealed record DeliveryAttemptDto(
    int       AttemptNumber,
    string    Channel,
    string    Destination,
    string    Status,
    DateTime  AttemptedAt,
    int?      ResponseCode,
    string?   ErrorMessage);
