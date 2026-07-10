using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring sweep (daily 02:00 UTC): reconciles every org that has a Stripe subscription id
/// against Stripe as source of truth via <see cref="IBillingReconciliationService"/>. The safety
/// net for missed <c>customer.subscription.*</c> webhooks and stale/test-mode subscription ids.
///
/// <para>Per-org try/catch isolates one org's failure from the rest. Idempotent — the underlying
/// service converges (re-writing the same derived state) and a downgraded org has its subscription
/// id cleared, so it drops out of this sweep's predicate on the next run.</para>
///
/// <para><b>Mass-downgrade circuit breaker.</b> Before reconciling, the sweep counts orgs that are
/// simultaneously PAST the missing-grace window. A healthy production sees at most a trickle of
/// genuine cancellations; a BATCH signals a systemic fault — most dangerously a persistent
/// valid-but-wrong Stripe key (wrong live/test mode or wrong connected account) that authenticates
/// but 404s every live subscription, which would otherwise downgrade the entire paying base ~3 days
/// later. When that count exceeds the threshold the whole sweep aborts (downgrades none) and alerts.</para>
/// </summary>
public sealed class BillingReconciliationJob
{
    /// <summary>Default cap on orgs allowed to be past-grace in one run before the sweep aborts as a suspected systemic fault.</summary>
    private const int DefaultMassDowngradeThreshold = 10;

    private readonly ProcuLinkDbContext _db;
    private readonly IBillingReconciliationService _reconciliation;
    private readonly IConfiguration _config;
    private readonly ILogger<BillingReconciliationJob> _logger;

    public BillingReconciliationJob(
        ProcuLinkDbContext db,
        IBillingReconciliationService reconciliation,
        IConfiguration config,
        ILogger<BillingReconciliationJob> logger)
    {
        _db = db;
        _reconciliation = reconciliation;
        _config = config;
        _logger = logger;
    }

    // DisableConcurrentExecution: a second overlapping sweep on the same data could double-downgrade
    // and double-emit billing_cancelled (there is no optimistic-concurrency token on Organisation).
    // A daily sweep should never overlap; the mutex is cheap insurance against a manual re-trigger.
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        // ── Mass-downgrade circuit breaker ───────────────────────────────────
        var now = DateTime.UtcNow;
        var pastGraceCount = await _db.Organisations
            .AsNoTracking()
            .CountAsync(o => o.StripeReconciliationMissingSince != null
                          && o.StripeReconciliationMissingSince <= now - StripeSubscriptionReconciliationService.GracePeriod, ct);
        var threshold = _config.GetValue<int?>("Billing:ReconciliationMassDowngradeThreshold") ?? DefaultMassDowngradeThreshold;
        if (pastGraceCount > threshold)
        {
            _logger.LogCritical(
                "BillingReconciliationJob ABORTED: {Count} org(s) are simultaneously past the {Days}-day missing-grace " +
                "window (threshold {Threshold}) — this signals a systemic Stripe key/account misconfiguration, not real " +
                "churn. NO downgrades applied this run. Verify Stripe:SecretKey mode/account before the next sweep.",
                pastGraceCount, StripeSubscriptionReconciliationService.GracePeriod.TotalDays, threshold);
            return;
        }

        var ids = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.StripeSubscriptionId != null && o.StripeSubscriptionId != "")
            .Select(o => o.Id)
            .ToListAsync(ct);

        _logger.LogInformation(
            "BillingReconciliationJob: {Count} org(s) with a Stripe subscription to reconcile ({PastGrace} past grace, under threshold {Threshold}).",
            ids.Count, pastGraceCount, threshold);

        var ok = 0;
        foreach (var id in ids)
        {
            try
            {
                await _reconciliation.ReconcileOrgAsync(id, ct);
                ok++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BillingReconciliationJob: reconcile failed for org {OrgId}.", id);
            }
        }

        _logger.LogInformation("BillingReconciliationJob complete — {Ok}/{Total} org(s) reconciled without error.", ok, ids.Count);
    }
}
