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
    //
    // 'pending_parse' is here for a different reason from the other two, and it is worth naming.
    // Nothing WRITES it today: it is the C# default on PurchaseOrderEntity.Status, and every
    // construction site overrides it before the row is saved. It survives as a default waiting to
    // leak — the first ingest path that forgets one of those assignments produces an order in a
    // status with no sweeper, no alert and no UI bucket, i.e. one that is permanently invisible
    // rather than merely late. Sweeping it costs nothing while nothing writes it (the query matches
    // no rows) and converts that silent, permanent loss into the ordinary requeue-then-dead-letter
    // path the moment it does. A status the product declares and no watchdog covers is the shape
    // this sweep exists to prevent.
    private static readonly string[] TransientStatuses =
    {
        OrderStatusConstants.PendingParse,
        OrderStatusConstants.Parsing,
        OrderStatusConstants.Transforming,
    };

    /// <summary>
    /// The PARSE-leg members of <see cref="TransientStatuses"/> — the strands the requeue branch
    /// re-drives through a fresh parse job.
    ///
    /// <para>Deliberately a NAMED positive test rather than <c>!= Transforming</c>. The branches
    /// below already read as "transforming, or everything else", and adding <c>pending_parse</c> to
    /// the transient set while leaving the requeue branch keyed on <c>== Parsing</c> would have
    /// routed a leaked <c>pending_parse</c> order into the TRANSFORM recovery — resetting an order
    /// that has never been parsed to <c>ready</c> and offering an operator an empty PO to send. A
    /// third transient status arriving one day must land in a branch someone chose for it, not in
    /// whichever one the <c>else</c> happens to be.</para>
    /// </summary>
    private static bool IsParseSide(string status) =>
        status is OrderStatusConstants.PendingParse or OrderStatusConstants.Parsing;

    /// <summary>
    /// How many times a single order may be re-enqueued before we stop looping it.
    /// A transient Worker restart mid-job is recoverable; past this cap the outcome is
    /// status-aware: a parse-side strand that keeps failing to parse is dead-lettered to
    /// terminal 'failed', whereas a 'transforming' strand is recovered to the re-sendable
    /// 'ready' state (a real failing transform reverts itself to 'ready', so a strand the
    /// sweep still sees is a rare claimed-but-no-job crash window — never a true failure).
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

        // Collect parse-job requeues to fire AFTER the bookkeeping (RequeueCount/UpdatedAt)
        // is committed — a re-enqueued job must not run before this sweep's write lands.
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

                if (IsParseSide(fromStatus))
                {
                    // KEEP the order in 'parsing' and re-drive a fresh parse job. The parse
                    // guard (OrderIngestionService.ParseStoredFileAsync) only does work when
                    // Status == "parsing"; resetting to 'pending_parse' here made the
                    // re-enqueued job log "already processed, skipping parse" and return WITHOUT
                    // parsing — silently stranding the order forever. 'parsing' is the same
                    // status the normal ingest and the unrouted→re-parse flow re-parse from, and
                    // the parse's atomic ExecuteUpdate is itself keyed on 'parsing', so a stray
                    // concurrent job cannot double-write. UpdatedAt is bumped below, so the order
                    // leaves the stuck window until it stalls again.
                    //
                    // A leaked 'pending_parse' order takes this same door, and 'parsing' is the
                    // right target for it too: it is the ONLY status ParseStoredFileAsync acts on,
                    // and pending_parse -> parsing is exactly what the normal ingest performs. The
                    // assignment below is not a no-op for that case, which is why it is written
                    // unconditionally rather than guarded as "already parsing".
                    order.Status = OrderStatusConstants.Parsing;
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
                    parseReEnqueued = IsParseSide(fromStatus) && _parseEnqueuer is not null,
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
            else if (fromStatus == OrderStatusConstants.Transforming)
            {
                // ── Requeue cap exceeded, but a 'transforming' strand is NOT a genuine
                //    failure → recover to 'ready', never terminal Failed ───────────────
                // The order is already fully resolved. A transform job that actually RAN and failed
                // records ITSELF as transform_failed — OrderTransformService's wrapper catches
                // anything that escapes the whole method and routes it through the same guarded
                // write the acceptance gate and the template/mapping failures use. So a strand this
                // sweep still sees means one of exactly two things, and 'ready' is right for both:
                //   • CLAIMED BUT NO JOB EVER RAN — the rare crash window between the controller's
                //     claim commit and its synchronous enqueue.
                //   • THE PROCESS DIED MID-TRANSFORM — OOM, eviction, a hard kill. No catch runs, so
                //     nothing could have been recorded. That is a transient infrastructure fault,
                //     not an order-level one, and retrying is the correct answer to it.
                // Neither must ever become a permanent false-failure. Recover to the healthy,
                // re-sendable 'ready' state (mirrors how a stuck DELIVERY dead-letters to the
                // RECOVERABLE delivery_dead_letter, never terminal Failed). RequeueCount is reset so
                // a future genuine stall gets a fresh requeue budget.
                //
                // NOTE: the premise here used to read "a transform job that actually RAN and failed
                // reverts ITSELF to 'ready'". That was stale twice over — such a job lands in
                // transform_failed, not ready, and the pre-fix code had two unguarded regions from
                // which a job that ran could strand here with nothing recorded at all.
                var stalledRequeueCount = order.RequeueCount;
                order.Status = OrderStatusConstants.Ready;
                order.RequeueCount = 0;
                order.UpdatedAt = now;

                var recoverPayload = JsonSerializer.Serialize(new
                {
                    reason = "StuckTransformRecovered",
                    fromStatus,
                    toStatus = OrderStatusConstants.Ready,
                    stuckSince,
                    detectedAt = now,
                    thresholdMinutes = stuckThreshold.TotalMinutes,
                    requeueCount = stalledRequeueCount,
                    maxRequeues = MaxRequeues,
                    deadLettered = false,
                    detail = "Order stranded in 'transforming' (claimed but no transform job ran) past the requeue cap — recovered to 'ready' so it can be re-sent; never marked failed.",
                });

                _db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    OrgId = order.OrgId,
                    UserId = null,
                    EntityType = "Order",
                    EntityId = order.Id,
                    Action = "StuckTransformRecovered",
                    Payload = JsonDocument.Parse(recoverPayload),
                    CreatedAt = now,
                });

                _logger.LogWarning(
                    "StuckOrderDetection: order {OrderId} (org {OrgId}) stranded in 'transforming' since {StuckSince:o} past the requeue cap — recovering to 'ready' (NOT failed).",
                    order.Id, order.OrgId, stuckSince);
            }
            else
            {
                // ── Requeue cap exceeded → dead-letter (genuinely failed) ─────────
                // Reached only for a PARSE-side strand ('parsing', or a leaked 'pending_parse' this
                // sweep already re-drove through 'parsing' twice): a file that keeps failing to
                // parse is genuinely unprocessable, so it is dead-lettered as failed. The
                // pending_parse -> failed edge that implies is declared in
                // OrderStatusMachine.Transitions, which carries the argument for it.
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

        // Fire parse requeues only after the requeue bookkeeping is committed.
        if (parseRequeues.Count > 0)
        {
            if (_parseEnqueuer is null)
            {
                _logger.LogWarning(
                    "StuckOrderDetection: {Count} parse requeue(s) kept in '{Parsing}' but no IParseJobEnqueuer is registered in this process — orders will be re-driven once an enqueuer is available.",
                    parseRequeues.Count, OrderStatusConstants.Parsing);
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
