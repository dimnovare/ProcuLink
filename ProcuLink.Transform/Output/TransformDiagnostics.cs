using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Tiny static observability sink for the Transform layer's <b>static, DI-less</b> output builders
/// (<see cref="MappedTransformService"/>, <see cref="SourceMapReDerive"/>,
/// <see cref="OutputTemplateEmitter"/>). Those types are constructed with a bare
/// <c>new()</c> from many call sites, so there is no per-instance <c>ILogger</c> to inject.
///
/// <para>This sink lets those paths emit a warning when a <em>fail-open / fail-safe</em> branch
/// swallows an error (a corrupt total that degrades to 0, a Scriban expression / SourceMap /
/// IncludeWhen predicate that fails to evaluate). The <b>behaviour is unchanged</b> — these paths
/// still fail open exactly as before; this only adds a log line so the silent degradation is
/// observable instead of invisible.</para>
///
/// <para><b>Default = silent.</b> The factory defaults to <see cref="NullLoggerFactory"/>, so until a
/// composition root assigns a real <see cref="ILoggerFactory"/> (optional — the app works without
/// it) nothing is written and the default behaviour is byte-for-byte identical. The assignment is a
/// one-time process-wide opt-in; it never changes transform output.</para>
/// </summary>
public static class TransformDiagnostics
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>
    /// Assign the process-wide logger factory the static output builders log through. Optional: call
    /// once from the composition root (e.g. <c>TransformDiagnostics.LoggerFactory = app.Services
    /// .GetRequiredService&lt;ILoggerFactory&gt;();</c>). Until then logging is a no-op. A null value
    /// resets to the silent <see cref="NullLoggerFactory"/>.
    /// </summary>
    public static ILoggerFactory LoggerFactory
    {
        get => _factory;
        set => _factory = value ?? NullLoggerFactory.Instance;
    }

    /// <summary>Create a category-scoped logger. Cheap; safe to call per invocation.</summary>
    internal static ILogger CreateLogger(string category) => _factory.CreateLogger(category);

    /// <summary>Create a category-scoped logger for <typeparamref name="T"/>.</summary>
    internal static ILogger CreateLogger<T>() => _factory.CreateLogger(typeof(T).FullName ?? typeof(T).Name);
}
