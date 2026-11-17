using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire job: sends a freshly read document when its flow is set to <c>auto</c>. A failed check never blocks it, with two
/// exceptions: a repeated document number waits for a person, because on an unattended flow it would be a second posting; and so
/// does the first emailed document from a new sender, because anyone who knows the workspace's address can email it.
/// It loads the document itself so the sender's claim sees the row as it is now. Paused processing or any other refusal
/// leaves the document as it was, for a person to send.
/// </summary>
public class SendDocumentJob(ProcuLinkDbContext db, DocumentSender sender)
{
    [Queue("critical")]
    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(Guid documentId, Guid organisationId, CancellationToken ct)
    {
        var doc = await db.FlowDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
        if (doc is null) return;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == doc.FlowId && f.OrgId == organisationId, ct);
        if (flow is null || !ShouldSend(doc, flow)) return;
        await sender.SendAsync(doc, SendMode.Automatic, null, ct);
    }

    // A sample is invented, and a sort that was not certain is unconfirmed: neither is sent by itself, only by a person (DocumentsController.Send, which does not come here).
    private static bool ShouldSend(FlowDocument doc, Flow flow) =>
        !doc.IsSample && !doc.SortUnconfirmed && flow.Automation == FlowAutomation.Auto && doc.Status is DocumentStatus.NeedsReview or DocumentStatus.Ready && DocumentStatus.IsEditable(doc.Status, doc.StatusReason)
        && !DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).Any(c => c.Code is DocumentChecks.Duplicate or DocumentChecks.NewSender && !c.Passed);

    public static void EnqueueIfAutomatic(IBackgroundJobClient jobs, FlowDocument doc, Flow flow)
    {
        if (!ShouldSend(doc, flow)) return;
        var (id, org) = (doc.Id, doc.OrgId);
        jobs.Enqueue<SendDocumentJob>(j => j.ExecuteAsync(id, org, CancellationToken.None));
    }
}
