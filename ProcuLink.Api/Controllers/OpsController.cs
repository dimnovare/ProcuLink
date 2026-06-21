using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Operator job-health surface. Aggregates the problematic order states (stuck /
/// failed / dead-letter / rejected) and open exceptions that are otherwise
/// scattered across the pipeline, lists the dead-letter queue, and exposes a
/// single requeue action for dead-lettered orders (which the existing
/// <c>/api/orders/{id}/retry-delivery</c> endpoint deliberately refuses).
/// All routes are org-scoped via <see cref="ICurrentTenantService"/>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/ops")]
public sealed class OpsController : ControllerBase
{
    private readonly IOpsHealthService       _health;
    private readonly ICurrentTenantService   _tenant;
    private readonly IOrderService           _orders;
    private readonly IBackgroundJobClient    _jobs;
    private readonly ProcuLinkDbContext      _db;
    private readonly ILogger<OpsController>  _logger;

    public OpsController(
        IOpsHealthService      health,
        ICurrentTenantService  tenant,
        IOrderService          orders,
        IBackgroundJobClient   jobs,
        ProcuLinkDbContext     db,
        ILogger<OpsController> logger)
    {
        _health = health;
        _tenant = tenant;
        _orders = orders;
        _jobs   = jobs;
        _db     = db;
        _logger = logger;
    }

    // ── GET /api/ops/health ───────────────────────────────────────────────────

    /// <summary>
    /// Job-health summary: counts of orders in each problematic state (stuck
    /// parsing/delivering, transform_failed, delivery_failed, delivery_dead_letter,
    /// rejected_by_supplier, failed, SLA-breached) plus total open exceptions.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(OpsHealthDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHealth(CancellationToken ct)
    {
        var s = await _health.GetHealthAsync(_tenant.OrganisationId, ct);
        return Ok(new OpsHealthDto(
            ParsingStuck:                 s.ParsingStuck,
            DeliveringStuck:              s.DeliveringStuck,
            TransformFailed:              s.TransformFailed,
            DeliveryFailed:               s.DeliveryFailed,
            DeliveryDeadLetter:           s.DeliveryDeadLetter,
            RejectedBySupplier:           s.RejectedBySupplier,
            Failed:                       s.Failed,
            SlaBreached:                  s.SlaBreached,
            OpenExceptions:               s.OpenExceptions,
            StuckThresholdMinutes:        s.StuckThresholdMinutes,
            TotalProblemOrders:           s.TotalProblemOrders,
            ActiveWorkers:                s.ActiveWorkers,
            LastWorkerHeartbeatUtc:       s.LastWorkerHeartbeatUtc,
            SecondsSinceWorkerHeartbeat:  s.SecondsSinceWorkerHeartbeat,
            WorkerHealthy:                s.WorkerHealthy,
            PendingReview:                s.PendingReview));
    }

    // ── GET /api/ops/dead-letter ──────────────────────────────────────────────

    /// <summary>
    /// Orders currently in <c>delivery_dead_letter</c> (and, with
    /// <c>?includeFailed=true</c>, <c>delivery_failed</c>), newest first, each with
    /// its latest delivery-attempt error, response code, and timestamps.
    /// </summary>
    [HttpGet("dead-letter")]
    [ProducesResponseType(typeof(IReadOnlyList<DeadLetterOrderDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDeadLetter(
        [FromQuery] bool includeFailed = false,
        CancellationToken ct = default)
    {
        var rows = await _health.ListDeadLetterAsync(_tenant.OrganisationId, includeFailed, ct);
        return Ok(rows.Select(r => new DeadLetterOrderDto(
            r.OrderId, r.PoNumber, r.SupplierId, r.SupplierName, r.Status,
            r.DeliveryAttempts, r.LastError, r.LastResponseCode,
            r.LastAttemptAt, r.CreatedAt, r.UpdatedAt)));
    }

    // ── POST /api/ops/orders/{id}/requeue-delivery ────────────────────────────

    /// <summary>
    /// Operator escalation: re-enqueue delivery for an order whose automatic
    /// retries are exhausted (<c>delivery_dead_letter</c>) or that is in
    /// <c>delivery_failed</c>. This is the path the existing
    /// <c>/api/orders/{id}/retry-delivery</c> endpoint deliberately rejects for
    /// dead-lettered orders. Flips the order back to <c>delivering</c> and forces a
    /// fresh dispatch of the latest artifact (bypasses the AutoDeliver flag).
    /// </summary>
    [HttpPost("orders/{id:guid}/requeue-delivery")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequeueDelivery(Guid id, CancellationToken ct)
    {
        var orgId     = _tenant.OrganisationId;
        var getResult = await _orders.GetByIdAsync(orgId, id, ct);
        if (!getResult.IsSuccess)
            return NotFound();

        var order = getResult.Value!;

        if (order.Status is not (OrderStatusConstants.DeliveryDeadLetter or OrderStatusConstants.DeliveryFailed))
            return BadRequest(new
            {
                error = $"Order must be in '{OrderStatusConstants.DeliveryDeadLetter}' or '{OrderStatusConstants.DeliveryFailed}' status to requeue delivery (current: '{order.Status}')."
            });

        var artifact = order.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        if (artifact is null)
            return BadRequest(new { error = "No outbound artifact found. Transform the order before requeuing delivery." });

        // Optimistic status flip so the operator view reflects the requeue immediately.
        var tracked = await _db.PurchaseOrders
            .FirstOrDefaultAsync(o => o.Id == id && o.OrgId == orgId, ct);
        if (tracked is not null)
        {
            tracked.Status    = OrderStatusConstants.Delivering;
            tracked.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        DeliverOrderJob.EnqueueRedeliver(_jobs, id, orgId, artifact.Id);

        _logger.LogWarning(
            "Ops requeue-delivery: order {OrderId} (org {OrgId}) re-enqueued from '{FromStatus}', artifact {ArtifactId}.",
            id, orgId, order.Status, artifact.Id);

        return Accepted(new { status = OrderStatusConstants.Delivering });
    }
}
