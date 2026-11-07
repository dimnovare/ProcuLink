using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.Documents.Intake;
using ProcuLink.Core.Documents;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire recurring job (every 5 minutes, all orgs): enqueues one <see cref="FlowPickupJob"/> per enabled pickup intake
/// whose org has the document engine on. A kind with no registered source is left alone.
/// </summary>
public class FlowPickupDispatchJob(
    ProcuLinkDbContext db, IBackgroundJobClient jobs, DocumentEngineGate gate, IEnumerable<IPickupSource> sources)
{
    [Queue("polling")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var kinds = sources.Select(s => s.Kind).Intersect(IntakeKind.Pickups).ToList();
        var due = await db.FlowIntakes.AsNoTracking()
            .Where(i => i.IsEnabled && kinds.Contains(i.Kind))
            .Select(i => new { i.OrgId, i.Id }).ToListAsync(ct);
        foreach (var i in due.Where(i => gate.IsEnabled(i.OrgId)))
            jobs.Enqueue<FlowPickupJob>(j => j.ExecuteAsync(i.OrgId, i.Id, CancellationToken.None));
    }
}
