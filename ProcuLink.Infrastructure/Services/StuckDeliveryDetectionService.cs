using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class StuckDeliveryDetectionService : IStuckDeliveryDetectionService
{
    /// <summary>
    /// How many times a single order may be re-driven from a stuck <c>delivering</c> state
    /// before we give up and dead-letter it. A transient Worker restart / network blip mid-dispatch
    /// is recoverable; an order that keeps stranding in <c>delivering</c> after this many requeues
    /// is genuinely stuck, so we stop looping it and move it to <c>delivery_dead_letter</c> with a
    /// clear reason. Mirrors <c>StuckOrderDetectionService.MaxRequeues</c>.
    ///
    /// <para>Counted against <c>PurchaseOrderEntity.DeliveryRequeueCount</c> — the DELIVERY-phase
    /// budget, DISTINCT from the parse/transform <c>RequeueCount</c> that
    /// <c>StuckOrderDetectionService</c> spends. An order that already exhausted its parse/transform
    /// requeues therefore reaches delivery with a FULL delivery budget and is never prematurely
    /// dead-lettered on its first delivery stall.</para>
    /// </summary>
    private const int MaxRequeues = 2;

    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<StuckDeliveryDetectionService> _logger;

    // Optional re-drive seam. The Worker that runs this sweep registers the Hangfire-backed
    // adapter; when absent (e.g. a process without the delivery-retry job) we still reset
    // UpdatedAt + count the requeue so the order leaves the stuck window — never silently
    // turning a transient stall into a permanent failure on the first blip, and never leaving
    // it pinned in the stuck window where the next sweep would double-act on it.
    private readonly IRetryDeliveryEnqueuer? _retryEnqueuer;

    public StuckDeliveryDetectionService(
        ProcuLinkDbContext db,
        ILogger<StuckDeliveryDetectionService> logger,
        IRetryDeliveryEnqueuer? retryEnqueuer = null)
    {
        _db = db;
        _logger = logger;
        _retryEnqueuer = retryEnqueuer;
    }

    public async Task<int> RunAsync(TimeSpan stuckThreshold, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - stuckThreshold;

        // Cross-tenant maintenance sweep (mirrors StuckOrderDetectionService / DeliverySlaService).
        // Tenant isolation is preserved because each order, requeue, and audit event carries
        // that order's own OrgId.
        var stuck = await _db.PurchaseOrders
            .Where(o => o.Status == OrderStatusConstants.Delivering && o.UpdatedAt < cutoff)
            .ToListAsync(ct);

        if (stuck.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var actedOn = 0;

        // Collect retry re-drives to fire AFTER the status/timestamp changes are committed —
        // an enqueued retry job could otherwise start before its bumped UpdatedAt is persisted.
        var retryRequeues = new List<(Guid OrderId, Guid OrgId)>();

        foreach (var order in stuck)
        {
            // Idempotency / recovery re-check: this list is a fresh query, but guard explicitly so
            // an order that already left 'delivering' (e.g. the dispatch completed between the
            // query and now) is never double-processed.
            if (order.Status != OrderStatusConstants.Delivering)
                continue;

            var stuckSince = order.UpdatedAt;

            if (order.DeliveryRequeueCount < MaxRequeues)
            {
                // ── Transient stall → re-drive delivery (do NOT fail) ─────────────
                order.DeliveryRequeueCount += 1;
                // Bump UpdatedAt so this order leaves the stuck window: the retry job will move it
                // to a terminal status, and a duplicate sweep before then won't re-act on it.
                order.UpdatedAt = now;

                retryRequeues.Add((order.Id, order.OrgId));

                var requeuePayload = JsonSerializer.Serialize(new
                {
                    reason = "StuckDeliveryRequeued",
                    fromStatus = OrderStatusConstants.Delivering,
                    stuckSince,
                    detectedAt = now,
                    thresholdMinutes = stuckThreshold.TotalMinutes,
                    requeueCount = order.DeliveryRequeueCount,
                    maxRequeues = MaxRequeues,
                    retryReEnqueued = _retryEnqueuer is not null,
                });

                _db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    OrgId = order.OrgId,
                    UserId = null,
                    EntityType = "Order",
                    EntityId = order.Id,
                    Action = "StuckDeliveryRequeued",
                    Payload = JsonDocument.Parse(requeuePayload),
                    CreatedAt = now,
                });

                _logger.LogWarning(
                    "StuckDeliveryDetection: order {OrderId} (org {OrgId}) stuck in 'delivering' since {StuckSince:o} — re-driving delivery (attempt {RequeueCount}/{MaxRequeues}).",
                    order.Id, order.OrgId, stuckSince, order.DeliveryRequeueCount, MaxRequeues);
            }
            else
            {
                // ── Requeue cap exceeded → dead-letter (genuinely stuck) ──────────
                // Guaranteed delivering→terminal transition. Clear the SLA timer so the SLA
                // sweep can never flag an order whose delivery is now terminal.
                order.Status = OrderStatusConstants.DeliveryDeadLetter;
                order.UpdatedAt = now;
                order.DeliveryDueAt = null;
                order.SlaBreached = false;

                var failPayload = JsonSerializer.Serialize(new
                {
                    reason = "StuckDeliveryDeadLettered",
                    fromStatus = OrderStatusConstants.Delivering,
                    stuckSince,
                    detectedAt = now,
                    thresholdMinutes = stuckThreshold.TotalMinutes,
                    requeueCount = order.DeliveryRequeueCount,
                    maxRequeues = MaxRequeues,
                    deadLettered = true,
                    detail = $"Order re-driven {order.DeliveryRequeueCount} time(s) and kept stranding in 'delivering' — dead-lettered.",
                });

                _db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    OrgId = order.OrgId,
                    UserId = null,
                    EntityType = "Order",
                    EntityId = order.Id,
                    Action = "StuckDeliveryDeadLettered",
                    Payload = JsonDocument.Parse(failPayload),
                    CreatedAt = now,
                });

                _logger.LogWarning(
                    "StuckDeliveryDetection: order {OrderId} (org {OrgId}) stuck in 'delivering' since {StuckSince:o} after {RequeueCount} re-drive(s) — dead-lettering.",
                    order.Id, order.OrgId, stuckSince, order.DeliveryRequeueCount);
            }

            actedOn++;
        }

        await _db.SaveChangesAsync(ct);

        // Fire retry re-drives only after the bumped UpdatedAt rows are committed.
        if (retryRequeues.Count > 0)
        {
            if (_retryEnqueuer is null)
            {
                _logger.LogWarning(
                    "StuckDeliveryDetection: {Count} delivery re-drive(s) bumped out of the stuck window but no IRetryDeliveryEnqueuer is registered in this process — orders will be re-driven once an enqueuer is available.",
                    retryRequeues.Count);
            }
            else
            {
                foreach (var (orderId, orgId) in retryRequeues)
                    await _retryEnqueuer.EnqueueAsync(orderId, orgId, ct);
            }
        }

        _logger.LogWarning("StuckDeliveryDetection: acted on {Count} stuck delivering order(s) (re-driven or dead-lettered).", actedOn);
        return actedOn;
    }
}
