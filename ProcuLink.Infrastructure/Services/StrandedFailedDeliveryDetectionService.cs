using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class StrandedFailedDeliveryDetectionService : IStrandedFailedDeliveryDetectionService
{
    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<StrandedFailedDeliveryDetectionService> _logger;
    private readonly DeliveryReliabilityOptions _reliability;

    // Optional re-drive seam (mirrors StrandedReadyOrderDetectionService's optional enqueuer). The
    // Worker that runs this sweep registers the Hangfire-backed RetryDeliveryJob adapter; when absent
    // we still bump UpdatedAt + write the audit so the order leaves the aged window and is not
    // re-audited on every sweep — never silently pinning it where the next sweep re-acts on it.
    private readonly IRetryDeliveryEnqueuer? _retryEnqueuer;

    public StrandedFailedDeliveryDetectionService(
        ProcuLinkDbContext db,
        ILogger<StrandedFailedDeliveryDetectionService> logger,
        DeliveryReliabilityOptions? reliability = null,
        IRetryDeliveryEnqueuer? retryEnqueuer = null)
    {
        _db = db;
        _logger = logger;
        _reliability = reliability ?? new DeliveryReliabilityOptions();
        _retryEnqueuer = retryEnqueuer;
    }

    public async Task<int> RunAsync(TimeSpan agedThreshold, CancellationToken ct, int maxBatch = IStrandedFailedDeliveryDetectionService.DefaultMaxBatch)
    {
        var cutoff = DateTime.UtcNow - agedThreshold;
        // Attempt cap must match RetryDeliveryAsync's cap exactly so "attempts remaining" agrees with
        // the retry path (an order at the cap is already dead-lettered, never re-driven here).
        var maxAttempts = _reliability.MaxAttempts > 0 ? _reliability.MaxAttempts : 3;

        // Cross-tenant maintenance sweep (mirrors StrandedReadyOrderDetectionService). Tenant isolation
        // is preserved because each order, enqueue, and audit event carries that order's own OrgId.
        //
        // Every eligibility filter is pushed INTO the query so the database excludes the orders that
        // legitimately rest in delivery_failed (a supplier rejection lands in rejected_by_supplier, a
        // dead order in delivery_dead_letter — neither matches this status filter). We recover ONLY:
        //   • delivery_failed orders aged past the threshold. The threshold MUST exceed the maximum
        //     retry backoff (the recurring job sets 3h vs the {30,60,120}-min schedule), so a
        //     legitimately-scheduled retry — which fires within minutes and moves the order out of
        //     delivery_failed — is never raced; only a genuinely LOST reschedule ages this long.
        //   • routed orders (SupplierId set — an unrouted order has no delivery config to retry).
        //   • with delivery attempts still REMAINING (terminal attempts below the cap; an in-flight
        //     'dispatching' row is not a completed attempt, matching RetryDeliveryAsync's count).
        //   • with NO in-flight 'dispatching' attempt (a send is mid-flight → not stranded).
        // Bounded by maxBatch (oldest strand first) so one sweep can never load an unbounded backlog.
        var candidates = await _db.PurchaseOrders
            .Where(o => o.Status == OrderStatusConstants.DeliveryFailed && o.UpdatedAt < cutoff)
            .Where(o => o.SupplierId != null)
            .Where(o => _db.DeliveryAttempts.Count(a => a.OrderId == o.Id && a.OrgId == o.OrgId
                        && a.Status != DeliveryAttempt.StatusDispatching) < maxAttempts)
            .Where(o => !_db.DeliveryAttempts.Any(a => a.OrderId == o.Id && a.OrgId == o.OrgId
                        && a.Status == DeliveryAttempt.StatusDispatching))
            .OrderBy(o => o.UpdatedAt)
            .Take(maxBatch)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var actedOn = 0;

        // Collect re-drives to fire AFTER the UpdatedAt bump + audit are committed — an enqueued retry
        // must not run and read a stale row before this sweep's write lands. (delivery_failed is
        // claimable regardless of UpdatedAt, so the bump can never block the enqueued retry's own claim.)
        var toEnqueue = new List<(Guid OrderId, Guid OrgId)>();

        foreach (var order in candidates)
        {
            var stalledSince = order.UpdatedAt;

            // Bump UpdatedAt so the order leaves the aged window: RetryDeliveryAsync will move it to a
            // delivering/terminal status, and a duplicate sweep before then won't re-act on it. (Even
            // a duplicate enqueue is harmless — RetryDeliveryAsync's atomic claim + per-order mutex +
            // attempt-cap prevent any double-send.)
            order.UpdatedAt = now;
            toEnqueue.Add((order.Id, order.OrgId));

            var payload = JsonSerializer.Serialize(new
            {
                reason = "StrandedFailedDeliveryRecovered",
                fromStatus = OrderStatusConstants.DeliveryFailed,
                stalledSince,
                detectedAt = now,
                thresholdMinutes = agedThreshold.TotalMinutes,
                reEnqueued = _retryEnqueuer is not null,
                detail = "Order left in delivery_failed with attempts remaining and no scheduled/in-flight retry (the next-attempt schedule was lost between the failed-attempt write and BackgroundJob.Schedule) — re-driving delivery.",
            });

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                OrgId = order.OrgId,
                UserId = null,
                EntityType = "Order",
                EntityId = order.Id,
                Action = "StrandedFailedDeliveryRecovered",
                Payload = JsonDocument.Parse(payload),
                CreatedAt = now,
            });

            _logger.LogWarning(
                "StrandedFailedDeliveryDetection: order {OrderId} (org {OrgId}) stranded in 'delivery_failed' since {StalledSince:o} with attempts remaining and no scheduled/in-flight retry — re-enqueueing delivery.",
                order.Id, order.OrgId, stalledSince);

            actedOn++;
        }

        await _db.SaveChangesAsync(ct);

        // Fire re-drives only after the bumped UpdatedAt + audit rows are committed.
        if (toEnqueue.Count > 0)
        {
            if (_retryEnqueuer is null)
            {
                _logger.LogWarning(
                    "StrandedFailedDeliveryDetection: {Count} stranded delivery_failed order(s) bumped out of the aged window but no IRetryDeliveryEnqueuer is registered in this process — they will be re-driven once an enqueuer is available (or via an operator Retry).",
                    toEnqueue.Count);
            }
            else
            {
                foreach (var (orderId, orgId) in toEnqueue)
                    await _retryEnqueuer.EnqueueAsync(orderId, orgId, ct);
            }
        }

        _logger.LogWarning("StrandedFailedDeliveryDetection: recovered {Count} stranded delivery_failed order(s).", actedOn);
        return actedOn;
    }
}
