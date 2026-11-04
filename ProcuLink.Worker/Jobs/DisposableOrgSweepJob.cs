using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring (daily): erases the workspaces production smoke runs leave behind. The smoke cleanup
/// (frontend <c>scripts/prod-smoke/clerk-disposable.mjs</c>) deletes only the Clerk org and user, so
/// the Organisation row the API creates on first call stayed forever.
///
/// <para>An org is erased only when ALL hold: its slug or name starts <c>proculink-ci-smoke-</c> or
/// matches <c>^smoke-\d{8}-</c>; it is older than 6 hours, so a smoke run in flight is never touched;
/// it has no orders, invoices or suppliers; and it has no Stripe subscription (which the eraser
/// re-checks itself). Idempotent: an erased org no longer exists to match.</para>
/// </summary>
public sealed class DisposableOrgSweepJob(
    ProcuLinkDbContext db, IOrganisationEraser eraser, ILogger<DisposableOrgSweepJob> logger)
{
    public const string Actor = "system:disposable-sweep";

    private static readonly Regex Disposable = new(@"^(proculink-ci-smoke-|smoke-\d{8}-)", RegexOptions.CultureInvariant);

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-6);
        var candidates = await db.Organisations.AsNoTracking()
            .Where(o => o.CreatedAt < cutoff && o.StripeSubscriptionId == null
                && (o.Slug.StartsWith("proculink-ci-smoke-") || o.Slug.StartsWith("smoke-")
                    || o.Name.StartsWith("proculink-ci-smoke-") || o.Name.StartsWith("smoke-"))
                && !db.PurchaseOrders.Any(p => p.OrgId == o.Id)
                && !db.Invoices.Any(i => i.OrganisationId == o.Id)
                && !db.Suppliers.Any(s => s.OrgId == o.Id))
            .Select(o => new { o.Id, o.Slug, o.Name })
            .ToListAsync(ct);

        foreach (var org in candidates.Where(o => Disposable.IsMatch(o.Slug) || Disposable.IsMatch(o.Name)))
        {
            var receipt = await eraser.EraseAsync(org.Id, Actor, "Leftover production-smoke workspace.", ct);
            logger.LogInformation("DisposableOrgSweepJob: {Slug} -> {Outcome}.", org.Slug, receipt.Outcome);
        }
    }
}
