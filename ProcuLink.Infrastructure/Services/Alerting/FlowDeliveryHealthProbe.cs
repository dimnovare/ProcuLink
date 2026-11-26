using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;

namespace ProcuLink.Infrastructure.Services.Alerting;

/// <summary>
/// Counts dead-lettered and stuck flow deliveries across all organisations. The stuck predicate uses the
/// same clocks as the document-delivery sweep, at three times its stranded age.
/// </summary>
public sealed class FlowDeliveryHealthProbe(ProcuLinkDbContext db, WorkerHealthAlertOptions options) : IFlowDeliveryHealthProbe
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    /// <summary>Latest dead letters read for the messages: enough for a few of each side.</summary>
    public const int LatestRows = 20;

    public async Task<FlowDeliveryHealthSignal> GetSignalAsync(CancellationToken ct)
    {
        db.UseCrossOrganisationScope(
            "flow delivery health probe: counts dead-lettered and stuck flow deliveries across ALL " +
            "organisations, naming the latest few by org slug, for the operator alert only, never a tenant-facing view.");

        var now = DateTime.UtcNow;
        var windowStart = now - options.PipelineFailureWindow;
        var cutoff = now - StuckAfter;
        var rows = db.Set<FlowDelivery>();

        var dead = rows.Where(d => d.Status == FlowDeliveryStatus.DeadLetter && d.UpdatedAt >= windowStart);
        var counts = await dead.GroupBy(d => new { d.FailureKind, d.OrgId })
            .Select(g => new FlowDeadLetterCount(g.Key.FailureKind, g.Key.OrgId, g.Count())).ToListAsync(ct);
        var latest = await dead.OrderByDescending(d => d.UpdatedAt).Take(LatestRows)
            .Join(db.Organisations, d => d.OrgId, o => o.Id, (d, o) => new FlowDeadLetterRow(o.Slug, d.Protocol, d.FailureKind, d.LastError))
            .ToListAsync(ct);
        var stuck = await rows.CountAsync(d =>
            (d.Status == FlowDeliveryStatus.Queued && d.UpdatedAt < cutoff)
            || (d.Status == FlowDeliveryStatus.Delivering && d.ClaimedAt < cutoff)
            || (d.Status == FlowDeliveryStatus.Failed && d.NextAttemptAt < cutoff), ct);

        return new FlowDeliveryHealthSignal(counts, latest, options.EffectivePipelineFailureWindowMinutes, stuck, (int)StuckAfter.TotalMinutes);
    }
}
