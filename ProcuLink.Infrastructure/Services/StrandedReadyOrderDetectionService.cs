using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class StrandedReadyOrderDetectionService : IStrandedReadyOrderDetectionService
{
    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<StrandedReadyOrderDetectionService> _logger;

    // Optional re-drive seam. The Worker that runs this sweep registers the Hangfire-backed adapter;
    // when absent (a process without DeliverOrderJob) we still bump UpdatedAt + write the audit so the
    // order leaves the aged window and is not re-audited on every sweep. Mirrors
    // StuckDeliveryDetectionService's optional IRetryDeliveryEnqueuer pattern.
    private readonly IDeliveryDispatchEnqueuer? _enqueuer;

    public StrandedReadyOrderDetectionService(
        ProcuLinkDbContext db,
        ILogger<StrandedReadyOrderDetectionService> logger,
        IDeliveryDispatchEnqueuer? enqueuer = null)
    {
        _db = db;
        _logger = logger;
        _enqueuer = enqueuer;
    }

    public async Task<int> RunAsync(TimeSpan agedThreshold, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - agedThreshold;

        // Cross-tenant maintenance sweep (mirrors StuckDeliveryDetectionService). Tenant isolation is
        // preserved because each order, enqueue, and audit event carries that order's own OrgId. Load
        // the aged ready_to_deliver candidates with a simple, provider-portable top query; the
        // per-order eligibility checks below run as separate queries (no correlated subqueries) so the
        // logic is identical on InMemory and Postgres.
        var candidates = await _db.PurchaseOrders
            .Where(o => o.Status == OrderStatusConstants.ReadyToDeliver && o.UpdatedAt < cutoff)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var actedOn = 0;

        // Collect enqueues to fire AFTER the UpdatedAt bump + audit are committed — an enqueued job
        // must not run and read a stale row before this sweep's write lands.
        var toEnqueue = new List<(Guid OrderId, Guid OrgId, Guid ArtifactId)>();

        foreach (var order in candidates)
        {
            // Idempotency / recovery re-check: guard explicitly so an order that already left
            // ready_to_deliver (delivered between the query and now) is never re-driven.
            if (order.Status != OrderStatusConstants.ReadyToDeliver)
                continue;

            // Unrouted orders have no delivery config — they can't be stranded auto-deliveries.
            if (order.SupplierId is null)
                continue;

            // Recover ONLY orders configured to AUTO-deliver. A manual order (AutoDeliver=false, or no
            // config) legitimately rests in ready_to_deliver awaiting a manual send — force-dispatching
            // it would be a bug, worse than leaving it. A lost enqueue only strands an order that was
            // SUPPOSED to auto-dispatch, which is exactly this set.
            var autoDeliver = await _db.SupplierDeliveryConfigs
                .Where(c => c.OrgId == order.OrgId && c.SupplierId == order.SupplierId)
                .Select(c => (bool?)c.AutoDeliver)
                .FirstOrDefaultAsync(ct);
            if (autoDeliver != true)
                continue;

            // The exact "delivery was never dispatched" signature: no delivery attempt yet. Any prior
            // attempt means delivery already ran / is running → never re-drive (double-send guard).
            var attempted = await _db.DeliveryAttempts
                .AnyAsync(a => a.OrderId == order.Id && a.OrgId == order.OrgId, ct);
            if (attempted)
                continue;

            // ready_to_deliver is only ever set alongside an artifact, but guard defensively.
            var artifactId = await _db.OutboundArtifacts
                .Where(a => a.OrderId == order.Id && a.OrgId == order.OrgId)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(ct);
            if (artifactId is null)
                continue;

            // Bump UpdatedAt so the order leaves the aged window: DeliverOrderJob will move it to a
            // delivering/terminal status, and a duplicate sweep before then won't re-act on it. (Even a
            // duplicate enqueue is harmless — DeliverOrderJob's atomic claim + per-order mutex prevent
            // any double-send.) ready_to_deliver is claimable regardless of UpdatedAt, so the bump can
            // never block the enqueued job's own claim.
            order.UpdatedAt = now;
            toEnqueue.Add((order.Id, order.OrgId, artifactId.Value));

            var payload = JsonSerializer.Serialize(new
            {
                reason = "StrandedReadyDeliveryRecovered",
                fromStatus = OrderStatusConstants.ReadyToDeliver,
                artifactId = artifactId.Value,
                agedSince = order.CreatedAt,
                detectedAt = now,
                thresholdMinutes = agedThreshold.TotalMinutes,
                reEnqueued = _enqueuer is not null,
                detail = "Order left in ready_to_deliver with an artifact and no delivery attempt (delivery enqueue lost between the transform commit and DeliverOrderJob.Enqueue) — re-driving delivery.",
            });

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                OrgId = order.OrgId,
                UserId = null,
                EntityType = "Order",
                EntityId = order.Id,
                Action = "StrandedReadyDeliveryRecovered",
                Payload = JsonDocument.Parse(payload),
                CreatedAt = now,
            });

            _logger.LogWarning(
                "StrandedReadyDeliveryDetection: order {OrderId} (org {OrgId}) stranded in 'ready_to_deliver' with artifact {ArtifactId} and no delivery attempt — re-enqueueing delivery.",
                order.Id, order.OrgId, artifactId.Value);

            actedOn++;
        }

        await _db.SaveChangesAsync(ct);

        // Fire enqueues only after the bumped UpdatedAt + audit rows are committed.
        if (toEnqueue.Count > 0)
        {
            if (_enqueuer is null)
            {
                _logger.LogWarning(
                    "StrandedReadyDeliveryDetection: {Count} stranded order(s) bumped out of the aged window but no IDeliveryDispatchEnqueuer is registered in this process — they will be re-driven once an enqueuer is available (or via an operator action).",
                    toEnqueue.Count);
            }
            else
            {
                foreach (var (orderId, orgId, artifactId) in toEnqueue)
                    await _enqueuer.EnqueueAsync(orderId, orgId, artifactId, ct);
            }
        }

        if (actedOn > 0)
            _logger.LogWarning("StrandedReadyDeliveryDetection: recovered {Count} stranded ready_to_deliver order(s).", actedOn);
        return actedOn;
    }
}
