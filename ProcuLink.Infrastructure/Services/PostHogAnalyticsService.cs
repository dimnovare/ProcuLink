using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Configuration for the PostHog analytics backend client.
/// Bound from the <c>Analytics:PostHog</c> configuration section.
/// </summary>
public sealed class PostHogOptions
{
    /// <summary>PostHog project API key (a.k.a. project token). When null/empty the service no-ops.</summary>
    public string? ApiKey { get; set; }

    /// <summary>PostHog host URL. Defaults to the EU cloud endpoint.</summary>
    public string Host { get; set; } = "https://eu.posthog.com";
}

/// <summary>
/// Server-side analytics client. Wraps the official PostHog .NET SDK (v2.7.1) and
/// always tags events with the calling organisation group so PostHog cohort/funnel
/// filtering works. No-ops when no API key is configured.
/// </summary>
public sealed class PostHogAnalyticsService : IAnalyticsService, IAsyncDisposable
{
    private readonly PostHogOptions _opts;
    private readonly ILogger<PostHogAnalyticsService> _log;
    private readonly PostHog.PostHogClient? _client;
    private readonly ConcurrentQueue<TestEnvelope> _testQueue = new();

    public PostHogAnalyticsService(IOptions<PostHogOptions> opts, ILogger<PostHogAnalyticsService> log)
    {
        _opts = opts.Value;
        _log  = log;

        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
        {
            // The SDK requires IOptions<PostHog.PostHogOptions> (its own options type — distinct
            // from our local PostHogOptions above, so we fully-qualify it here).
            var sdkOptions = new PostHog.PostHogOptions
            {
                ProjectToken = _opts.ApiKey,
                HostUrl      = new Uri(_opts.Host),
            };
            _client = new PostHog.PostHogClient(Options.Create(sdkOptions));
        }
    }

    public Task CaptureAsync(
        Guid organisationId,
        string? userId,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        CancellationToken ct = default)
    {
        var distinctId = userId ?? $"org_{organisationId}";
        var groups = new Dictionary<string, string> { ["organisation"] = organisationId.ToString() };
        var props  = properties is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(properties);

        // Always present.
        props.TryAdd("environment", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "development");

        // Always queue locally so PeekTestQueue() can be asserted in tests.
        _testQueue.Enqueue(new TestEnvelope(eventName, distinctId, groups, props));

        if (_client is null) return Task.CompletedTask;

        try
        {
            // PostHog SDK Capture takes Dictionary<string, object>?. Filter nulls so we
            // don't hand the SDK a value-typed null inside an object reference dictionary.
            var sdkProps = new Dictionary<string, object>(props.Count);
            foreach (var kv in props)
            {
                if (kv.Value is not null) sdkProps[kv.Key] = kv.Value;
            }

            var sdkGroups = new PostHog.GroupCollection();
            foreach (var g in groups) sdkGroups.Add(g.Key, g.Value);

            _client.Capture(distinctId, eventName, sdkProps, sdkGroups, flags: null);
        }
        catch (Exception ex)
        {
            // Analytics must never break a business request path.
            _log.LogWarning(ex, "PostHog capture failed for event {Event}", eventName);
        }
        return Task.CompletedTask;
    }

    public async Task SetPersonPropertiesAsync(
        string distinctId,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        if (_client is null) return;

        try
        {
            var sdkProps = new Dictionary<string, object>(properties.Count);
            foreach (var kv in properties)
            {
                if (kv.Value is not null) sdkProps[kv.Key] = kv.Value;
            }

            await _client.IdentifyAsync(
                distinctId,
                personPropertiesToSet: sdkProps,
                personPropertiesToSetOnce: null,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostHog identify failed for {DistinctId}", distinctId);
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_client is null) return;
        try
        {
            await _client.FlushAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostHog flush failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(default);
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    /// <summary>Test-only sink — exposes everything enqueued for assertion.</summary>
    public IReadOnlyList<TestEnvelope> PeekTestQueue() => _testQueue.ToArray();

    public sealed record TestEnvelope(
        string EventName,
        string DistinctId,
        IReadOnlyDictionary<string, string> Groups,
        IReadOnlyDictionary<string, object?> Properties);
}
