using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring (daily): erases the workspaces production smoke runs leave behind. The smoke cleanup
/// (frontend <c>scripts/prod-smoke/clerk-disposable.mjs</c>) deletes only the Clerk org and user, so
/// the Organisation row the API creates on first call stayed forever.
///
/// <para>An org is erased only when ALL hold: its slug or name starts <c>proculink-ci-smoke-</c>
/// (the only prefix a generator emits); it is older than 6 hours, so a smoke run in flight is never
/// touched; <see cref="OrganisationEraser.IsPaying"/> is false; and the eraser's own table walk finds
/// no row outside <see cref="Bookkeeping"/>. Idempotent: an erased org no longer exists to match.</para>
/// </summary>
public sealed class DisposableOrgSweepJob(
    ProcuLinkDbContext db, IOrganisationEraser eraser, ILogger<DisposableOrgSweepJob> logger)
{
    public const string Actor = "system:disposable-sweep";
    private const string Prefix = "proculink-ci-smoke-";

    /// <summary>Tables a workspace fills just by being signed into. A row anywhere else means it was used.</summary>
    private static readonly HashSet<string> Bookkeeping =
        ["memberships", "audit_events", "org_plan_history", "ai_usage_monthly", "idempotency_keys"];

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-6);
        var candidates = await db.Organisations.AsNoTracking()
            .Where(o => o.CreatedAt < cutoff && (o.Slug.StartsWith(Prefix) || o.Name.StartsWith(Prefix)))
            .ToListAsync(ct);

        foreach (var org in candidates.Where(o => !OrganisationEraser.IsPaying(o)))
        {
            try
            {
                var used = (await OrganisationEraser.CountRowsAsync(db, org.Id, ct))
                    .Where(t => t.Value > 0 && !Bookkeeping.Contains(t.Key)).Select(t => t.Key).ToList();
                if (used.Count > 0)
                {
                    logger.LogInformation("DisposableOrgSweepJob: {Slug} kept — it has rows in {Tables}.", org.Slug, used);
                    continue;
                }
                var receipt = await eraser.EraseAsync(org.Id, Actor, "Leftover production-smoke workspace.", ct);
                logger.LogInformation("DisposableOrgSweepJob: {Slug} -> {Outcome}.", org.Slug, receipt.Outcome);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad workspace must neither stop the rest nor leave its tracked receipt for the next save.
                db.ChangeTracker.Clear();
                logger.LogError(ex, "DisposableOrgSweepJob: erasing {Slug} failed; continuing.", org.Slug);
            }
        }
    }
}
