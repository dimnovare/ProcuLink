using System.Text.Json.Nodes;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

public static class FlowWebhookEvent
{
    public const string Read = "document.read";
    public const string ReadFailed = "document.read_failed";
    public const string Sent = "document.sent";
    public const string SendFailed = "document.send_failed";
    public const string Test = "document.test";
}

/// <summary>Tells a flow's webhook that a document was read or sent. One enqueue per transition; nothing when the flow has no webhook.</summary>
public static class FlowWebhook
{
    public static async Task EnqueueAsync(ProcuLinkDbContext db, IBackgroundJobClient jobs, FlowDocument doc, string eventType, CancellationToken ct)
    {
        try
        {
            var intake = await db.FlowIntakes.AsNoTracking().FirstOrDefaultAsync(
                i => i.OrgId == doc.OrgId && i.FlowId == doc.FlowId && i.Kind == IntakeKind.Api && i.IsEnabled, ct);
            if (string.IsNullOrWhiteSpace(UrlOf(intake))) return;
            var (orgId, documentId, eventId) = (doc.OrgId, doc.Id, Guid.NewGuid());
            jobs.Enqueue<FlowWebhookJob>(j => j.ExecuteAsync(orgId, documentId, eventType, eventId, CancellationToken.None));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The read or send is already saved. A missed announcement must not undo it or fail its job.
        }
    }

    public static string? UrlOf(FlowIntake? intake) =>
        intake is null ? null : JsonNode.Parse(intake.ConfigJson)?["webhookUrl"]?.GetValue<string>();
}
