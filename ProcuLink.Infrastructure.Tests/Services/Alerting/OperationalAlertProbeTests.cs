using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Alerting;

namespace ProcuLink.Infrastructure.Tests.Services.Alerting;

/// <summary>
/// WP-37 — the three alert signals that no existing health surface computed: delivery failure rate,
/// pull-channel freshness, and the AI token-cap latch. Each test seeds real rows and asserts the
/// number the alert sweep will threshold on, so a query that silently stops counting is caught here
/// rather than by a page that never arrives.
/// </summary>
public class OperationalAlertProbeTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    // ── Delivery failure rate ────────────────────────────────────────────────────

    [Fact]
    public async Task DeliveryFailureRate_CountsSuccessesAndFailuresInsideTheWindow()
    {
        await using var db = NewDb();
        var org = await SeedOrgAsync(db);
        SeedAttempts(db, org, DeliveryAttempt.StatusFailed, 3, Now.AddMinutes(-10));
        SeedAttempts(db, org, DeliveryAttempt.StatusSuccess, 1, Now.AddMinutes(-10));
        await db.SaveChangesAsync();

        var signals = await Probe(db).GetSignalsAsync(default);

        signals.DeliveryFailureRate.Attempts.Should().Be(4);
        signals.DeliveryFailureRate.Failures.Should().Be(3);
        signals.DeliveryFailureRate.FailurePercent.Should().Be(75d);
    }

    [Fact]
    public async Task DeliveryFailureRate_IgnoresAttemptsOlderThanTheWindow()
    {
        await using var db = NewDb();
        var org = await SeedOrgAsync(db);
        // Window default is 60 minutes; these are 3 hours old.
        SeedAttempts(db, org, DeliveryAttempt.StatusFailed, 50, Now.AddHours(-3));
        SeedAttempts(db, org, DeliveryAttempt.StatusSuccess, 2, Now.AddMinutes(-5));
        await db.SaveChangesAsync();

        var signals = await Probe(db).GetSignalsAsync(default);

        signals.DeliveryFailureRate.Attempts.Should().Be(2);
        signals.DeliveryFailureRate.Failures.Should().Be(0);
    }

    [Fact]
    public async Task DeliveryFailureRate_ExcludesInFlightAndUnconfirmedAttempts()
    {
        await using var db = NewDb();
        var org = await SeedOrgAsync(db);
        SeedAttempts(db, org, DeliveryAttempt.StatusSuccess,     2, Now.AddMinutes(-5));
        SeedAttempts(db, org, DeliveryAttempt.StatusDispatching, 7, Now.AddMinutes(-5));
        SeedAttempts(db, org, DeliveryAttempt.StatusUnconfirmed, 5, Now.AddMinutes(-5));
        await db.SaveChangesAsync();

        var signals = await Probe(db).GetSignalsAsync(default);

        signals.DeliveryFailureRate.Attempts.Should().Be(2,
            "a send still in flight, or one whose outcome is unknown, is not a failure");
        signals.DeliveryFailureRate.Failures.Should().Be(0);
    }

    [Fact]
    public async Task DeliveryFailureRate_CountsEveryOrg()
    {
        await using var db = NewDb();
        var a = await SeedOrgAsync(db);
        var b = await SeedOrgAsync(db);
        SeedAttempts(db, a, DeliveryAttempt.StatusFailed, 2, Now.AddMinutes(-5));
        SeedAttempts(db, b, DeliveryAttempt.StatusFailed, 3, Now.AddMinutes(-5));
        await db.SaveChangesAsync();

        var signals = await Probe(db).GetSignalsAsync(default);

        signals.DeliveryFailureRate.Failures.Should().Be(5,
            "the operator alert is a system probe, deliberately cross-tenant");
    }

    // ── Pull channels ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PullChannels_ReportsAllThreeChannelsEveryRun()
    {
        await using var db = NewDb();

        var signals = await Probe(db).GetSignalsAsync(default);

        signals.PullChannels.Select(c => c.Channel)
            .Should().BeEquivalentTo(new[] { "email", "sftp", "s3" });
    }

    [Fact]
    public async Task PullChannels_Email_UsesTheNewestSuccessfulPollAcrossEnabledOrgs()
    {
        await using var db = NewDb();
        await SeedEmailOrgAsync(db, enabled: true, lastPolledAt: Now.AddMinutes(-200));
        await SeedEmailOrgAsync(db, enabled: true, lastPolledAt: Now.AddMinutes(-9));

        var email = (await Probe(db).GetSignalsAsync(default)).PullChannels.Single(c => c.Channel == "email");

        email.EnabledOrgs.Should().Be(2);
        email.MinutesSinceLastSuccess.Should().BeApproximately(9d, 0.01);
    }

    [Fact]
    public async Task PullChannels_Email_IgnoresDisabledOrgs()
    {
        await using var db = NewDb();
        await SeedEmailOrgAsync(db, enabled: false, lastPolledAt: Now.AddMinutes(-1));
        await SeedEmailOrgAsync(db, enabled: true,  lastPolledAt: Now.AddMinutes(-500));

        var email = (await Probe(db).GetSignalsAsync(default)).PullChannels.Single(c => c.Channel == "email");

        email.EnabledOrgs.Should().Be(1);
        email.MinutesSinceLastSuccess.Should().BeApproximately(500d, 0.01,
            "a switched-off org's fresh timestamp must not mask a live org's stalled channel");
    }

    [Fact]
    public async Task PullChannels_Email_NeverPolled_ReportsNullRatherThanZeroAge()
    {
        await using var db = NewDb();
        await SeedEmailOrgAsync(db, enabled: true, lastPolledAt: null);

        var email = (await Probe(db).GetSignalsAsync(default)).PullChannels.Single(c => c.Channel == "email");

        email.EnabledOrgs.Should().Be(1);
        email.MinutesSinceLastSuccess.Should().BeNull(
            "a channel configured minutes ago has not polled yet — that is not an incident");
    }

    [Fact]
    public async Task PullChannels_SftpAndS3_CountEnabledConfigsAndUseTheDispatcherExecution()
    {
        await using var db = NewDb();
        var org = await SeedOrgAsync(db);
        db.SftpIngressConfigs.Add(new SftpIngressConfig { Id = Guid.NewGuid(), OrgId = org, IsEnabled = true });
        db.SftpIngressConfigs.Add(new SftpIngressConfig { Id = Guid.NewGuid(), OrgId = org, IsEnabled = false });
        db.S3IngressConfigs.Add(new S3IngressConfig { Id = Guid.NewGuid(), OrgId = org, IsEnabled = true });
        await db.SaveChangesAsync();

        var executions = new FakeRecurringJobExecutions
        {
            ["sftp-polling"] = Now.AddMinutes(-4),
            ["s3-polling"]   = Now.AddMinutes(-120),
        };

        var channels = (await Probe(db, executions: executions).GetSignalsAsync(default)).PullChannels;

        var sftp = channels.Single(c => c.Channel == "sftp");
        sftp.EnabledOrgs.Should().Be(1);
        sftp.MinutesSinceLastSuccess.Should().BeApproximately(4d, 0.01);

        var s3 = channels.Single(c => c.Channel == "s3");
        s3.EnabledOrgs.Should().Be(1);
        s3.MinutesSinceLastSuccess.Should().BeApproximately(120d, 0.01);
    }

    [Fact]
    public async Task PullChannels_NoConfigs_ReportZeroEnabledOrgs()
    {
        await using var db = NewDb();

        var channels = (await Probe(db).GetSignalsAsync(default)).PullChannels;

        channels.Should().OnlyContain(c => c.EnabledOrgs == 0);
    }

    // ── AI token-cap latch ───────────────────────────────────────────────────────

    [Fact]
    public async Task AiTokenLatch_CountsGoodStandingOrgsAtOrOverTheirLimit()
    {
        await using var db = NewDb();
        var latched = await SeedOrgAsync(db, plan: PlanConstants.Growth, status: AccountStatusConstants.Active);
        var fine    = await SeedOrgAsync(db, plan: PlanConstants.Growth, status: AccountStatusConstants.Active);
        SeedUsage(db, latched, 5);
        SeedUsage(db, fine, 5);
        await db.SaveChangesAsync();

        var tracker = new FakeAiUsageTracker { AtOrOverLimit = { latched } };

        var signals = await Probe(db, tracker: tracker).GetSignalsAsync(default);

        signals.AiTokenLatch.LatchedOrgs.Should().Be(1);
    }

    [Fact]
    public async Task AiTokenLatch_ExcludesDelinquentOrgs()
    {
        await using var db = NewDb();
        var readOnly = await SeedOrgAsync(db, plan: PlanConstants.Growth, status: AccountStatusConstants.ReadOnly);
        SeedUsage(db, readOnly, 5);
        await db.SaveChangesAsync();

        var tracker = new FakeAiUsageTracker { AtOrOverLimit = { readOnly } };

        var signals = await Probe(db, tracker: tracker).GetSignalsAsync(default);

        signals.AiTokenLatch.LatchedOrgs.Should().Be(0,
            "a read-only org's AI budget is clamped deliberately by billing — it is not an incident");
        tracker.Queried.Should().NotContain(readOnly,
            "a delinquent org must be filtered out before the limit is even resolved");
    }

    [Fact]
    public async Task AiTokenLatch_IgnoresOrgsWithNoUsageThisMonth()
    {
        await using var db = NewDb();
        var idle = await SeedOrgAsync(db, plan: PlanConstants.Growth, status: AccountStatusConstants.Active);
        SeedUsage(db, idle, tokens: 0);
        // Plus a prior-month row that must not resurrect the org into this month's candidate set.
        var prior = Now.AddMonths(-1);
        db.AiUsageMonthly.Add(new AiUsageMonthly
        {
            OrgId = idle, Year = prior.Year, Month = prior.Month, TokensUsed = 999_999,
            UpdatedAt = prior,
        });
        await db.SaveChangesAsync();

        var tracker = new FakeAiUsageTracker { AtOrOverLimit = { idle } };

        var signals = await Probe(db, tracker: tracker).GetSignalsAsync(default);

        signals.AiTokenLatch.LatchedOrgs.Should().Be(0,
            "an org that spent no tokens this month cannot have latched this month's cap");
    }

    [Fact]
    public async Task AiTokenLatch_NoUsageRowsAtAll_IsZero()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);

        var signals = await Probe(db, tracker: new FakeAiUsageTracker()).GetSignalsAsync(default);

        signals.AiTokenLatch.LatchedOrgs.Should().Be(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OperationalAlertProbe Probe(
        ProcuLinkDbContext db,
        IAiUsageTracker? tracker = null,
        IRecurringJobLastExecutionSource? executions = null,
        WorkerHealthAlertOptions? options = null) =>
        new(db,
            tracker ?? new FakeAiUsageTracker(),
            executions ?? new FakeRecurringJobExecutions(),
            options ?? new WorkerHealthAlertOptions(),
            NullLogger<OperationalAlertProbe>.Instance,
            () => Now);

    private static async Task<Guid> SeedOrgAsync(
        ProcuLinkDbContext db, string plan = PlanConstants.Growth, string status = AccountStatusConstants.Active)
    {
        var id = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = id, Name = $"Org {id:N}", Plan = plan, AccountStatus = status,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedEmailOrgAsync(
        ProcuLinkDbContext db, bool enabled, DateTime? lastPolledAt)
    {
        var id = Guid.NewGuid();
        var cfg = EmailPollingConfigJson(enabled, lastPolledAt);
        db.Organisations.Add(new Organisation
        {
            Id = id, Name = $"Org {id:N}",
            Plan = PlanConstants.Growth, AccountStatus = AccountStatusConstants.Active,
            EmailPollingEnabled = enabled, EmailConfigJson = cfg,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// Writes the same camelCase shape <c>EmailPollingConfig</c> serialises, so the probe is
    /// exercised against the real on-disk format rather than a convenient one.
    /// </summary>
    private static string EmailPollingConfigJson(bool enabled, DateTime? lastPolledAt) =>
        JsonSerializer.Serialize(new
        {
            enabled,
            host = "imap.example.com",
            port = 993,
            useSsl = true,
            username = "po@example.com",
            folder = "INBOX",
            defaultSupplierId = (Guid?)null,
            passwordCiphertext = (string?)null,
            lastPolledAt,
            updatedAt = (DateTime?)null,
        });

    private static void SeedAttempts(
        ProcuLinkDbContext db, Guid orgId, string status, int count, DateTime attemptedAt)
    {
        for (var i = 0; i < count; i++)
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrgId = orgId, OrderId = Guid.NewGuid(),
                Channel = "http", Destination = "https://supplier.example/po",
                Status = status, AttemptedAt = attemptedAt, AttemptNumber = 1,
            });
        }
    }

    private static void SeedUsage(ProcuLinkDbContext db, Guid orgId, long tokens) =>
        db.AiUsageMonthly.Add(new AiUsageMonthly
        {
            OrgId = orgId, Year = Now.Year, Month = Now.Month,
            TokensUsed = tokens, UpdatedAt = Now,
        });

    private sealed class FakeAiUsageTracker : IAiUsageTracker
    {
        public HashSet<Guid> AtOrOverLimit { get; } = new();
        public List<Guid> Queried { get; } = new();

        public Task<bool> IsAtOrOverLimitAsync(Guid organisationId, CancellationToken ct = default)
        {
            Queried.Add(organisationId);
            return Task.FromResult(AtOrOverLimit.Contains(organisationId));
        }

        public Task IncrementAsync(Guid organisationId, long tokens, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<AiUsageSnapshot> GetCurrentAsync(Guid organisationId, CancellationToken ct = default) =>
            Task.FromResult(new AiUsageSnapshot(organisationId, Now.Year, Now.Month, 0, 0));
    }

    private sealed class FakeRecurringJobExecutions : IRecurringJobLastExecutionSource
    {
        private readonly Dictionary<string, DateTime?> _byId = new();
        public DateTime? this[string id] { set => _byId[id] = value; }
        public DateTime? GetLastExecutionUtc(string recurringJobId) =>
            _byId.TryGetValue(recurringJobId, out var at) ? at : null;
    }
}
