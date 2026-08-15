namespace ProcuLink.Core.Services;

/// <summary>
/// Enqueues outbound trigger deliveries for all active subscriptions matching
/// the org + event type. Each subscription fires as a separate Hangfire job.
/// <para>
/// <paramref name="eventType"/> must come from <c>IntegrationEventTypes</c>, which is both the emit
/// vocabulary and the subscribe allow-list. Matching here is EXACT string equality, so an event
/// type outside that class fans out to zero subscriptions and returns silently — it does not fail.
/// </para>
/// </summary>
public interface IIntegrationTriggerService
{
    Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct);
}
