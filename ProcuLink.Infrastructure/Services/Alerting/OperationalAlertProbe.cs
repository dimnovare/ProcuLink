using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Alerting;

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
    private readonly ProcuLinkDbContext _db;
    private readonly IAiUsageTracker _aiUsage;
    private readonly WorkerHealthAlertOptions _options;
    private readonly ILogger<OperationalAlertProbe> _logger;
    private readonly Func<DateTime> _utcNow;

    public OperationalAlertProbe(
        ProcuLinkDbContext db,
        IAiUsageTracker aiUsage,
        WorkerHealthAlertOptions options,
        ILogger<OperationalAlertProbe> logger,
        Func<DateTime>? utcNow = null)
    {
        _db = db;
        _aiUsage = aiUsage;
        _options = options;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<OperationalAlertSignals> GetSignalsAsync(CancellationToken ct)
    {
        var now = _utcNow();

        return new OperationalAlertSignals(
            await ReadDeliveryFailureRateAsync(now, ct),
            // The pull channels (IMAP / SFTP / S3 for orders) are retired; flow pickups are not watched here.
            Array.Empty<PullChannelSignal>(),
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
