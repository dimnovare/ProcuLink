using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class StuckOrderDetectionService : IStuckOrderDetectionService
{
    // Statuses an order should never sit in for long — they are transient
    // "a Hangfire job is working on this" states.
    private static readonly string[] TransientStatuses =
    {
        OrderStatusConstants.Parsing,
        OrderStatusConstants.Transforming,
    };

    /// <summary>
    /// How many times a single order may be re-enqueued before we give up and
    /// dead-letter it. A transient Worker restart mid-job is recoverable; an order
    /// that keeps stalling after this many requeues is genuinely failing, so we stop
    /// looping it and mark it failed with a clear reason.
    /// </summary>
    private const int MaxRequeues = 2;

    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<StuckOrderDetectionService> _logger;

    // Optional: the Api process registers HangfireParseJobEnqueuer; the Worker that runs
    // the recurring sweep may not have it registered. When present, stuck 'parsing' orders
    // are re-driven by enqueuing a fresh parse job; when absent we still reset + count the
    // attempt (so a process that DOES have the enqueuer, or a later run, can pick it up) —
    // never silently turning a transient stall into a permanent failure on the first blip.
    private readonly IParseJobEnqueuer? _parseEnqueuer;

    public StuckOrderDetectionService(
        ProcuLinkDbContext db,
        ILogger<StuckOrderDetectionService> logger,
        IParseJobEnqueuer? parseEnqueuer = null)
    {
        _db = db;
        _logger = logger;
        _parseEnqueuer = parseEnqueuer;
    }

    public async Task<int> RunAsync(TimeSpan stuckThreshold, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - stuckThreshold;

        // Cross-tenant maintenance sweep. This mirrors EmailPollingJob, which also
        // reads across all organisations. Tenant isolation is preserved because each
        // order, requeue, and audit event carries that order's own OrgId.
        var stuck = await _db.PurchaseOrders
            .Where(o => TransientStatuses.Contains(o.Status) && o.UpdatedAt < cutoff)
            .ToListAsync(ct);

        if (stuck.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var actedOn = 0;

        // Collect parse-job requeues to fire AFTER the status changes are committed —
        // an enqueued job could otherwise start before its 'pending_parse' status is
        // persisted and exit early on a stale 'parsing' read.
        var parseRequeues = new List<(Guid OrderId, Guid OrgId)>();

        foreach (var order in stuck)
        {
            // Idempotency / recovery re-check: this list is a fresh query, but guard
            // explicitly so an order that already left the transient set (recovered
            // between the query and now) is never double-processed.
            if (!TransientStatuses.Contains(order.Status))
                continue;

            var fromStatus = order.Status;
            var stuckSince = order.UpdatedAt;

            if (order.RequeueCount < MaxRequeues)
            {
                // ── Transient stall → requeue (do NOT fail) ───────────────────────
                order.RequeueCount += 1;
                order.UpdatedAt = now;

                if (fromStatus == OrderStatusConstants.Parsing)
                {
                    // Reset to the pre-parse state and re-drive the parse job.
                    order.Status = OrderStatusConstants.PendingParse;
                    parseRequeues.Add((order.Id, order.OrgId));
                }
                else // Transforming
                {
                    // The order is already fully resolved; reset to its pre-transform
                    // 'ready' state so the normal transform path can re-drive it. (There
                    // is no cross-project transform-enqueue seam from Infrastructure, and
                    // the original output format is not stored on the order, so we recover
                    // to the resolved state rather than guessing a format.)
                    order.Status = OrderStatusConstants.Ready;
                }

                var requeuePayload = JsonSerializer.Serialize(new
                {
                    reason = "StuckRequeued",
                    fromStatus,
                    toStatus = order.Status,
                    stuckSince,
                    detectedAt = now,
                    thresholdMinutes = stuckThreshold.TotalMinutes,
                    requeueCount = order.RequeueCount,
                    maxRequeues = MaxRequeues,
                    parseReEnqueued = fromStatus == OrderStatusConstants.Parsing && _parseEnqueuer is not null,
                });

                _db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    OrgId = order.OrgId,
                    UserId = null,
                    EntityType = "Order",
                    EntityId = order.Id,
                    Action = "StuckRequeued",
                    Payload = JsonDocument.Parse(requeuePayload),
                    CreatedAt = now,
                });

                _logger.LogWarning(
                    "StuckOrderDetection: order {OrderId} (org {OrgId}) stuck in '{FromStatus}' since {StuckSince:o} — requeueing (attempt {RequeueCount}/{MaxRequeues}, new status '{ToStatus}').",
                    order.Id, order.OrgId, fromStatus, stuckSince, order.RequeueCount, MaxRequeues, order.Status);
            }
            else
            {
                // ── Requeue cap exceeded → dead-letter (genuinely failed) ─────────
                order.Status = OrderStatusConstants.Failed;
                order.UpdatedAt = now;

                var failPayload = JsonSerializer.Serialize(new
                {
                    reason = "StuckTimeout",
                    fromStatus,
                    stuckSince,
                    detectedAt = now,
                    thresholdMinutes = stuckThreshold.TotalMinutes,
                    requeueCount = order.RequeueCount,
                    maxRequeues = MaxRequeues,
                    deadLettered = true,
                    detail = $"Order re-enqueued {order.RequeueCount} time(s) and kept stalling in a transient status — dead-lettered as failed.",
                });

                _db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    OrgId = order.OrgId,
                    UserId = null,
                    EntityType = "Order",
                    EntityId = order.Id,
                    Action = "StuckTimeout",
                    Payload = JsonDocument.Parse(failPayload),
                    CreatedAt = now,
                });

                _logger.LogWarning(
                    "StuckOrderDetection: order {OrderId} (org {OrgId}) stuck in '{FromStatus}' since {StuckSince:o} after {RequeueCount} requeue(s) — dead-lettering as failed.",
                    order.Id, order.OrgId, fromStatus, stuckSince, order.RequeueCount);
            }

            actedOn++;
        }

        await _db.SaveChangesAsync(ct);

        // Fire parse requeues only after the 'pending_parse' resets are committed.
        if (parseRequeues.Count > 0)
        {
            if (_parseEnqueuer is null)
            {
                _logger.LogWarning(
                    "StuckOrderDetection: {Count} parse requeue(s) reset to '{PendingParse}' but no IParseJobEnqueuer is registered in this process — orders will be re-driven once an enqueuer is available.",
                    parseRequeues.Count, OrderStatusConstants.PendingParse);
            }
            else
            {
                foreach (var (orderId, orgId) in parseRequeues)
                    await _parseEnqueuer.EnqueueAsync(orderId, orgId, ct);
            }
        }

        _logger.LogWarning("StuckOrderDetection: acted on {Count} stuck order(s) (requeued or dead-lettered).", actedOn);
        return actedOn;
    }
}
