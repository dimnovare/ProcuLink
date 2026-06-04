using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
public sealed class OpsHealthService : IOpsHealthService
{
    /// <summary>
    /// Mirrors <c>StuckOrderDetectionJob.StuckThreshold</c> (30 min): how long an order may
    /// sit in a transient status before an operator should treat it as stuck.
    /// </summary>
    public TimeSpan StuckThreshold { get; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A heartbeat older than this is treated as a dead/missing worker.
    /// Hangfire's default HeartbeatInterval is 30 s; we allow 2× leeway.
    /// </summary>
    private static readonly TimeSpan WorkerHeartbeatDeadline = TimeSpan.FromSeconds(60);

    private readonly ProcuLinkDbContext _db;
    private readonly IMonitoringApi?    _monitoring;

    public OpsHealthService(ProcuLinkDbContext db, IMonitoringApi? monitoring = null)
    {
        _db         = db;
        _monitoring = monitoring;
    }

    public async Task<OpsHealthSummary> GetHealthAsync(Guid organisationId, CancellationToken ct)
    {
        var stuckCutoff = DateTime.UtcNow - StuckThreshold;

        // One org-scoped GROUP BY over orders → all status counts in a single round-trip.
        // Stuck counts need the time predicate, so they are computed separately but still
        // as cheap COUNT queries (indexed on OrgId + Status).
        var byStatus = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == organisationId)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(string status) =>
            byStatus.FirstOrDefault(r => r.Status == status)?.Count ?? 0;

        var parsingStuck = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == organisationId
                          && o.Status == OrderStatusConstants.Parsing
                          && o.UpdatedAt < stuckCutoff, ct);

        var deliveringStuck = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == organisationId
                          && o.Status == OrderStatusConstants.Delivering
                          && o.UpdatedAt < stuckCutoff, ct);

        var slaBreached = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == organisationId
                          && o.SlaBreached
                          && o.Status != OrderStatusConstants.Delivered, ct);

        var openExceptions = await _db.OrderExceptions
            .CountAsync(e => e.OrgId == organisationId && e.State == "open", ct);

        // ── Worker / Hangfire-server health ──────────────────────────────────────
        var (activeWorkers, lastHeartbeat, secondsSince, workerHealthy) = GetWorkerHealth();

        return new OpsHealthSummary(
            ParsingStuck:                 parsingStuck,
            DeliveringStuck:              deliveringStuck,
            TransformFailed:              Count(OrderStatusConstants.TransformFailed),
            DeliveryFailed:               Count(OrderStatusConstants.DeliveryFailed),
            DeliveryDeadLetter:           Count(OrderStatusConstants.DeliveryDeadLetter),
            RejectedBySupplier:           Count(OrderStatusConstants.RejectedBySupplier),
            Failed:                       Count(OrderStatusConstants.Failed),
            SlaBreached:                  slaBreached,
            OpenExceptions:               openExceptions,
            StuckThresholdMinutes:        (int)StuckThreshold.TotalMinutes,
            ActiveWorkers:                activeWorkers,
            LastWorkerHeartbeatUtc:       lastHeartbeat,
            SecondsSinceWorkerHeartbeat:  secondsSince,
            WorkerHealthy:                workerHealthy);
    }

    /// <summary>
    /// Queries the Hangfire monitoring API for registered server heartbeats.
    /// Returns safe defaults when the monitoring API is unavailable or throws.
    /// </summary>
    private (int activeWorkers, DateTime? lastHeartbeat, double? secondsSince, bool healthy)
        GetWorkerHealth()
    {
        try
        {
            var api = _monitoring ?? Hangfire.JobStorage.Current?.GetMonitoringApi();
            if (api is null)
                return (0, null, null, false);

            var servers = api.Servers();
            if (servers is null || servers.Count == 0)
                return (0, null, null, false);

            var now          = DateTime.UtcNow;
            var lastHeartbeat = servers
                .Select(s => s.Heartbeat)
                .Where(h => h.HasValue)
                .Select(h => h!.Value)
                .DefaultIfEmpty()
                .Max();

            if (lastHeartbeat == default)
                return (servers.Count, null, null, false);

            var age     = (now - lastHeartbeat).TotalSeconds;
            var healthy = age <= WorkerHeartbeatDeadline.TotalSeconds;
            return (servers.Count, lastHeartbeat, age, healthy);
        }
        catch
        {
            // Monitoring API unavailable (e.g. storage not yet ready, test environment).
            return (0, null, null, false);
        }
    }

    public async Task<IReadOnlyList<DeadLetterOrder>> ListDeadLetterAsync(
        Guid organisationId, bool includeFailed, CancellationToken ct)
    {
        var orders = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == organisationId
                     && (o.Status == OrderStatusConstants.DeliveryDeadLetter
                      || (includeFailed && o.Status == OrderStatusConstants.DeliveryFailed)))
            .OrderByDescending(o => o.UpdatedAt)
            .Select(o => new
            {
                o.Id,
                o.PoNumber,
                o.SupplierId,
                o.Status,
                o.CreatedAt,
                o.UpdatedAt,
            })
            .ToListAsync(ct);

        if (orders.Count == 0)
            return Array.Empty<DeadLetterOrder>();

        var orderIds    = orders.Select(o => o.Id).ToList();
        var supplierIds = orders.Select(o => o.SupplierId).Distinct().ToList();

        // Supplier names in one org-scoped lookup (avoids a navigation projection that the
        // EF InMemory provider mistranslates when no supplier rows exist).
        var supplierNames = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.OrgId == organisationId && supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        // Pull the latest delivery attempt per order in one org-scoped query, then reduce
        // in-memory (InMemory provider doesn't translate GroupBy→First reliably; this is a
        // small operator list, not a hot path).
        var attempts = await _db.DeliveryAttempts
            .AsNoTracking()
            .Where(a => a.OrgId == organisationId
                     && a.OrderId != null
                     && orderIds.Contains(a.OrderId!.Value))
            .Select(a => new
            {
                OrderId = a.OrderId!.Value,
                a.AttemptedAt,
                a.ErrorMessage,
                a.RejectionReason,
                a.ResponseCode,
            })
            .ToListAsync(ct);

        var byOrder = attempts
            .GroupBy(a => a.OrderId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Count  = g.Count(),
                    Latest = g.OrderByDescending(a => a.AttemptedAt).First(),
                });

        return orders.Select(o =>
        {
            byOrder.TryGetValue(o.Id, out var att);
            return new DeadLetterOrder(
                OrderId:          o.Id,
                PoNumber:         o.PoNumber,
                SupplierId:       o.SupplierId,
                SupplierName:     supplierNames.GetValueOrDefault(o.SupplierId, string.Empty),
                Status:           o.Status,
                DeliveryAttempts: att?.Count ?? 0,
                LastError:        att?.Latest.RejectionReason ?? att?.Latest.ErrorMessage,
                LastResponseCode: att?.Latest.ResponseCode,
                LastAttemptAt:    att?.Latest.AttemptedAt,
                CreatedAt:        o.CreatedAt,
                UpdatedAt:        o.UpdatedAt);
        }).ToList();
    }
}
