namespace ProcuLink.Core.Services;

public interface IAnalyticsService
{
    /// <summary>
    /// Captures a server-side event. No-op when no PostHog key is configured.
    /// </summary>
    Task CaptureAsync(
        Guid organisationId,
        string? userId,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sets person-level properties on the given distinct id.
    /// </summary>
    Task SetPersonPropertiesAsync(
        string distinctId,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken ct = default);

    /// <summary>
    /// Flushes the in-memory queue. Called on shutdown.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);
}
