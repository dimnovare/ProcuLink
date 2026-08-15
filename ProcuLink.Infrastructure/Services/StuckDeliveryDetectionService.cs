using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
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

    // Optional notification seam, same shape and same reason as _retryEnqueuer above. This class is
    // the SECOND dead-letter writer in the system (DeliveryService.DeadLetterAsync is the first),
    // and the customer-facing order.dead_lettered event has to fire from both — an order that
    // stranded in 'delivering' past its requeue budget is just as undeliverable as one that spent
    // its retry budget, and a notification wired to only one of the two paths is silence that looks
    // like coverage. Optional rather than required so the existing positional test constructors keep
    // compiling; both live hosts register IIntegrationTriggerService, so production always notifies.
    private readonly IIntegrationTriggerService? _integrationTrigger;

    // Optional exception-reconcile seam, same shape and same reason as the two seams above. The
    // customer-facing webhook is only half of "tell someone": the IN-APP surface is the exception
    // list, and 'dead_letter' is the single 'critical' severity OrderExceptionService raises.
    // DeliveryService.DeadLetterAsync reconciles, so ITS dead-letters open that row; the orders
    // dead-lettered here reached the same terminal, undeliverable status showing no problem at all
    // in GET /api/exceptions or the inbox. Optional rather than required so the existing positional
    // test constructors keep compiling; both live hosts register IOrderExceptionService, so
    // production always reconciles.
    private readonly IOrderExceptionService? _exceptions;

    public StuckDeliveryDetectionService(
        ProcuLinkDbContext db,
        ILogger<StuckDeliveryDetectionService> logger,
        IRetryDeliveryEnqueuer? retryEnqueuer = null,
        IIntegrationTriggerService? integrationTrigger = null,
        IOrderExceptionService? exceptions = null)
    {
        _db = db;
        _logger = logger;
        _retryEnqueuer = retryEnqueuer;
        _integrationTrigger = integrationTrigger;
        _exceptions = exceptions;
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

        // Same deal for dead-letter notifications: collected here, fired only after the terminal
        // status is committed (see the emit block at the end of this method for why that ordering
        // is what makes the notification at-most-once).
        var deadLettered = new List<(Guid OrderId, Guid OrgId, DateTime At, int RequeueCount, string Detail)>();

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
                // Bump UpdatedAt so this order leaves the stuck window: a duplicate sweep before the
                // re-drive lands won't re-act on it.
                //
                // The bump ALSO makes this row un-claimable for the reclaim window, so the retry we
                // enqueue below does NOT deliver it: that retry finds a 'delivering' row that is no
                // longer stale, loses the atomic claim, and returns ClaimLost. Recovery completes one
                // SCHEDULED backoff step later (~30 min), once the row has aged past the window and a
                // retry can actually claim it. That is exactly why ClaimLost must keep rescheduling —
                // this sweep cannot recover an order on its own. Pinned by
                // CrashedHolderRecoveryCompositionPostgresTests.
                //
                // (Handing the row back in an idle status instead would recover in seconds, but an
                // idle status is claimable REGARDLESS of UpdatedAt — so a holder that is merely slow
                // rather than dead would be re-claimed and the PO double-sent. The staleness gate is
                // what makes "abandoned" provable instead of presumed. Left as-is deliberately.)
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

                var stuckDetail =
                    $"Order re-driven {order.DeliveryRequeueCount} time(s) and kept stranding in 'delivering' — dead-lettered.";

                deadLettered.Add((order.Id, order.OrgId, now, order.DeliveryRequeueCount, stuckDetail));

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
                    detail = stuckDetail,
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

        // ── Raise the in-app problem for the stranded order ───────────────────────
        // Ordered AFTER the commit for the same reason as the fan-out below: Reconcile derives the
        // problem from the order's CURRENT status, so it must read the committed
        // 'delivery_dead_letter' — running it before the save would derive 'delivering', which maps
        // to no problem at all, and the row would silently not be opened.
        //
        // Reconcile is idempotent by suppression (it will not add a second open row for a code that
        // already has one), so re-entry is safe. Best-effort and swallowed per order, mirroring
        // DeliveryService.SafeReconcileExceptionsAsync: exception generation is operational
        // observability data and must never undo a committed terminal status, and because this
        // sweep is cross-tenant, letting one org's reconcile throw would abandon every remaining
        // org's row — the same silence this block exists to remove.
        if (deadLettered.Count > 0)
        {
            if (_exceptions is null)
            {
                _logger.LogWarning(
                    "StuckDeliveryDetection: {Count} order(s) dead-lettered but no IOrderExceptionService is registered in this process — no dead_letter exceptions were opened for them.",
                    deadLettered.Count);
            }
            else
            {
                foreach (var (orderId, orgId, _, _, _) in deadLettered)
                {
                    try
                    {
                        await _exceptions.ReconcileAsync(orgId, orderId, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "StuckDeliveryDetection: failed to reconcile exceptions for dead-lettered order {OrderId} (org {OrgId}) — the order IS dead-lettered; only the exception row was lost.",
                            orderId, orgId);
                    }
                }
            }
        }

        // ── Tell the customer the stranded order is not coming ────────────────────
        // Fired only after the terminal status is committed, which is what makes this at-most-once:
        // the sweep selects on `Status == Delivering`, so an order that is already
        // 'delivery_dead_letter' can never be picked up by a later sweep and notified twice. Order
        // the enqueue BEFORE the commit and a crash in the gap would leave the order still
        // 'delivering', re-selected next sweep, and notified again — duplicates being the one
        // outcome worse than none here. The residual failure mode is a lost notification, chosen
        // deliberately.
        //
        // Each event is enqueued independently and a failure is swallowed per order: the sweep is a
        // cross-tenant maintenance pass, so letting one org's fan-out throw would abandon the
        // remaining orgs' notifications — the same silence this block exists to remove.
        if (deadLettered.Count > 0)
        {
            if (_integrationTrigger is null)
            {
                _logger.LogWarning(
                    "StuckDeliveryDetection: {Count} order(s) dead-lettered but no IIntegrationTriggerService is registered in this process — no order.dead_lettered events were emitted for them.",
                    deadLettered.Count);
            }
            else
            {
                foreach (var (orderId, orgId, at, requeueCount, detail) in deadLettered)
                {
                    try
                    {
                        await _integrationTrigger.EnqueueAsync(
                            orgId,
                            IntegrationEventTypes.OrderDeadLettered,
                            new
                            {
                                order_id = orderId,
                                dead_lettered_at = at,
                                attempt_count = requeueCount,
                                error = detail,
                            },
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "StuckDeliveryDetection: failed to emit order.dead_lettered for order {OrderId} (org {OrgId}) — the order IS dead-lettered; only the notification was lost.",
                            orderId, orgId);
                    }
                }
            }
        }

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
