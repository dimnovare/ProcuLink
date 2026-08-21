using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Alerting;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Database-backed <see cref="IWorkerHealthAlertStateStore"/> — the durable half of the alert
/// cooldown.
///
/// <para><b>Why the database and not Hangfire storage or the idempotency-keys table.</b> The sweep
/// already reads this database every five minutes for its worker-health snapshot, so the cooldown
/// shares the fate of the thing it guards: if Postgres is unreachable the sweep is blind anyway and
/// says so, rather than a second, independently-failing store adding a new way to be wrong.
/// Hangfire's hash storage would work but is untyped, undiscoverable in the schema, and swept by
/// job-retention policy that has nothing to do with alerting. The idempotency-keys table is
/// org-scoped request de-duplication with a 24-hour lifetime — borrowing it would put operational
/// state under a tenant key and let a retention sweep silently re-arm every alert.</para>
///
/// <para><b>Registered as a SINGLETON that opens its own scope per call.</b> The state object it
/// backs is a singleton, and a singleton must never capture a scoped <c>DbContext</c>. Resolving a
/// fresh scope per call is what keeps that true. The context is left UNSCOPED (no
/// <c>ScopeToOrganisation</c>): these rows carry no organisation and no query filter applies.</para>
/// </summary>
public sealed class DbWorkerHealthAlertStateStore : IWorkerHealthAlertStateStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Func<DateTime> _utcNow;

    public DbWorkerHealthAlertStateStore(IServiceScopeFactory scopes, Func<DateTime>? utcNow = null)
    {
        _scopes = scopes;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<WorkerHealthAlertConditionState>> LoadAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

        var rows = await db.WorkerHealthAlertCooldowns
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .Select(r => new WorkerHealthAlertConditionState(r.AlertKey, r.WasBad, r.LastAlertUtc))
            .ToList();
    }

    public async Task SaveAsync(
        IReadOnlyCollection<WorkerHealthAlertConditionState> states,
        CancellationToken ct)
    {
        if (states.Count == 0)
            return;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

        var keys = states.Select(s => s.AlertKey).ToList();
        var existing = await db.WorkerHealthAlertCooldowns
            .Where(r => keys.Contains(r.AlertKey))
            .ToDictionaryAsync(r => r.AlertKey, ct);

        var now = _utcNow();

        foreach (var state in states)
        {
            if (existing.TryGetValue(state.AlertKey, out var row))
            {
                row.WasBad = state.WasBad;
                row.LastAlertUtc = state.LastAlertUtc;
                row.UpdatedUtc = now;
            }
            else
            {
                db.WorkerHealthAlertCooldowns.Add(new WorkerHealthAlertCooldown
                {
                    AlertKey = state.AlertKey,
                    WasBad = state.WasBad,
                    LastAlertUtc = state.LastAlertUtc,
                    UpdatedUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
