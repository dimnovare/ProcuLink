using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Alerting;
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
/// <para><b>And the host really registers it.</b> The graph test MIRRORS the Worker's registrations,
/// so on its own it proves the graph constructs, not that <c>ProcuLink.Worker/Program.cs</c> wires
/// it — the same distinction <c>PushIngressSeamRegistrationTests</c> exists for. The source guard
/// below closes that half, including the position check against <c>builder.Build()</c>.</para>
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

    [Theory]
    [InlineData(@"AddScoped\s*<\s*IOperationalAlertProbe\s*,\s*OperationalAlertProbe\s*>")]
    [InlineData(@"AddSingleton\s*<\s*IRecurringJobLastExecutionSource\s*,\s*HangfireRecurringJobLastExecutionSource\s*>")]
    [InlineData(@"AddScoped\s*<\s*EmailWorkerAlertSink\s*>")]
    [InlineData(@"AddScoped\s*<\s*IWorkerAlertSink\s*>\s*\(\s*sp\s*=>\s*new\s+CompositeWorkerAlertSink")]
    [InlineData(@"AlertingEmailOptions\.SectionName")]
    public void Worker_registersTheAlertingGraph_beforeBuild(string pattern)
    {
        var program = WorkerProgramSource();

        var match = Regex.Match(program, pattern);
        Assert.True(match.Success,
            $"ProcuLink.Worker/Program.cs must contain a registration matching /{pattern}/. " +
            "Without it the alert condition it feeds is silently never evaluated in production.");

        // Position matters: builder.Build() snapshots the service collection, so a registration
        // written after it compiles, reads correctly, and is never seen by the built provider.
        var build = Regex.Match(program, @"\bbuilder\s*\.\s*Build\s*\(\s*\)");
        Assert.True(build.Success, "ProcuLink.Worker/Program.cs must call builder.Build().");
        Assert.True(match.Index < build.Index,
            $"registration /{pattern}/ is at offset {match.Index}, AFTER builder.Build() at {build.Index}.");
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
    /// Mirrors the Worker's alerting registrations (see the source guard above for the half this
    /// cannot prove). Everything from the job down is the production type; only the email transport,
    /// the health snapshot and the AI-limit verdict are doubles.
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

        services.AddSingleton<WorkerHealthAlertState>();
        services.AddSingleton(new WorkerHealthAlertOptions());
        services.AddSingleton(new AlertingEmailOptions { To = alertEmailTo });
        services.AddSingleton<IRecurringJobLastExecutionSource, NullRecurringJobLastExecutionSource>();

        services.AddScoped<IOperationalAlertProbe, OperationalAlertProbe>();
        services.AddScoped<EmailWorkerAlertSink>();
        services.AddScoped<IWorkerAlertSink>(sp => new CompositeWorkerAlertSink(
            new IWorkerAlertSink[] { sp.GetRequiredService<EmailWorkerAlertSink>() },
            sp.GetRequiredService<ILogger<CompositeWorkerAlertSink>>()));
        services.AddScoped<IWorkerHealthAlertService, WorkerHealthAlertService>();
        services.AddScoped<WorkerHealthAlertJob>();

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
