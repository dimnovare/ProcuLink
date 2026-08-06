using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services.Alerting;

/// <summary>
/// Reads the three alert signals no existing health surface computed. EF Core only, no raw SQL.
/// <para>
/// EVERY query here is deliberately CROSS-TENANT — deliberately NOT scoped by <c>org_id</c>. This is
/// a system health probe for the single operator, exactly like
/// <c>OpsHealthService.GetWorkerHealthSnapshotAsync</c>: "is the product delivering at all", not
/// "what does tenant X see". Nothing it returns is ever rendered on an org-scoped API surface — the
/// only consumer is <c>WorkerHealthAlertService</c>, whose output goes to the operator's alert
/// sinks. It returns COUNTS and AGES only, never a tenant's identifiers, names or content, so a
/// cross-tenant read here cannot leak one customer's data to another.
/// </para>
/// </summary>
public sealed class OperationalAlertProbe : IOperationalAlertProbe
{
    /// <summary>Operator-facing pull-channel names. Also the ids used in alert text.</summary>
    public const string EmailChannel = "email";
    public const string SftpChannel  = "sftp";
    public const string S3Channel    = "s3";

    /// <summary>
    /// Recurring-job ids registered in <c>ProcuLink.Worker.Worker.StartAsync</c>. Used as the
    /// migration-free last-poll evidence for the two channels that persist none of their own.
    /// </summary>
    public const string SftpPollingJobId = "sftp-polling";
    public const string S3PollingJobId   = "s3-polling";

    private readonly ProcuLinkDbContext _db;
    private readonly IAiUsageTracker _aiUsage;
    private readonly IRecurringJobLastExecutionSource _recurringJobs;
    private readonly WorkerHealthAlertOptions _options;
    private readonly ILogger<OperationalAlertProbe> _logger;
    private readonly Func<DateTime> _utcNow;

    public OperationalAlertProbe(
        ProcuLinkDbContext db,
        IAiUsageTracker aiUsage,
        IRecurringJobLastExecutionSource recurringJobs,
        WorkerHealthAlertOptions options,
        ILogger<OperationalAlertProbe> logger,
        Func<DateTime>? utcNow = null)
    {
        _db = db;
        _aiUsage = aiUsage;
        _recurringJobs = recurringJobs;
        _options = options;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<OperationalAlertSignals> GetSignalsAsync(CancellationToken ct)
    {
        var now = _utcNow();

        return new OperationalAlertSignals(
            await ReadDeliveryFailureRateAsync(now, ct),
            await ReadPullChannelsAsync(now, ct),
            await ReadAiTokenLatchAsync(now, ct));
    }

    // ── Delivery failure rate ────────────────────────────────────────────────────

    /// <summary>
    /// Concluded delivery attempts in the trailing window, all orgs. <c>dispatching</c> (in flight)
    /// and <c>unconfirmed</c> (outcome lost to a crash, parked for a human) are excluded so a send
    /// that has not resolved yet is never scored as a failure.
    /// </summary>
    private async Task<DeliveryFailureRateSignal> ReadDeliveryFailureRateAsync(
        DateTime now, CancellationToken ct)
    {
        var window = _options.EffectiveDeliveryFailureWindowMinutes;
        var since  = now.AddMinutes(-window);

        var concluded = _db.DeliveryAttempts
            .AsNoTracking()
            .Where(a => a.AttemptedAt >= since
                     && (a.Status == DeliveryAttempt.StatusSuccess
                      || a.Status == DeliveryAttempt.StatusFailed));

        var attempts = await concluded.CountAsync(ct);
        var failures = await concluded.CountAsync(a => a.Status == DeliveryAttempt.StatusFailed, ct);

        return new DeliveryFailureRateSignal(window, attempts, failures);
    }

    // ── Pull channels ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<PullChannelSignal>> ReadPullChannelsAsync(
        DateTime now, CancellationToken ct)
    {
        return new[]
        {
            await ReadEmailChannelAsync(now, ct),
            await ReadDispatcherBackedChannelAsync(
                SftpChannel, SftpPollingJobId,
                await _db.SftpIngressConfigs.AsNoTracking().CountAsync(c => c.IsEnabled, ct),
                now),
            await ReadDispatcherBackedChannelAsync(
                S3Channel, S3PollingJobId,
                await _db.S3IngressConfigs.AsNoTracking().CountAsync(c => c.IsEnabled, ct),
                now),
        };
    }

    /// <summary>
    /// IMAP is the one pull channel with a genuine persisted success stamp:
    /// <c>EmailPollingConfig.LastPolledAt</c>, written by <c>EmailPollOrgJob</c> only after a
    /// successful disconnect. The freshest stamp across ENABLED orgs is used — a disabled org's
    /// fresh timestamp must never mask a live org's stalled channel.
    /// <para>
    /// The consequence, stated plainly because it limits what an alert off this can claim: taking
    /// the NEWEST makes this a CHANNEL-level signal ("is anyone still polling successfully"), so one
    /// org with broken IMAP credentials among several healthy ones will not raise it. That is the
    /// deliberate trade — the alternative (oldest) pages the operator over a single customer's
    /// misconfiguration, which is the false-page shape this whole design avoids.
    /// </para>
    /// </summary>
    private async Task<PullChannelSignal> ReadEmailChannelAsync(DateTime now, CancellationToken ct)
    {
        // The stamp lives inside a JSON blob, so the parse happens in memory. Only the enabled orgs'
        // blobs are fetched, which is the same candidate set EmailPollingJob dispatches over.
        var configs = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.EmailPollingEnabled)
            .Select(o => o.EmailConfigJson)
            .ToListAsync(ct);

