using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

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

    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<StuckOrderDetectionService> _logger;

    public StuckOrderDetectionService(ProcuLinkDbContext db, ILogger<StuckOrderDetectionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> RunAsync(TimeSpan stuckThreshold, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - stuckThreshold;

        // Cross-tenant maintenance sweep. This mirrors EmailPollingJob, which also
        // reads across all organisations. Tenant isolation is preserved because each
        // failed order and its audit event carry that order's own OrgId.
        var stuck = await _db.PurchaseOrders
            .Where(o => TransientStatuses.Contains(o.Status) && o.UpdatedAt < cutoff)
            .ToListAsync(ct);

        if (stuck.Count == 0)
            return 0;

        var now = DateTime.UtcNow;

        foreach (var order in stuck)
        {
            var fromStatus = order.Status;
            var stuckSince = order.UpdatedAt;

            order.Status = OrderStatusConstants.Failed;
            order.UpdatedAt = now;

            var payload = JsonSerializer.Serialize(new
            {
                reason = "StuckTimeout",
                fromStatus,
                stuckSince,
                detectedAt = now,
                thresholdMinutes = stuckThreshold.TotalMinutes,
            });

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                OrgId = order.OrgId,
                UserId = null,
                EntityType = "Order",
                EntityId = order.Id,
                Action = "StuckTimeout",
                Payload = JsonDocument.Parse(payload),
                CreatedAt = now,
            });

            _logger.LogWarning(
                "StuckOrderDetection: order {OrderId} (org {OrgId}) was stuck in '{FromStatus}' since {StuckSince:o} — marking failed.",
                order.Id, order.OrgId, fromStatus, stuckSince);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("StuckOrderDetection: marked {Count} stuck order(s) as failed.", stuck.Count);
        return stuck.Count;
    }
}
