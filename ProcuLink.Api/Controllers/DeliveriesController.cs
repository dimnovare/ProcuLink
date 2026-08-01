using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
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

        // Projected to a row first, then mapped: SupplierResponseClassification.CauseFor is a C#
        // decision, not something the provider can translate into SQL. Same single query, same
        // columns — only the shaping moves client-side.
        var attempts = await _db.DeliveryAttempts
            .AsNoTracking()
            .Where(a => a.OrgId == orgId && a.OrderId == id)
            .OrderByDescending(a => a.AttemptedAt)
            .Select(a => new
            {
                a.AttemptNumber,
                a.Channel,
                a.Destination,
                a.Status,
                a.AttemptedAt,
                a.ResponseCode,
                a.ErrorMessage,
                a.RejectionReason,
                a.ResponseBody,
                a.RetryAfterSeconds,
            })
            .ToListAsync(ct);

        return Ok(attempts.Select(a => new DeliveryAttemptDto(
            a.AttemptNumber,
            a.Channel,
            a.Destination,
            a.Status,
            a.AttemptedAt,
            a.ResponseCode,
            a.ErrorMessage,
            // Only a FAILED attempt has a failure cause. A success has nothing to recover from, and
            // an in-flight 'dispatching' / parked 'unconfirmed' row has no supplier verdict at all —
            // asking the table about either would get the confident answer "unreachable" for a
            // response that never happened.
            FailureCause: a.Status == DeliveryAttempt.StatusFailed
                ? SupplierResponseClassification.CauseFor(a.ResponseCode, a.RejectionReason, a.ResponseBody)
                : null,
            RetryAfterSeconds: a.RetryAfterSeconds))
            .ToList());
    }
}

/// <summary>DTO returned by <see cref="DeliveriesController.GetDeliveryAttempts"/>.</summary>
/// <param name="FailureCause">
/// Why this attempt failed, as a stable <see cref="SupplierFailureCause"/> slug — what a client keys
/// its per-attempt recovery control off, so it never has to parse
/// <paramref name="ErrorMessage"/>. Null for any attempt that did not fail (a success, an in-flight
/// dispatch, a park).
/// </param>
/// <param name="RetryAfterSeconds">
/// The wait the supplier asked for on this attempt (their <c>Retry-After</c>), bounded to what the
/// retry queue will actually honour. Null when they asked for none — never read it as zero.
/// </param>
public sealed record DeliveryAttemptDto(
    int       AttemptNumber,
    string    Channel,
    string    Destination,
    string    Status,
    DateTime  AttemptedAt,
    int?      ResponseCode,
    string?   ErrorMessage,
    string?   FailureCause = null,
    int?      RetryAfterSeconds = null);
