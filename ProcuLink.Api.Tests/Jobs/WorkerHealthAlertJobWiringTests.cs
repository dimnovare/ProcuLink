using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Alerting;
using ProcuLink.Worker;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// WP-37 — proves the alert path is CONNECTED, not merely present.
///
/// <para><b>The hole this closes.</b> Unit tests pin each piece against a mock: the service against
/// a fake sink, the probe against a fake tracker, the sink against a fake email client. Every one of
/// them stays green if nothing ever calls the next thing along. These tests drive the real Hangfire
/// job entry point through a real object graph — job → service → probe → per-condition cooldown →
/// composite sink → email sink — and assert on the outbound message that Postmark would have been
/// handed. Only the email transport itself is a double.</para>
///
/// <para><b>And the host really registers it.</b> Every test here resolves the PRODUCTION
/// registration seam, <c>WorkerAlertingRegistration.AddWorkerAlerting</c>, which is the single call
/// <c>ProcuLink.Worker/Program.cs</c> makes. It used to mirror the Worker's registrations in a
/// private helper and assert on <c>Program.cs</c> source with a regex — and that combination was
/// blind: deleting <c>EmailWorkerAlertSink</c> from the real composite left the regex matching (it
/// stopped at the constructor name), left the sibling <c>AddScoped&lt;EmailWorkerAlertSink&gt;</c>
/// pattern matching, and left the graph test green because its own copy of the composite still had
/// the sink. Every test passed while the email routing this packet exists to deliver was gone.</para>
///
/// <para>A test that reads source with a regex is what let that through. These resolve a real
/// container and inspect what it actually built. The one remaining source assertion is the single
/// <c>AddWorkerAlerting</c> call site and its position relative to <c>builder.Build()</c> — the one
/// thing a container cannot prove about a host it does not run.</para>
/// </summary>
public sealed class WorkerHealthAlertJobWiringTests
{
    [Fact]
    public async Task Job_deliversAWorkerDownAlertAllTheWayToTheOutboundEmail()
    {
        var mail = new RecordingEmailApiClient();
        await using var provider = BuildAlertingGraph(
            mail,
            health: new StubOpsHealth(new WorkerHealthSnapshot(
                WorkerHealthy: false, ActiveWorkers: 0, SecondsSinceWorkerHeartbeat: 900,
                DeadLetterOrders: 0, FailedDeliveryOrders: 0)));

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkerHealthAlertJob>().ExecuteAsync(default);

        mail.Sent.Should().ContainSingle("the job must reach the transport, not merely decide to");
        mail.Sent[0].To.Should().Equal("founder@example.com");
        mail.Sent[0].Subject.Should().Contain(OperationalAlertKeys.WorkerHeartbeatLost);
        mail.Sent[0].TextBody.Should().Contain("no healthy worker");
    }

