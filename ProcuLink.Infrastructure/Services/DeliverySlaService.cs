using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class DeliverySlaService : IDeliverySlaService
{
    // Terminal / confirmed statuses whose SLA must never be flagged. A confirmed delivery
    // clears DeliveryDueAt, dead-letter clears it too, and a supplier rejection (4xx / manual
    // mark-rejected) now clears it as well — but we also exclude them here so a late-arriving row
    // (or legacy data written before those clears existed) can never be flagged after the fact.
    private static readonly string[] ExcludedStatuses =
    {
        OrderStatusConstants.Delivered,
        OrderStatusConstants.DeliveryDeadLetter,
        OrderStatusConstants.RejectedBySupplier,
    };

    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<DeliverySlaService> _logger;

    public DeliverySlaService(ProcuLinkDbContext db, ILogger<DeliverySlaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Cross-tenant maintenance sweep (mirrors StuckOrderDetectionService). Tenant isolation
        // is preserved because each flagged order and its audit event carry that order's own OrgId.
        //
        // NOTE: the !SlaBreached predicate here is only a cheap pre-filter. It is NOT the guard —
        // it is advisory (TOCTOU), because an overlapping sweep can flag the same order between this
        // SELECT and the write. The real guard is the atomic claim in FlagAtomicallyAsync. Entities
        // are left TRACKED because the non-relational path below mutates them.
        //
        // OrderBy(o => o.Id) is load-bearing, not cosmetic: FlagAtomicallyAsync claims row-by-row
        // inside one transaction, so it holds each claimed row's lock until commit. Two overlapping
        // sweeps walking an UNORDERED result set could lock the same two orders in opposite order
        // and deadlock — Postgres would detect it and kill one sweep. A total order shared by every
        // sweep rules that out BETWEEN TWO SLA SWEEPS.
        //
        // It does NOT rule out a deadlock against StuckDeliveryDetectionService, which selects
        // overlapping 'delivering' rows with no OrderBy and commits its mutations via a single
        // change-tracked SaveChanges (UPDATE ordering is EF's, not Id order). Both jobs run on the
        // identical */15 * * * * cron (see Worker.cs). A Postgres deadlock between the two is
        // possible when >=2 orders are simultaneously stuck-delivering and SLA-overdue. This is an
        // accepted residual, not a defect to fix here: both sides are transactional, Postgres kills
        // one victim, and that sweep simply retries on the next 15-minute tick — self-healing and
        // non-corrupting.
        var breached = await _db.PurchaseOrders
            .Where(o => o.DeliveryDueAt != null
                        && o.DeliveryDueAt < now
                        && !o.SlaBreached
                        && !ExcludedStatuses.Contains(o.Status))
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        if (breached.Count == 0)
            return 0;

        var flagged = _db.Database.IsRelational()
            ? await FlagAtomicallyAsync(breached, now, ct)
            : await FlagViaChangeTrackerAsync(breached, now, ct);

        if (flagged > 0)
            _logger.LogWarning("DeliverySla: flagged {Count} order(s) as SLA-breached.", flagged);

        return flagged;
    }

    /// <summary>
    /// Relational path — the flag flip IS the claim. The UPDATE re-checks every condition the
    /// RunAsync SELECT applied (not just !SlaBreached), against the same `now`, so it is both the
    /// concurrency guard and a re-verification against staleness: only the sweep whose statement
    /// affects a row writes the audit event; an overlapping sweep sees 0 rows and writes nothing, so
    /// the DeliverySlaBreached audit row can never be double-inserted, and an order that changed
    /// status or lost its DeliveryDueAt (e.g. delivered or dead-lettered) between the SELECT and the
    /// claim is silently skipped instead of flagged from a stale in-memory snapshot.
    ///
    /// <para>One transaction wraps the claims and their audit rows: ExecuteUpdateAsync auto-commits
    /// its own statement, so an unwrapped claim followed by a separate SaveChanges could crash
    /// between the two and leave a flagged order with NO audit trail. ExecuteUpdate enlists in the
    /// ambient transaction on Npgsql — the same pattern as the persist block in
    /// OrderIngestionService.ParseStoredFileAsync. The sweep set is small (overdue deliveries only),
    /// so the transaction is short.</para>
    /// </summary>
    private async Task<int> FlagAtomicallyAsync(
        List<PurchaseOrderEntity> breached, DateTime now, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var flagged = 0;
        foreach (var order in breached)
        {
            var claimed = await _db.PurchaseOrders
                .Where(o => o.Id == order.Id
                         && o.OrgId == order.OrgId
                         && !o.SlaBreached
                         && o.DeliveryDueAt != null
                         && o.DeliveryDueAt < now
                         && !ExcludedStatuses.Contains(o.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.SlaBreached, true)
                    .SetProperty(o => o.UpdatedAt, now), ct);

            if (claimed == 0)
            {
                _logger.LogInformation(
                    "DeliverySla: order {OrderId} (org {OrgId}) was not claimed — either flagged by a concurrent sweep, or its status/DeliveryDueAt changed (e.g. delivered or dead-lettered) since the SELECT — skipping.",
                    order.Id, order.OrgId);
                continue;
            }

            AddBreachAudit(order, now);
            flagged++;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return flagged;
    }

    /// <summary>
    /// Non-relational path — the EF InMemory test provider can translate neither ExecuteUpdate nor a
    /// transaction, so fall back to the original read-modify-write. InMemory tests are
    /// single-threaded, so the atomicity the relational claim guarantees is not needed here. Mirrors
    /// FireIntegrationTriggerJob.RecordFailureAsync, which splits on IsRelational() for the same reason.
    /// </summary>
    private async Task<int> FlagViaChangeTrackerAsync(
        List<PurchaseOrderEntity> breached, DateTime now, CancellationToken ct)
    {
        foreach (var order in breached)
        {
            order.SlaBreached = true;
            order.UpdatedAt = now;
            AddBreachAudit(order, now);
        }

        await _db.SaveChangesAsync(ct);
        return breached.Count;
    }

    /// <summary>Queues the DeliverySlaBreached audit row for one claimed order. Caller SaveChanges.</summary>
    private void AddBreachAudit(PurchaseOrderEntity order, DateTime now)
    {
        var dueAt = order.DeliveryDueAt!.Value;

        var payload = JsonSerializer.Serialize(new
        {
            reason = "DeliverySlaBreached",
            status = order.Status,
            dueAt,
            detectedAt = now,
            overdueMinutes = Math.Round((now - dueAt).TotalMinutes, 1),
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = order.OrgId,
            UserId = null,
            EntityType = "Order",
            EntityId = order.Id,
            Action = "DeliverySlaBreached",
            Payload = JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        _logger.LogWarning(
            "DeliverySla: order {OrderId} (org {OrgId}) breached its SLA — due {DueAt:o}, status '{Status}'.",
            order.Id, order.OrgId, dueAt, order.Status);
    }
}
