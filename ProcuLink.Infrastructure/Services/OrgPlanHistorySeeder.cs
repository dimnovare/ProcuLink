using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// One-time (but rerunnable) boot backfill for <see cref="OrgPlanHistory"/>:
/// every organisation that has NO history row yet gets exactly one baseline row
/// seeded from its CURRENT plan + order-limit override, effective from the org's
/// CreatedAt. Pre-feature orgs therefore meter every window with today's values —
/// exactly the pre-history behaviour — until their next real plan/override change
/// appends a dated row.
///
/// <para>Idempotent: orgs that already have any history row (from a previous boot
/// seed, from the org-creation hook, or from a change append) are skipped, so the
/// seed is safe to run on every boot. Concurrent boots of multiple API instances
/// can in the worst case insert duplicate baselines with IDENTICAL values — as-of
/// resolution is unaffected (same values either way).</para>
/// </summary>
public static class OrgPlanHistorySeeder
{
    /// <summary>Seeds missing baseline rows. Returns the number of rows inserted.</summary>
    public static async Task<int> SeedMissingBaselinesAsync(ProcuLinkDbContext db, CancellationToken ct = default)
    {
        var orgsWithoutHistory = await db.Organisations
            .AsNoTracking()
            .Where(o => !db.OrgPlanHistories.Any(h => h.OrgId == o.Id))
            .Select(o => new { o.Id, o.Plan, o.OrderLimitOverride, o.CreatedAt })
            .ToListAsync(ct);

        if (orgsWithoutHistory.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        foreach (var org in orgsWithoutHistory)
        {
            db.OrgPlanHistories.Add(new OrgPlanHistory
            {
                Id                 = Guid.NewGuid(),
                OrgId              = org.Id,
                Plan               = org.Plan,
                OrderLimitOverride = org.OrderLimitOverride,
                EffectiveFrom      = org.CreatedAt == default
                    ? now
                    : new DateTimeOffset(DateTime.SpecifyKind(org.CreatedAt, DateTimeKind.Utc)),
            });
        }

        await db.SaveChangesAsync(ct);
        return orgsWithoutHistory.Count;
    }
}
