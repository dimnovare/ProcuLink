using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire recurring job (every 5 minutes, all orgs): re-enqueues deliveries a lost job left behind (an unscoped Worker context, so no org filter applies), and repairs a
/// document stuck in <c>sending</c> with nothing in flight. <see cref="DeliverDocumentJob"/> is idempotent, so a re-enqueue is safe.
/// </summary>
public class DocumentDeliverySweepJob(ProcuLinkDbContext db, IBackgroundJobClient jobs, ILogger<DocumentDeliverySweepJob> logger)
{
    public static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(10);

    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StrandedAfter;
        var stranded = await db.Set<FlowDelivery>()
            .Where(d => (d.Status == FlowDeliveryStatus.Queued && d.UpdatedAt < cutoff)
                     || (d.Status == FlowDeliveryStatus.Delivering && d.ClaimedAt < cutoff)
                     || (d.Status == FlowDeliveryStatus.Failed && d.NextAttemptAt < cutoff))
            .Select(d => new { d.Id, d.OrgId }).ToListAsync(ct);
        foreach (var d in stranded) DeliverDocumentJob.Enqueue(jobs, d.Id, d.OrgId);

        // A crash between the sender's claim and its save leaves a document in "sending" that nothing will ever move on.
        var stuck = await db.FlowDocuments
            .Where(d => d.Status == DocumentStatus.Sending && d.UpdatedAt < cutoff
                && !db.Set<FlowDelivery>().Any(x => x.OrgId == d.OrgId && x.DocumentId == d.Id
                    && (x.Status == FlowDeliveryStatus.Queued || x.Status == FlowDeliveryStatus.Delivering || x.Status == FlowDeliveryStatus.Failed)))
            .ToListAsync(ct);
        var repaired = new List<(FlowDocument Doc, string Before)>();
        foreach (var doc in stuck) repaired.Add((doc, await DeliverDocumentJob.RollUpAsync(db, doc, null, DateTime.UtcNow, ct)));
        await db.SaveChangesAsync(ct);
        foreach (var (doc, before) in repaired)
        {
            logger.LogWarning("Flow document {DocumentId} (org {OrgId}) was stuck in sending with nothing in flight; repaired to {Status}.", doc.Id, doc.OrgId, doc.Status);
            await DeliverDocumentJob.AnnounceAsync(db, jobs, doc, before, logger, ct);
        }
    }
}
