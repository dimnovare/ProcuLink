using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcuLink.Api.Jobs;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Composition;

[CollectionDefinition("worker-host", DisableParallelization = true)]
public sealed class WorkerHostCollection;

/// <summary>
/// A job the Worker runs but does not register only fails at <c>Host.Build()</c> in production
/// (<c>ValidateOnBuild</c> is on in Development only). This runs the Worker's real entry point as
/// Development, takes the built host from the <c>HostBuilt</c> diagnostic event (the hook
/// <c>HostFactoryResolver</c> uses) and aborts before <c>Run()</c>, so nothing starts.
/// </summary>
[Collection("worker-host")]
public class WorkerCompositionTests
{
    private sealed class HostBuiltSentinel(IHost host) : Exception { public IHost Host { get; } = host; }

    [Fact]
    public void Worker_services_resolve()
    {
        var env = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__DefaultConnection"] = "Host=localhost;Database=none;Username=none;Password=none",
            ["Storage__R2Endpoint"] = "", // keep the startup clock probe off the network
            ["Sentry__Dsn"] = "",
        };
        var saved = env.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);

        using var listener = new HostBuiltListener();
        IHost? host = null;
        try
        {
            var entry = typeof(ProcuLink.Worker.Worker).Assembly.EntryPoint!;
            entry.Invoke(null, new object[] { Array.Empty<string>() });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is HostBuiltSentinel s)
        {
            host = s.Host;
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }

        host.Should().NotBeNull("the Worker must build its host (ValidateOnBuild is on in Development)");
        // Registered, not instantiated: building a job reaches Hangfire's storage, which would open
        // a database connection. The dependency graph itself was already validated by Build().
        var registered = host!.Services.GetRequiredService<IServiceProviderIsService>();
        registered.IsService(typeof(DisposableOrgSweepJob)).Should().BeTrue();
        registered.IsService(typeof(ReadDocumentJob)).Should().BeTrue();
    }

    /// <summary>Aborts the Worker's own build only; a host another test builds is left alone.</summary>
    private sealed class HostBuiltListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _all;
        private IDisposable? _hosting;

        public HostBuiltListener() => _all = DiagnosticListener.AllListeners.Subscribe(this);

        public void OnNext(DiagnosticListener l)
        {
            if (l.Name == "Microsoft.Extensions.Hosting") _hosting = l.Subscribe(this);
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (e.Key == "HostBuilt" && e.Value is IHost h
                && h.Services.GetService<IServiceProviderIsService>()!.IsService(typeof(DisposableOrgSweepJob)))
                throw new HostBuiltSentinel(h);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { _hosting?.Dispose(); _all.Dispose(); }
    }
}
