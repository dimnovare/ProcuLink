using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IAnalyticsService"/>. Records every captured event
/// in-memory so emitter tests can assert event name, distinct id, and properties.
/// </summary>
public sealed class FakeAnalyticsService : IAnalyticsService
{
    public List<CapturedEvent> CapturedEvents { get; } = new();

    public Task CaptureAsync(
        Guid orgId,
        string? userId,
        string eventName,
        IReadOnlyDictionary<string, object?>? props = null,
        CancellationToken ct = default)
    {
        CapturedEvents.Add(new CapturedEvent(
            orgId,
            userId,
            eventName,
            props ?? new Dictionary<string, object?>()));
        return Task.CompletedTask;
    }

    public Task SetPersonPropertiesAsync(
        string distinctId,
        IReadOnlyDictionary<string, object?> props,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    public sealed record CapturedEvent(
        Guid OrgId,
        string? UserId,
        string EventName,
        IReadOnlyDictionary<string, object?> Properties);
}
