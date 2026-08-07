using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Startup;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
//  MigrationBootstrap — what makes the startup migration task write the
//  process-global readiness flag, and when it must not.
//
//  THE DEFECT. The task was registered unconditionally, so a host booted with
//  EF InMemory — six test files do exactly that — entered a retry loop whose
//  MigrateAsync() can never succeed: 6 attempts with 3+6+9+12+15 = 45 s of
//  backoff, then MarkFailed(). Measured with a temporary probe after a single
//  HealthTestFactory boot, MigrationReadiness.State sat at Pending for 44 s and
//  flipped to Failed at t = 45 s — long after the class that booted the host had
//  finished, landing inside whatever OTHER class was asserting on the flag by
//  then. PR #175 serialised every host-booting class into the "postgres-container"
//  collection, which fixes the synchronous half; no xUnit collection can serialise
//  a write that arrives 45 s after the class ends, so this half had to be fixed in
//  production code.
//
//  ISOLATION. This class names MigrationReadiness, so it belongs to the assembly's
//  one serialisation domain for process-global state. Membership is enforced by
//  ProcessGlobalStateIsSerializedTests, not remembered.
//
//  DIRECTION. A gate that skips is worth only as much as its opposite direction:
//  RelationalProvider_StillEntersTheRetryLoop is the control that keeps
//  "skip when non-relational" from quietly becoming "skip", which would take the
//  Railway/Neon boot's migration — and its fail-loud path — with it.
// ════════════════════════════════════════════════════════════════════════════

[Collection("postgres-container")]
public sealed class MigrationBootstrapTests
{
    /// <summary>
    /// A skipped bootstrap returns in microseconds; an unskipped one against a provider that can
    /// never migrate takes 45 s. Anything inside this window means "returned promptly", and the
    /// window is wide enough that a loaded CI host cannot fake the difference.
    /// </summary>
    private static readonly TimeSpan PromptReturn = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a cancelled bootstrap gets to return. An honoured token returns on the next
    /// thread-pool tick; the backoff it is cancelled out of is 9s. Five seconds sits far enough
    /// from both that neither a loaded CI host nor an ignored token can be mistaken for the other.
    /// </summary>
    private static readonly TimeSpan CancelResponseWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a genuine relational attempt is given to fail and log. Connection-refused is
    /// immediate, but building this DbContext's model on a cold run is not, so the deadline is
    /// generous — it bounds a hang, it does not measure anything.
    /// </summary>
    private static readonly TimeSpan AttemptDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Relational, and guaranteed unreachable: port 1 is IANA-reserved (tcpmux) and nothing in CI
    /// listens there, so the connection is refused rather than timing out.
    /// </summary>
    private const string UnreachablePostgres =
        "Host=127.0.0.1;Port=1;Database=proculink_unreachable;Username=nobody;Password=nobody;" +
        "Timeout=2;Command Timeout=2;Pooling=false";

