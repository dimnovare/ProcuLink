namespace ProcuLink.Core.Services;

/// <summary>
/// Enqueues outbound trigger deliveries for all active subscriptions matching
/// the org + event type. Each subscription fires as a separate Hangfire job.
/// Call this from OrderService (order.created) and DeliveryService (order.delivered / order.failed).
/// </summary>
public interface IIntegrationTriggerService
{
    Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct);
}
