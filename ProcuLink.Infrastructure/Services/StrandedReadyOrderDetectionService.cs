using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
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

    public async Task<int> RunAsync(TimeSpan agedThreshold, CancellationToken ct, int maxBatch = IStrandedReadyOrderDetectionService.DefaultMaxBatch)
    {
        var cutoff = DateTime.UtcNow - agedThreshold;

        // Cross-tenant maintenance sweep (mirrors StuckDeliveryDetectionService). Tenant isolation is
        // preserved because each order, enqueue, and audit event carries that order's own OrgId.
        //
        // Every cheap eligibility filter is pushed INTO the query so the database — not this process —
        // excludes the orders that legitimately REST in ready_to_deliver. Without this, a
        // manual-delivery-heavy org's parked orders (which sit in ready_to_deliver indefinitely) would
        // be loaded and skipped on EVERY 15-minute sweep, forever — O(all parked orders) rows plus an
        // N+1 config/attempt lookup per order. We recover ONLY:
        //   • routed orders (SupplierId set — an unrouted order has no delivery config),
        //   • configured to AUTO-deliver for THAT order's own supplier (a manual order legitimately
        //     rests here awaiting a manual send; force-dispatching it would be a bug — worse than
        //     leaving it), and
        //   • with no delivery attempt against the order's CURRENT artifact.
        //
        // That last filter compares AttemptedAt to the newest artifact's CreatedAt, and the comparison
        // — not the mere existence of an attempt row — is what makes this a double-send guard. An
        // attempt row does NOT prove a send happened: the four pre-dispatch failure paths in
        // DeliveryService (missing config, no dispatcher, undecryptable credentials, artifact download
        // failed) all persist a terminal row having sent zero bytes. Nor does a row that DID send prove
        // it sent THIS payload: a mapping edit resets a past-ready order (OrderMappingOverrideService
        // .IsPastReady covers delivered/delivery_failed/delivery_dead_letter) and the re-transform
        // commits a NEW artifact, while nothing deletes the old attempt rows — attempt rows are
        // permanent now (the ops requeue supersedes via CapSupersededAt instead of deleting; the
        // only remaining DeliveryAttempts.RemoveRange is DataErasureService's whole-order erasure).
        // So an order that had ever been attempted was permanently unrecoverable here, and its
        // corrected PO silently never sent.
        //
        // DEPENDS ON: OrderTransformService ADDs a new OutboundArtifact row stamped CreatedAt = UtcNow
        // on every transform (it never updates one in place). If a re-transform ever reuses an artifact
        // row, attempts from the PREVIOUS payload would date newer than it and would silently blind
        // this sweep again — the exact defect this filter fixes. The 30-minute aged threshold
        // (StrandedReadyDeliveryDetectionJob) dwarfs any clock skew between the two writers.
        //
        // NO ATTEMPT CAP HERE, DELIBERATELY — and that is a DEPENDENCY, not an observation. This sweep
        // re-drives via DeliverOrderJob, the first-delivery path, which carries no cap of its own (the
        // cap lives in RetryDeliveryAsync and StrandedFailedDeliveryDetectionService). It needs none
        // BECAUSE it can drive at most ONE dispatch per artifact: that dispatch writes a
        // current-artifact attempt row (excluded by the filter below) AND flips the order out of
        // ready_to_deliver (excluded by the status filter) — excluded twice over. A delivery_failed
        // outcome then hands off to StrandedFailedDeliveryDetectionService, which counts ALL terminal
        // rows, so earlier failures still cap the retry ladder.
        // That rests entirely on: NO attempt-writing path leaves an order in ready_to_deliver holding a
        // current-artifact row. That holds by TWO routes, and it is worth knowing which one covers a
        // path before you change it:
        //   • PRE-CLAIM paths write the terminal status themselves — FailMissingConfigAsync, plus
        //     FailBeforeDispatchAsync reached from the no-dispatcher and undecryptable-credentials
        //     checks. These run before the claim, so the order really is still ready_to_deliver and
        //     they must move it.
        //   • POST-CLAIM paths are covered by the ATOMIC CLAIM (DeliveryService.cs:206-210), which has
        //     already flipped the order to 'delivering' before any attempt row can exist — the artifact
        //     download failure, OpenDispatchAttemptAsync, and PersistAttemptAsync. Note that
        //     OpenDispatchAttemptAsync writes an attempt row and NO order status at all: it is
        //     protected by the claim ALONE. So "every attempt-writing path writes a terminal status" is
        //     false — do not rely on it.
        // The live hazard is therefore removing or weakening the claim (or adding a pre-claim path that
        // forgets to flip the status), NOT the source order of the two writes inside a path: within a
        // path the status write and the row Add are both tracked and land in ONE SaveChanges, so they
        // commit atomically and their statement order has no observable effect.
        // Pinned by LostOrderRecoveryPostgresTests.B6_NoAttemptWritingPath_LeavesTheOrderInReadyToDeliver,
        // which drives every path and asserts none ends in ready_to_deliver — it catches a path that
        // fails to move the order, by whichever route was supposed to cover it. If B6 goes red, fix the
        // path or add the cap — do not silence B6.
        //
        // THIS PREDICATE IS ARTIFACT-SCOPED ON PURPOSE. It asks "was the CURRENT artifact
        // dispatched?", because a re-transform mints a new artifact and older rows MUST go stale, or
        // the corrected PO is never sent (the defect described above). Do not swap in an order-scoped
        // "did a send ever begin for this order, ever" marker test — that answers a different
        // question and would leave a corrected PO permanently unsent. A per-row marker test cannot answer THIS
        // one: DeliveryAttempt has no ArtifactId column, and IdempotencyKey (artifact-scoped,
        // plk-dlv-{orderId:N}-{artifactId:N}) cannot be backfilled — per-attempt artifact identity was
        // never stored. Two discriminators is the correct shape, not drift; collapsing them yields a
        // silent lost order on this side or a wrong supplier-callback verdict on the other.
        //
        // Bounded by maxBatch (oldest strand first) so one sweep can never load an unbounded backlog;
        // the remainder is picked up by the next run.
        var candidates = await _db.PurchaseOrders
            .Where(o => o.Status == OrderStatusConstants.ReadyToDeliver && o.UpdatedAt < cutoff)
            .Where(o => o.SupplierId != null)
            .Where(o => _db.SupplierDeliveryConfigs.Any(c =>
                c.OrgId == o.OrgId && c.SupplierId == o.SupplierId && c.AutoDeliver))
            // WP-35: the cutoff is the newest DELIVERABLE artifact. A re-processed preview is newer
            // than every real artifact, so an unfiltered Max would move the cutoff past the last
            // genuine attempt and make an already-delivered order look stranded — and this sweep
            // re-drives delivery on a timer with no human in the loop.
            .Where(o => !_db.DeliveryAttempts.Any(a => a.OrderId == o.Id && a.OrgId == o.OrgId
                && a.AttemptedAt >= _db.OutboundArtifacts
                    .Where(art => art.OrderId == o.Id && art.OrgId == o.OrgId
                               && !art.FileKey.Contains(OutboundArtifactSelection.ReprocessKeyMarker))
                    .Max(art => art.CreatedAt)))
            .OrderBy(o => o.UpdatedAt)
            .Take(maxBatch)
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
            // ready_to_deliver is only ever set alongside an artifact, but guard defensively — an
            // order with no artifact has nothing to deliver. (The routed / auto-deliver / no-attempt
            // filters are already enforced in the query above.)
            // WP-35: what this sweep dispatches must be the order's deliverable output, never a
            // re-processed preview — this is the one send path with no operator behind it.
            var artifactId = await _db.OutboundArtifacts
                .Where(a => a.OrderId == order.Id && a.OrgId == order.OrgId)
                .Deliverable()
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
                // Read by a human during a double-send investigation — it must not overstate what was
                // checked. The order may well carry OLDER attempt rows (from a delivery of a previous
                // artifact, or a pre-dispatch failure that sent nothing); what was verified is only
                // that none of them targets the artifact named above.
                detail = "Order left in ready_to_deliver with an artifact that has no delivery attempt against it (delivery enqueue lost between the transform commit and DeliverOrderJob.Enqueue) — re-driving delivery. Attempts against EARLIER artifacts may exist and were deliberately not treated as evidence about this one.",
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
                "StrandedReadyDeliveryDetection: order {OrderId} (org {OrgId}) stranded in 'ready_to_deliver' with artifact {ArtifactId}, which has no delivery attempt against it (attempts against earlier artifacts may exist) — re-enqueueing delivery.",
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
