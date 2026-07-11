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
/// later. Two INDEPENDENT trips abort the whole sweep (downgrades none) and alert — whichever fires
/// first:
/// <list type="bullet">
///   <item><b>Absolute</b> — past-grace count exceeds a fixed threshold (default 10). A large batch
///   is systemic regardless of base size.</item>
///   <item><b>Relative</b> — past-grace orgs are simultaneously ≥ a small absolute floor (default 3)
///   AND ≥ a fraction (default 25%) of the orgs-with-a-subscription reconciled this run. This catches
///   the case the absolute threshold is inert for: a SMALL paying base (≤10 orgs) where a wrong-mode
///   key 404s every subscription and the whole base goes past grace without ever exceeding 10. The
///   floor stops a lone genuine cancellation in a 2–3 org base from tripping the fraction.</item>
/// </list></para>
/// </summary>
public sealed class BillingReconciliationJob
{
    /// <summary>Default absolute cap on orgs allowed to be past-grace in one run before the sweep aborts as a suspected systemic fault.</summary>
    private const int DefaultMassDowngradeThreshold = 10;

    /// <summary>Default absolute floor for the RELATIVE breaker — below this many past-grace orgs the fraction rule never trips (protects tiny bases from a single legit cancellation). Tunable via <c>Billing:ReconciliationMassDowngradeFloor</c>.</summary>
    private const int DefaultMassDowngradeFloor = 3;

    /// <summary>Default fraction for the RELATIVE breaker — past-grace orgs at or above this share of the subscribed base signal a systemic fault. Tunable via <c>Billing:ReconciliationMassDowngradeFraction</c>.</summary>
    private const double DefaultMassDowngradeFraction = 0.25;

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

        // The population reconciled this run = every org carrying a Stripe subscription id.
        // Computed up front so it doubles as the RELATIVE breaker's denominator below; the
        // past-grace numerator is filtered on the SAME subscription-id predicate, so past-grace
        // ⊆ this set and the fraction ≤ 1 by construction.
        var ids = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.StripeSubscriptionId != null && o.StripeSubscriptionId != "")
            .Select(o => o.Id)
            .ToListAsync(ct);

        // Past-grace numerator for the RELATIVE breaker. It MUST carry the same subscription-id
        // predicate as the denominator (ids) so it is a strict SUBSET and the fraction can never
        // exceed 1. Without it a "phantom" org — one a reconcile-404 marked missing, then a
        // late/missed customer.subscription.deleted downgraded (nulling the subscription id) —
        // would be counted here yet absent from ids, letting a few phantoms trip the breaker
        // every run and silently disable the reconciliation backstop. (The delete webhook now
        // also clears the marker, so this predicate is belt-and-suspenders against any
        // residual/legacy phantom rows.)
        var pastGraceCount = await _db.Organisations
            .AsNoTracking()
            .CountAsync(o => o.StripeReconciliationMissingSince != null
                          && o.StripeReconciliationMissingSince <= now - StripeSubscriptionReconciliationService.GracePeriod
                          && o.StripeSubscriptionId != null && o.StripeSubscriptionId != "", ct);

        var absoluteThreshold = _config.GetValue<int?>("Billing:ReconciliationMassDowngradeThreshold") ?? DefaultMassDowngradeThreshold;
        var floor             = _config.GetValue<int?>("Billing:ReconciliationMassDowngradeFloor")     ?? DefaultMassDowngradeFloor;
        var fraction          = _config.GetValue<double?>("Billing:ReconciliationMassDowngradeFraction") ?? DefaultMassDowngradeFraction;

        // Two INDEPENDENT trips — whichever fires first aborts the sweep. The relative trip
        // catches the small-base blind spot the absolute threshold cannot (see class doc).
        var absoluteTrip = pastGraceCount > absoluteThreshold;
        var relativeTrip = ids.Count > 0
                        && pastGraceCount >= floor
                        && pastGraceCount >= fraction * ids.Count;
        if (absoluteTrip || relativeTrip)
        {
            _logger.LogCritical(
                "BillingReconciliationJob ABORTED: {Count} of {Subscribed} org(s) with a subscription are simultaneously " +
                "past the {Days}-day missing-grace window (absolute threshold {Abs}, relative floor {Floor} + fraction {Frac}; " +
                "absoluteTrip={AbsTrip}, relativeTrip={RelTrip}) — this signals a systemic Stripe key/account " +
                "misconfiguration, not real churn. NO downgrades applied this run. Verify Stripe:SecretKey mode/account " +
                "before the next sweep.",
                pastGraceCount, ids.Count, StripeSubscriptionReconciliationService.GracePeriod.TotalDays,
                absoluteThreshold, floor, fraction, absoluteTrip, relativeTrip);
            return;
        }

        _logger.LogInformation(
            "BillingReconciliationJob: {Count} org(s) with a Stripe subscription to reconcile ({PastGrace} past grace; " +
            "absolute threshold {Abs}, relative floor {Floor} + fraction {Frac}).",
            ids.Count, pastGraceCount, absoluteThreshold, floor, fraction);

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
