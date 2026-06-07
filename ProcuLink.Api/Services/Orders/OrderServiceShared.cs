using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Shared helpers extracted verbatim from the original <c>OrderService</c> during the
/// internal-facade decomposition (audit W1/B1). Behaviour is unchanged — these methods
/// were lifted exactly as they were, only their host type moved.
///
/// Instance members (<see cref="SafeReconcileExceptionsAsync"/>, <see cref="EmitPassportEventAsync"/>)
/// need the scoped <see cref="ProcuLinkDbContext"/> / services, so this is constructed once
/// in the <c>OrderService</c> ctor and passed to the sub-services that use them.
/// Static members (<see cref="BuildAuditEvent"/>, <see cref="ApplyExtractionReviewFlags"/>)
/// are pure.
/// </summary>
internal sealed class OrderServiceShared
{
    private readonly ProcuLinkDbContext     _db;
    private readonly IOrderExceptionService _exceptions;
    private readonly ILogger                _logger;

    public OrderServiceShared(
        ProcuLinkDbContext     db,
        IOrderExceptionService exceptions,
        ILogger                logger)
    {
        _db         = db;
        _exceptions = exceptions;
        _logger     = logger;
    }

    /// <summary>
    /// Best-effort exception reconciliation: exception generation is operational
    /// observability data and must never fail the parent order operation.
    /// </summary>
    public async Task SafeReconcileExceptionsAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        try
        {
            await _exceptions.ReconcileAsync(orgId, orderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception reconcile failed for order {OrderId} (non-fatal).", orderId);
        }
    }

    public async Task EmitPassportEventAsync(
        Guid orgId, Guid orderId,
        string stage, string eventType,
        string actorType = "system", string? actorId = null,
        object? payload = null,
        CancellationToken ct = default)
    {
        _db.PoPassportEvents.Add(new PoPassportEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            OrderId    = orderId,
            Stage      = stage,
            EventType  = eventType,
            ActorType  = actorType,
            ActorId    = actorId,
            Payload    = payload is null ? null : JsonSerializer.Serialize(payload),
            OccurredAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    public static AuditEvent BuildAuditEvent(Guid orgId, Guid entityId, string action, object payload) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = entityId,
            Action     = action,
            Payload    = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
            CreatedAt  = DateTime.UtcNow
        };

    /// <summary>
    /// Forces every line the structured extractor flagged (a number that did not
    /// appear in the source text, or a quantity × unit price that did not reconcile
    /// with the stated amount) to "needs review" — even if its code resolved
    /// deterministically — and caps its confidence so it surfaces for a human.
    /// </summary>
    public static void ApplyExtractionReviewFlags(
        IReadOnlyList<PurchaseOrderLineEntity> lines,
        IReadOnlyCollection<int> reviewLineNumbers)
    {
        if (reviewLineNumbers.Count == 0) return;

        var reviewSet = reviewLineNumbers.ToHashSet();
        foreach (var le in lines)
        {
            if (!reviewSet.Contains(le.LineNumber)) continue;
            le.NeedsReview = true;
            if (le.Confidence > 0.5f) le.Confidence = 0.5f;
        }
    }
}
