using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire job: sends a freshly read document when its flow is set to <c>auto</c>. A failed check never blocks it.
/// It loads the document itself so the sender's claim sees the row as it is now. Paused processing or any other refusal
/// leaves the document as it was, for a person to send.
/// </summary>
public class SendDocumentJob(ProcuLinkDbContext db, DocumentSender sender)
{
    [Queue("critical")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid documentId, Guid organisationId, CancellationToken ct)
    {
        var doc = await db.FlowDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
        if (doc is null) return;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == doc.FlowId && f.OrgId == organisationId, ct);
        if (flow is null || !ShouldSend(doc, flow)) return;
        await sender.SendAsync(doc, SendMode.Automatic, null, ct);
    }

    private static bool ShouldSend(FlowDocument doc, Flow flow) =>
        flow.Automation == FlowAutomation.Auto && DocumentStatus.IsEditable(doc.Status, doc.StatusReason);

    public static void EnqueueIfAutomatic(IBackgroundJobClient jobs, FlowDocument doc, Flow flow)
    {
        if (!ShouldSend(doc, flow)) return;
        var (id, org) = (doc.Id, doc.OrgId);
        jobs.Enqueue<SendDocumentJob>(j => j.ExecuteAsync(id, org, CancellationToken.None));
    }
}