    // ── The gate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NonRelationalProvider_IsSkipped_WithoutRetryingOrWritingTheProcessGlobalFlag()
    {
        var previous = MigrationReadiness.State;
        MigrationReadiness.MarkPending();
        using var cts = new CancellationTokenSource();
        var log = new CapturingLoggerProvider();

        try
        {
            await using var services = BuildServices(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")), log);

            var bootstrap = MigrationBootstrap.RunAsync(services, EmptyConfiguration(), cts.Token);
            var finished = await Task.WhenAny(bootstrap, Task.Delay(PromptReturn));

            Assert.True(
                ReferenceEquals(finished, bootstrap),
                $"RunAsync had not returned {PromptReturn.TotalSeconds:0}s into an in-memory-backed host. "
                + $"MigrateAsync can never succeed on a non-relational provider, so it is sitting in the "
                + $"{MigrationBootstrap.MaxAttempts}-attempt retry loop and will write MigrationReadiness "
                + "roughly 45s from now — after this class has finished, inside some other class's "
                + $"set-then-assert window.\n\nLog so far:\n{log}");

            // Not merely "not Failed": the flag must be exactly as this test left it. A bootstrap
            // that decided anything at all about a schema it never looked at is the bug.
            await bootstrap;

            Assert.Equal(MigrationReadinessState.Pending, MigrationReadiness.State);
        }
        finally
        {
            cts.Cancel();
            Restore(previous);
        }
    }

    /// <summary>
    /// The opposite direction. Without this, deleting the whole bootstrap body would satisfy the
    /// test above — and take the Railway/Neon boot's migration with it.
    /// </summary>
    [Fact]
    public async Task RelationalProvider_StillEntersTheRetryLoop_SoTheGateCannotSwallowARealDeployment()
    {
        var previous = MigrationReadiness.State;
        MigrationReadiness.MarkPending();
        using var cts = new CancellationTokenSource();
        var log = new CapturingLoggerProvider();

        try
        {
            await using var services = BuildServices(o => o.UseNpgsql(UnreachablePostgres), log);

            var bootstrap = MigrationBootstrap.RunAsync(services, EmptyConfiguration(), cts.Token);
            var attempted = await WaitForLogAsync(log, "Migration attempt 1", AttemptDeadline);
            cts.Cancel();
            await bootstrap;

            Assert.True(
                attempted,
                "RunAsync never logged a first migration attempt against a relational provider within "
                + $"{AttemptDeadline.TotalSeconds:0}s. The non-relational skip is over-broad: production "
                + "boots Npgsql, and a bootstrap that declines to migrate it leaves the deployed schema "
                + $"stale with nothing reporting it.\n\nLog:\n{log}");
        }
        finally
        {
            cts.Cancel();
            Restore(previous);
        }
    }

    // ── The host-lifetime token ──────────────────────────────────────────────

    [Fact]
    public async Task CancellingDuringBackoff_StopsTheLoop_AndLeavesReadinessUntouched()
    {
        var previous = MigrationReadiness.State;
        MigrationReadiness.MarkPending();
        using var cts = new CancellationTokenSource();
        var log = new CapturingLoggerProvider();

        try
        {
            await using var services = BuildServices(o => o.UseNpgsql(UnreachablePostgres), log);

            var bootstrap = MigrationBootstrap.RunAsync(services, EmptyConfiguration(), cts.Token);

            // Cancel during the backoff before attempt 4, which is BackoffFor(3) = 9s, and not
            // during the first one. The first is 3s, and the loop's entry-side cancellation check
            // returns within that whether or not Task.Delay was given the token — so a test that
            // cancels there and allows any window ≥3s passes with the token dropped. Verified: it
            // did. Nine seconds is long enough that "returned because the token was honoured" and
            // "returned because the delay simply ran out" cannot be confused.
            var reachedDeepBackoff = await WaitForLogAsync(log, "Migration attempt 3", AttemptDeadline);

            var sw = Stopwatch.StartNew();
            cts.Cancel();
            var finished = await Task.WhenAny(bootstrap, Task.Delay(CancelResponseWindow));
            sw.Stop();

            Assert.True(
                reachedDeepBackoff,
                $"the loop never reached its third attempt, so this test proved nothing.\n\nLog:\n{log}");

            Assert.True(
                ReferenceEquals(finished, bootstrap),
                $"RunAsync ignored its CancellationToken: {sw.Elapsed.TotalSeconds:0.0}s after "
                + $"ApplicationStopping it is still inside the {MigrationBootstrap.BackoffFor(3).TotalSeconds:0}s "
                + "backoff. It will keep running past the host it belongs to — past a disposed "
                + "WebApplicationFactory in tests, and past shutdown in production — and then write "
                + $"MigrationReadiness.\n\nLog:\n{log}");

            await bootstrap;

            // A host shutting down is not a migration failure. MarkFailed here would page the founder
            // on every ordinary deploy and would be wrong: nothing was proven about the schema.
            Assert.Equal(MigrationReadinessState.Pending, MigrationReadiness.State);
        }
        finally
        {
            cts.Cancel();
            Restore(previous);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ServiceProvider BuildServices(
        Action<DbContextOptionsBuilder> configureDb, ILoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            // Information, not Trace: EF's model-building chatter runs to ~40 Debug lines and would
            // bury the two or three lines a failure message here needs to be readable.
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(logs);
        });
        services.AddDbContext<ProcuLinkDbContext>(configureDb);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Puts the process-global flag back exactly as this class found it. MigrationReadiness has a
    /// setter per state and no set-to-value entry point, and adding one for a test's benefit would
    /// put test-only surface on a production type — so the switch lives here.
    /// </summary>
    private static void Restore(MigrationReadinessState state)
    {
        switch (state)
        {
            case MigrationReadinessState.Succeeded: MigrationReadiness.MarkSucceeded(); break;
            case MigrationReadinessState.Failed:    MigrationReadiness.MarkFailed();    break;
            default:                                MigrationReadiness.MarkPending();   break;
        }
    }

    /// <summary>Nothing set: the bootstrap's own defaults are what production runs.</summary>
    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    /// <summary>
    /// Polls rather than sleeping a fixed interval: the first attempt's latency is dominated by EF
    /// model building, which is fast when warm and seconds when cold.
    /// </summary>
    private static async Task<bool> WaitForLogAsync(CapturingLoggerProvider log, string fragment, TimeSpan deadline)
    {
        var expiry = Stopwatch.StartNew();
        while (expiry.Elapsed < deadline)
        {
            if (log.Snapshot().Any(m => m.Contains(fragment, StringComparison.Ordinal)))
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Snapshot()
        {
            lock (_messages) return _messages.ToArray();
        }

        public override string ToString() => Snapshot().Count == 0
            ? "  (nothing logged)"
            : string.Join(Environment.NewLine, Snapshot().Select(m => "  " + m));

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private void Record(string message)
        {
            lock (_messages) _messages.Add(message);
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Record($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