    [Fact]
    public async Task Job_deliversAProbeDrivenAlertFromRealRowsInTheDatabase()
    {
        var mail = new RecordingEmailApiClient();
        var orgId = Guid.NewGuid();

        await using var provider = BuildAlertingGraph(
            mail,
            health: new StubOpsHealth(Healthy()),
            aiUsage: new StubAiUsageTracker(atOrOverLimit: orgId));

        // Real rows, read by the real OperationalAlertProbe inside the real service.
        using (var seed = provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Organisations.Add(new Organisation
            {
                Id = orgId, Name = "Wired Org",
                Plan = PlanConstants.Growth, AccountStatus = AccountStatusConstants.Active,
            });
            db.AiUsageMonthly.Add(new AiUsageMonthly
            {
                OrgId = orgId, Year = DateTime.UtcNow.Year, Month = DateTime.UtcNow.Month,
                TokensUsed = 1_000_000, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkerHealthAlertJob>().ExecuteAsync(default);

        mail.Sent.Should().ContainSingle();
        mail.Sent[0].Subject.Should().Contain(OperationalAlertKeys.AiTokenCapLatched,
            "the probe's finding must reach the sink through the service, not stop at the probe");
    }

    [Fact]
    public async Task Job_onAHealthySystem_sendsNothing()
    {
        var mail = new RecordingEmailApiClient();
        await using var provider = BuildAlertingGraph(mail, new StubOpsHealth(Healthy()));

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkerHealthAlertJob>().ExecuteAsync(default);

        mail.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Job_withNoAlertDestinationConfigured_runsCleanlyAndSendsNothing()
    {
        var mail = new RecordingEmailApiClient();
        await using var provider = BuildAlertingGraph(
            mail,
            health: new StubOpsHealth(new WorkerHealthSnapshot(false, 0, 900, 0, 0)),
            alertEmailTo: null);

        using var scope = provider.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<WorkerHealthAlertJob>();

        var act = async () => await job.ExecuteAsync(default);

        await act.Should().NotThrowAsync(
            "an unconfigured alerting destination must never take the Worker down");
        mail.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// The whole production graph with NO destination behind it — no recipient, and a Sentry SDK
    /// that an empty DSN left disabled. The sweep must report that it raised nothing, because
    /// nothing was received. Startup validation refuses this configuration in Production; this pins
    /// that the runtime is honest about it wherever it does occur.
    /// </summary>
    [Fact]
    public async Task Sweep_withNoDestinationBehindAnyTransport_reportsThatNoAlertWasRaised()
    {
        await using var provider = BuildAlertingGraph(
            new RecordingEmailApiClient(),
            health: new StubOpsHealth(new WorkerHealthSnapshot(false, 0, 900, 0, 0)),
            alertEmailTo: null);

        using var scope = provider.CreateScope();
        var raised = await scope.ServiceProvider
            .GetRequiredService<IWorkerHealthAlertService>().RunAsync(default);

        raised.Should().BeFalse(
            "the worker IS down, but no transport could tell anyone — 'raised an alert' must mean "
          + "an operator was reachable, not that the code path executed");
    }

    [Fact]
    public async Task Job_isIdempotent_repeatRunsInsideTheCooldownDoNotResend()
    {
        var mail = new RecordingEmailApiClient();
        await using var provider = BuildAlertingGraph(
            mail, new StubOpsHealth(new WorkerHealthSnapshot(false, 0, 900, 0, 0)));

        for (var i = 0; i < 3; i++)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkerHealthAlertJob>().ExecuteAsync(default);
        }

        mail.Sent.Should().ContainSingle(
            "the recurring job runs every 5 minutes — re-running it must not re-page");
    }

    // ── The host really registers all of this ────────────────────────────────────

    /// <summary>
    /// THE mutation guard. Every alert this packet raises leaves the process through the composite's
    /// transports, so a transport dropped from that argument list is the single edit that turns a
    /// working alarm into a silent one while changing nothing an operator can see.
    /// <para>
    /// Asserted on the CONSTRUCTED composite, resolved from the production registration: deleting
    /// <c>sp.GetRequiredService&lt;EmailWorkerAlertSink&gt;()</c> from
    /// <see cref="WorkerAlertingRegistration.AddWorkerAlerting"/> fails here and in
    /// <see cref="Job_deliversAWorkerDownAlertAllTheWayToTheOutboundEmail"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void Worker_composesEveryAlertTransport()
    {
        using var provider = BuildAlertingGraph(new RecordingEmailApiClient(), new StubOpsHealth(Healthy()));
        using var scope = provider.CreateScope();

        var sink = scope.ServiceProvider.GetRequiredService<IWorkerAlertSink>();

        sink.Should().BeOfType<CompositeWorkerAlertSink>();
        ((CompositeWorkerAlertSink)sink).Sinks.Select(s => s.GetType()).Should().BeEquivalentTo(
            new[] { typeof(SentryWorkerAlertSink), typeof(EmailWorkerAlertSink) },
            "an alert that reaches only one transport is one outage away from reaching none");
    }

    [Theory]
    [InlineData(typeof(IOperationalAlertProbe), typeof(OperationalAlertProbe))]
    [InlineData(typeof(IRecurringJobLastExecutionSource), typeof(HangfireRecurringJobLastExecutionSource))]
    [InlineData(typeof(IWorkerHealthAlertService), typeof(WorkerHealthAlertService))]
    public void Worker_resolvesEachAlertingComponent_asTheProductionType(Type contract, Type expected)
    {
        using var provider = BuildAlertingGraph(new RecordingEmailApiClient(), new StubOpsHealth(Healthy()));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(contract).Should().BeOfType(expected,
            "without it the alert conditions this component feeds are never evaluated in production");
    }

    [Fact]
    public void Worker_bindsTheAlertEmailDestinationFromConfiguration()
    {
        using var provider = BuildAlertingGraph(
            new RecordingEmailApiClient(), new StubOpsHealth(Healthy()),
            alertEmailTo: "founder@example.com");

        provider.GetRequiredService<AlertingEmailOptions>().Recipients
            .Should().Equal(new[] { "founder@example.com" },
                "an unbound Alerting:Email section makes every email alert a silent no-op");
    }

    /// <summary>
    /// The one thing a container cannot prove: that the host calls the seam at all, and calls it
    /// before <c>builder.Build()</c> snapshots the service collection. Narrow on purpose — it is a
    /// single call site, not five patterns each of which could match while meaning nothing.
    /// </summary>
    [Fact]
    public void Worker_callsTheAlertingRegistration_beforeBuild()
    {
        var program = WorkerProgramSource();

        var match = Regex.Match(program, @"\bAddWorkerAlerting\s*\(");
        Assert.True(match.Success,
            "ProcuLink.Worker/Program.cs must call services.AddWorkerAlerting(...). Without it the "
          + "entire alert sweep is absent from production while the Worker still starts and looks healthy.");

        var build = Regex.Match(program, @"\bbuilder\s*\.\s*Build\s*\(\s*\)");
        Assert.True(build.Success, "ProcuLink.Worker/Program.cs must call builder.Build().");
        Assert.True(match.Index < build.Index,
            $"AddWorkerAlerting is at offset {match.Index}, AFTER builder.Build() at {build.Index}. "
          + "Build() snapshots the service collection, so a later registration is never seen.");
    }

    /// <summary>
    /// The Hangfire-backed source takes an OPTIONAL <c>JobStorage</c>. Whether <c>AddHangfire</c>
    /// puts that type in the container is a packaging detail of Hangfire, and if this component
    /// cannot be constructed the probe cannot be constructed either — which takes the ENTIRE alert
    /// sweep down, all five conditions, on a Worker that still starts and looks healthy. So the
    /// registration shape is asserted, not assumed.
    /// </summary>
    [Fact]
    public void RecurringJobSource_resolvesWithNoJobStorageInTheContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRecurringJobLastExecutionSource, HangfireRecurringJobLastExecutionSource>();

        using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IRecurringJobLastExecutionSource>();
        source.Should().BeOfType<HangfireRecurringJobLastExecutionSource>();

        // And the contract holds with no scheduler storage available: unknown, never a throw and
        // never a fabricated timestamp that would read as a fresh poll.
        source.GetLastExecutionUtc("sftp-polling").Should().BeNull();
    }

    [Fact]
    public void Worker_stillSchedulesTheAlertSweep()
    {
        var worker = File.ReadAllText(Path.Combine(RepoRoot(), "ProcuLink.Worker", "Worker.cs"));

        worker.Should().MatchRegex(
            @"AddOrUpdate\s*<\s*WorkerHealthAlertJob\s*>\s*\(\s*""worker-health-alert""",
            "every condition in this packet is evaluated by that one recurring sweep — unscheduled, "
          + "nothing is monitored at all");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static WorkerHealthSnapshot Healthy() => new(true, 2, 3, 0, 0);

    /// <summary>
    /// Builds the graph through the PRODUCTION registration seam — the same one line
    /// <c>Program.cs</c> calls. Nothing about the alerting graph is restated here, so this helper
    /// cannot silently disagree with the host the way its predecessor did.
    /// <para>
    /// Only the inputs the graph reads are doubles: the email transport, the health snapshot and
    /// the AI-limit verdict. Everything from the job down — probe, cooldown state, composite, email
    /// sink — is the production type built by the production code.
    /// </para>
    /// </summary>
    private static ServiceProvider BuildAlertingGraph(
        IEmailApiClient mail,
        IOpsHealthService health,
        IAiUsageTracker? aiUsage = null,
        string? alertEmailTo = "founder@example.com")
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<ProcuLinkDbContext>(o => o.UseInMemoryDatabase(dbName));

        services.AddScoped(_ => health);
        services.AddScoped(_ => aiUsage ?? new StubAiUsageTracker());
        services.AddScoped(_ => mail);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(alertEmailTo is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["Alerting:Email:To"] = alertEmailTo })
            .Build();

        services.AddWorkerAlerting(configuration);

        return services.BuildServiceProvider();
    }

    private static string WorkerProgramSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ProcuLink.Worker", "Program.cs"));

    /// <summary>
    /// Walks up from the test binary until the solution file is found, so the guards read the real
    /// checked-in host source rather than a fixture.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not locate the repository root (ProcuLink.slnx).");
        return dir!.FullName;
    }

    private sealed class RecordingEmailApiClient : IEmailApiClient
    {
        public List<EmailApiMessage> Sent { get; } = new();
        public bool IsConfigured => true;
        public string DefaultFrom => "orders@proculink.eu";

        public Task<EmailApiResult> SendAsync(EmailApiMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(new EmailApiResult(true, null, 200));
        }
    }

    private sealed class StubOpsHealth : IOpsHealthService
    {
        private readonly WorkerHealthSnapshot _snapshot;
        public StubOpsHealth(WorkerHealthSnapshot snapshot) => _snapshot = snapshot;

        public TimeSpan StuckThreshold => TimeSpan.FromMinutes(30);

        public Task<WorkerHealthSnapshot> GetWorkerHealthSnapshotAsync(CancellationToken ct) =>
            Task.FromResult(_snapshot);

        public Task<OpsHealthSummary> GetHealthAsync(Guid organisationId, CancellationToken ct) =>
            throw new NotSupportedException("not used by the alert sweep");

        public Task<IReadOnlyList<DeadLetterOrder>> ListDeadLetterAsync(
            Guid organisationId, bool includeFailed, CancellationToken ct) =>
            throw new NotSupportedException("not used by the alert sweep");
    }

    private sealed class StubAiUsageTracker : IAiUsageTracker
    {
        private readonly HashSet<Guid> _latched;
        public StubAiUsageTracker(params Guid[] atOrOverLimit) => _latched = [.. atOrOverLimit];

        public Task<bool> IsAtOrOverLimitAsync(Guid organisationId, CancellationToken ct = default) =>
            Task.FromResult(_latched.Contains(organisationId));

        public Task IncrementAsync(Guid organisationId, long tokens, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<AiUsageSnapshot> GetCurrentAsync(Guid organisationId, CancellationToken ct = default) =>
            Task.FromResult(new AiUsageSnapshot(organisationId, DateTime.UtcNow.Year, DateTime.UtcNow.Month, 0, 0));
    }
}
