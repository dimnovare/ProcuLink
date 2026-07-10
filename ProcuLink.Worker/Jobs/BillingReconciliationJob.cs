using Hangfire;
using Microsoft.EntityFrameworkCore;
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
/// </summary>
public sealed class BillingReconciliationJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IBillingReconciliationService _reconciliation;
    private readonly ILogger<BillingReconciliationJob> _logger;

    public BillingReconciliationJob(
        ProcuLinkDbContext db,
        IBillingReconciliationService reconciliation,
        ILogger<BillingReconciliationJob> logger)
    {
        _db = db;
        _reconciliation = reconciliation;
        _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var ids = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.StripeSubscriptionId != null && o.StripeSubscriptionId != "")
            .Select(o => o.Id)
            .ToListAsync(ct);

        _logger.LogInformation("BillingReconciliationJob: {Count} org(s) with a Stripe subscription to reconcile.", ids.Count);

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
