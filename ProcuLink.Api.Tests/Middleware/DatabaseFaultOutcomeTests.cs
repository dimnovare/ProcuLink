using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcuLink.Api.Middleware;

namespace ProcuLink.Api.Tests.Middleware;

/// <summary>
/// The diagnostic has to answer the question, and it has to stay quiet otherwise.
///
/// <para>The second half is the one worth testing. A diagnostic that logs on every
/// request would bury the four Sentry issues it was written to explain, in the same
/// dashboard that has spent this week being de-noised.</para>
/// </summary>
public class DatabaseFaultOutcomeTests
{
    private sealed class Captured : ILogger
    {
        public List<string> Lines { get; } = new();
        public List<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            Levels.Add(level);
            Lines.Add(fmt(state, ex));
        }
    }

    private sealed class CapturedFactory(Captured logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    /// <summary>Runs the middleware over one request and returns what it logged.</summary>
    private static Captured Run(bool faulted, int status, string kind = "connection")
    {
        var captured = new Captured();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new CapturedFactory(captured));
        var provider = services.BuildServiceProvider();

        var builder = new ApplicationBuilder(provider);
        builder.UseDatabaseFaultOutcome();
        builder.Run(context =>
        {
            // Stand in for the EF interceptor, which writes the same item.
            if (faulted) context.Items["proculink.db-fault"] = kind;
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        var pipeline = builder.Build();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = "GET";
        http.Request.Path = "/api/billing/status";
        pipeline(http).GetAwaiter().GetResult();
        return captured;
    }

    [Fact]
    public void SaysSurvived_whenTheRequestStillSucceeded()
    {
        var log = Run(faulted: true, status: 200);

        var line = Assert.Single(log.Lines);
        Assert.Contains("SURVIVED", line);
        Assert.Contains("/api/billing/status", line);
        Assert.Contains("connection", line);
        Assert.Equal(LogLevel.Error, Assert.Single(log.Levels));
    }

    [Fact]
    public void SaysFailed_whenTheRequestReturnedAServerError()
    {
        var log = Run(faulted: true, status: 500);

        Assert.Contains("FAILED", Assert.Single(log.Lines));
    }

    [Fact]
    public void CountsAClientErrorAsSurvived_becauseTheAppStillAnswered()
    {
        // A 403 after a database blip is the app deciding, not the fault escaping.
        var log = Run(faulted: true, status: 403);

        Assert.Contains("SURVIVED", Assert.Single(log.Lines));
    }

    [Fact]
    public void SaysNothingAtAll_whenNoFaultWasRecorded()
    {
        // The anti-vacuity test, and the one that keeps this diagnostic affordable.
        Assert.Empty(Run(faulted: false, status: 200).Lines);
        Assert.Empty(Run(faulted: false, status: 500).Lines);
    }

    [Fact]
    public void StillReportsWhenTheRequestThrew()
    {
        // An unhandled exception is exactly the case worth hearing about, and it is
        // the one a plain `await next(); if (…)` would silently skip.
        var captured = new Captured();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new CapturedFactory(captured));
        var provider = services.BuildServiceProvider();

        var builder = new ApplicationBuilder(provider);
        builder.UseDatabaseFaultOutcome();
        builder.Run(context =>
        {
            context.Items["proculink.db-fault"] = "connection";
            throw new InvalidOperationException("boom");
        });
        var pipeline = builder.Build();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = "GET";
        http.Request.Path = "/api/suppliers";

        Assert.Throws<InvalidOperationException>(() => pipeline(http).GetAwaiter().GetResult());
        Assert.Contains("/api/suppliers", Assert.Single(captured.Lines));
    }
}
