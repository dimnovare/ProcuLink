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
    // clears DeliveryDueAt, and dead-letter clears it too, but we also exclude them here so a
    // late-arriving row (or legacy data) can never be flagged after the fact.
    private static readonly string[] ExcludedStatuses =
    {
        OrderStatusConstants.Delivered,
        OrderStatusConstants.DeliveryDeadLetter,
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
        var breached = await _db.PurchaseOrders
            .Where(o => o.DeliveryDueAt != null
                        && o.DeliveryDueAt < now
                        && !o.SlaBreached
                        && !ExcludedStatuses.Contains(o.Status))
            .ToListAsync(ct);

        if (breached.Count == 0)
            return 0;

        foreach (var order in breached)
        {
            var dueAt = order.DeliveryDueAt!.Value;

            order.SlaBreached = true;
            order.UpdatedAt = now;

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

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("DeliverySla: flagged {Count} order(s) as SLA-breached.", breached.Count);
        return breached.Count;
    }
}
