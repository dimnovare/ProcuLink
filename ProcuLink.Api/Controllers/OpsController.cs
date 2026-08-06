using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
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
    private readonly IAcceptanceGate         _acceptanceGate;

    public OpsController(
        IOpsHealthService      health,
        ICurrentTenantService  tenant,
        IOrderService          orders,
        IBackgroundJobClient   jobs,
        ProcuLinkDbContext     db,
        ILogger<OpsController> logger,
        IAcceptanceGate?       acceptanceGate = null)
    {
        _health = health;
        _tenant = tenant;
        _orders = orders;
        _jobs   = jobs;
        _db     = db;
        _logger = logger;
        // WP-17 — the acceptance gate for the operator escalation below. Optional in the ctor only
        // so the existing positional test constructions stay valid; the field is never null, and the
        // fallback builds the REAL gate from the DbContext this controller already holds. An
        // enforcement dependency that quietly defaults to "off" is the failure being closed here.
        _acceptanceGate = acceptanceGate
            ?? new ProcuLink.Api.Services.AcceptanceGate(
                   db, new ProcuLink.Api.Services.SupplierAcceptanceService(db));
    }

    // ── GET /api/ops/health ───────────────────────────────────────────────────

    /// <summary>
    /// Job-health summary: counts of orders in each problematic state (stuck
    /// parsing/delivering, transform_failed, delivery_failed, delivery_dead_letter,
    /// rejected_by_supplier, failed, delivery_unconfirmed, SLA-breached) plus total open exceptions.
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
            PendingReview:                s.PendingReview,
            PendingRouting:               s.PendingRouting,
            DeliveryHeld:                 s.DeliveryHeld,
            DeliveryUnconfirmed:          s.DeliveryUnconfirmed));
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

    /// <summary>
    /// The statuses <see cref="RequeueDelivery"/> admits, as the refusal sentence lists them —
    /// <c>'delivery_dead_letter' or 'delivery_failed'</c>. Read off
    /// <see cref="OrderStatusMachine.RequeueableFrom"/> rather than retyped, so a status added to
    /// the set is a status the operator is told about. Ordered explicitly: a HashSet's enumeration
    /// order is not part of its contract, and a refusal whose wording shuffles between requests
    /// reads like two different rules.
    /// </summary>
    private static string DescribeAdmittedStatuses() =>
        string.Join(" or ", OrderStatusMachine.RequeueableFrom
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => $"'{s}'"));

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
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequeueDelivery(Guid id, CancellationToken ct)
    {
        var orgId     = _tenant.OrganisationId;
        var getResult = await _orders.GetByIdAsync(orgId, id, ct);
        if (!getResult.IsSuccess)
            return NotFound();

        var order = getResult.Value!;

        // ONE authority for which statuses this escalation admits — OrderStatusMachine, like every
        // other gate on the delivery path. This used to be a hand-written literal pair, with the
        // SAME two statuses spelled out a second time in the refusal below; the message is now built
        // from the set, so what the endpoint names and what it admits cannot drift apart.
        if (!OrderStatusMachine.RequeueableFrom.Contains(order.Status))
            return BadRequest(new
            {
                error = $"Order must be in {DescribeAdmittedStatuses()} status to requeue delivery (current: '{order.Status}')."
            });

        // WP-35: newest DELIVERABLE, not newest. A re-processed preview is the newest artifact by
        // construction and must never be what an escalation puts in front of the supplier.
        var artifact = OutboundArtifactSelection.NewestDeliverable(order.OutboundArtifacts);

        if (artifact is null)
            return BadRequest(new { error = "No outbound artifact found. Transform the order before requeuing delivery." });

        // ── WP-17 — the acceptance gate ────────────────────────────────────────
        // An operator escalation is still a human deciding to send this document to the supplier, so
        // the supplier's blocking rules apply. It is checked HERE, before anything is written: the
        // block below supersedes the attempt cap and rewrites the order's status in one SaveChanges,
        // and a refusal arriving after that would leave the cap reset and the status rewritten for a
        // send that never happens. The way past a genuine refusal is the recorded override
        // (POST /api/orders/{id}/acceptance-gate/override), which is auditable — unlike an ops
        // endpoint that quietly ignored the rules.
        var decision = await _acceptanceGate.EvaluateAsync(orgId, id, ct);
        if (decision is { Blocked: true })
        {
            var refusal = string.IsNullOrWhiteSpace(decision.Reason)
                ? "This order wasn't sent because it doesn't meet what the supplier accepts."
                : decision.Reason!;

            _db.AuditEvents.Add(ProcuLink.Api.Services.OrderServiceShared.BuildAuditEvent(
                orgId, id, AcceptanceGateAudit.BlockedAction, new
                {
                    blockers = decision.Blockers
                        .Select(b => new { code = b.Code, line = b.LineNumber, message = b.Message })
                        .ToList(),
                    stage = "ops-requeue",
                }));
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Ops requeue-delivery refused for order {OrderId} (org {OrgId}): {Count} blocking supplier acceptance rule(s).",
                id, orgId, decision.Blockers.Count);

            return Conflict(new
            {
                error    = refusal,
                blockers = decision.Blockers
                    .Select(b => new { code = b.Code, line = b.LineNumber, message = b.Message })
                    .ToList(),
            });
        }

        // B2 (lost-order): DO NOT pre-flip to 'delivering'. DeliverOrderJob's atomic claim
        // (DispatchArtifactAsync — the one authority on which statuses are claimable) REJECTS a
        // FRESH 'delivering' row (UpdatedAt just stamped to now) via its reclaim-window guard, so
        // the enqueued job would no-op — the order stranded and this escalation silently defeated.
        // Instead:
        //   (1) reset the delivery-attempt cap — the operator's explicit intent is to dispatch PAST
        //       the dead-letter cap; without a reset, a single transient failure of the requeued
        //       dispatch would immediately re-dead-letter and the automatic retry queue would never
        //       re-engage; and
        //   (2) leave the order in the CLAIMABLE 'delivery_failed' idle status so the enqueued
        //       DeliverOrderJob's claim SUCCEEDS and actually dispatches the artifact to the supplier.
        var tracked = await _db.PurchaseOrders
            .FirstOrDefaultAsync(o => o.Id == id && o.OrgId == orgId, ct);
        if (tracked is not null)
        {
            // Reset the cap WITHOUT erasing dispatch evidence: stamp CapSupersededAt on exactly
            // the rows the cap counts (DeliveryAttempt.CountsAgainstCap — terminal, not already
            // superseded). The rows and their dispatch markers (IdempotencyKey / ArtifactSha256)
            // SURVIVE, so the webhook guard's premise "no marker ⇒ never dispatched" stays true
            // through a requeue — the old RemoveRange let a genuinely-sent order present as
            // never-dispatched, and a late supplier rejection was then refused and re-sent by the
            // stranded-failed sweep (the refused-rejection re-send P1).
            //
            // The predicate already excludes the in-flight 'dispatching' row, which must ALSO stay
            // unstamped: it is the only evidence a send was started and never finalised, the next
            // dispatch re-adopts it (same idempotency key) to PARK rather than re-send on a channel
            // that cannot de-duplicate, and it was never part of the spent budget anyway.
            //
            // Tracked mutation, not ExecuteUpdate: the stamp must commit ATOMICALLY with the status
            // write + audit row below (ExecuteUpdate commits immediately, outside this SaveChanges),
            // and the row set is bounded by the attempt cap.
            //
            // Known load→save window (same window the old RemoveRange had): a backoff retry firing
            // concurrently — delivery_failed is claimable — could commit a new terminal row between
            // this read and the SaveChanges. That row escapes the stamp and counts against the
            // FRESH budget, so the reset starts at 1 instead of 0. Bounded, evidence-preserving,
            // and no worse than the delete it replaced (a row added mid-delete survived that too);
            // not worth a lock on a manual ops escalation.
            var priorAttempts = await _db.DeliveryAttempts
                .Where(a => a.OrderId == id && a.OrgId == orgId)
                .Where(DeliveryAttempt.CountsAgainstCap)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            foreach (var attempt in priorAttempts)
                attempt.CapSupersededAt = now;

            var fromStatus = tracked.Status;
            tracked.Status    = OrderStatusConstants.DeliveryFailed; // claimable, send-ready idle state
            tracked.UpdatedAt = now;

            _db.AuditEvents.Add(new AuditEvent
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                UserId     = null,
                EntityType = "Order",
                EntityId   = id,
                Action     = "DeliveryRequeuedByOperator",
                Payload    = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    reason                  = "OpsRequeueDelivery",
                    fromStatus,
                    toStatus                = OrderStatusConstants.DeliveryFailed,
                    priorAttemptsSuperseded = priorAttempts.Count,
                    // No forensic row-copy here any more: the attempt rows themselves are the
                    // record now — they survive this requeue. That holds because AuditEventDays
                    // and DeliveryAttemptDays both default to 180 and are pruned by the same
                    // disabled-by-default sweep (DataRetentionOptions.cs:18,:21,:30), so the copy
                    // would not outlive the rows. If DeliveryAttemptDays is ever configured BELOW
                    // AuditEventDays, the rows die first and this decision must be revisited —
                    // the copy would then have been the record. The audit EVENT stays: "an
                    // operator requeued this" is not recoverable from the rows.
                    artifactId              = artifact.Id,
                    requeuedAt              = now,
                    detail                  = "Operator escalation: attempt cap reset (prior attempts superseded via CapSupersededAt — rows and dispatch evidence retained) and order left claimable so the re-enqueued delivery reaches the supplier past the dead-letter cap.",
                })),
                CreatedAt = now,
            });

            // Commit the reset + claimable status BEFORE enqueuing, so the DeliverOrderJob can't run
            // and read a stale status / attempt count (mirrors ReleaseBillingHeldOrdersAsync).
            await _db.SaveChangesAsync(ct);
        }

        DeliverOrderJob.EnqueueRedeliver(_jobs, id, orgId, artifact.Id);

        _logger.LogWarning(
            "Ops requeue-delivery: order {OrderId} (org {OrgId}) re-enqueued from '{FromStatus}' (attempt cap reset), artifact {ArtifactId}.",
            id, orgId, order.Status, artifact.Id);

        return Accepted(new { status = OrderStatusConstants.DeliveryFailed, requeued = true });
    }
}