        DateTime? newest = null;
        foreach (var json in configs)
        {
            var polledAt = SafeLastPolledAt(json);
            if (polledAt is { } at && (newest is null || at > newest))
                newest = at;
        }

        return new PullChannelSignal(EmailChannel, configs.Count, MinutesSince(newest, now));
    }

    /// <summary>
    /// SFTP and S3 persist no last-successful-poll timestamp, and adding one is a schema change.
    /// The recurring dispatcher's own last execution is the migration-free stand-in: it proves the
    /// channel is still being polled at all, which is the "dead pull channel" case. It does NOT
    /// prove one org's credentials still work — that per-org symptom is out of this probe's reach
    /// and is called out in the monitoring runbook.
    /// </summary>
    private Task<PullChannelSignal> ReadDispatcherBackedChannelAsync(
        string channel, string recurringJobId, int enabledOrgs, DateTime now)
    {
        DateTime? lastRun;
        try
        {
            lastRun = _recurringJobs.GetLastExecutionUtc(recurringJobId);
        }
        catch (Exception ex)
        {
            // Treated as "unknown", never as "stalled": an unreadable scheduler must not page.
            _logger.LogWarning(ex,
                "OperationalAlertProbe: could not read last execution of recurring job {JobId}.",
                recurringJobId);
            lastRun = null;
        }

        return Task.FromResult(new PullChannelSignal(channel, enabledOrgs, MinutesSince(lastRun, now)));
    }

    /// <summary>
    /// Parses <c>EmailPollingConfig</c> out of the org's stored JSON, reusing the same deserialiser
    /// the settings service writes with. A malformed blob yields null (unknown), never a throw —
    /// one corrupt org row must not blind the whole sweep.
    /// </summary>
    private DateTime? SafeLastPolledAt(string? json)
    {
        try
        {
            return EmailPollingConfig.FromJson(json).LastPolledAt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OperationalAlertProbe: unreadable email polling config; ignoring it.");
            return null;
        }
    }

    /// <summary>
    /// Age in minutes, or null when the timestamp is unknown. A Local-kind value is CONVERTED
    /// (not relabelled), and an Unspecified one is read as UTC — everything in this repo stamps
    /// <c>DateTime.UtcNow</c>, and a silent timezone shift here would move an age by hours and so
    /// either invent a stall or hide one.
    /// </summary>
    private static double? MinutesSince(DateTime? at, DateTime now)
    {
        if (at is not { } value)
            return null;

        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return (now - utc).TotalMinutes;
    }

    // ── AI token-cap latch ───────────────────────────────────────────────────────

    /// <summary>
    /// Counts orgs whose shared monthly OpenAI budget is exhausted, which silently degrades every
    /// PDF upload for that org to the regex fallback.
    /// <para>
    /// Two deliberate narrowings keep this from becoming a permanent false page:
    /// only orgs that actually SPENT tokens this month are candidates (an idle org and a
    /// zero-budget org would otherwise both satisfy <c>0 &gt;= 0</c>), and delinquent orgs are
    /// excluded because their budget is clamped on purpose by the billing rules — reporting them
    /// would be a page nobody can action.
    /// </para>
    /// The verdict itself comes from <see cref="IAiUsageTracker.IsAtOrOverLimitAsync"/>, i.e. the
    /// exact predicate production uses, so the config override and the delinquency clamp are
    /// honoured without re-deriving them here.
    /// </summary>
    private async Task<AiTokenLatchSignal> ReadAiTokenLatchAsync(DateTime now, CancellationToken ct)
    {
        var spenders = await _db.AiUsageMonthly
            .AsNoTracking()
            .Where(u => u.Year == now.Year && u.Month == now.Month && u.TokensUsed > 0)
            .Select(u => u.OrgId)
            .Distinct()
            .ToListAsync(ct);

        if (spenders.Count == 0)
            return new AiTokenLatchSignal(0);

        // AccountStatusConstants.IsReadOnly is a method, so the delinquency filter runs in memory
        // over the (small) candidate set rather than being pushed into SQL.
        var statuses = await _db.Organisations
            .AsNoTracking()
            .Where(o => spenders.Contains(o.Id))
            .Select(o => new { o.Id, o.AccountStatus })
            .ToListAsync(ct);

        var latched = 0;
        foreach (var org in statuses)
        {
            if (AccountStatusConstants.IsReadOnly(org.AccountStatus))
                continue;

            if (await _aiUsage.IsAtOrOverLimitAsync(org.Id, ct))
                latched++;
        }

        return new AiTokenLatchSignal(latched);
    }
}
