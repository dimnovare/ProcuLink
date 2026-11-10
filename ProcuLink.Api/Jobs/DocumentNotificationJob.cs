using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire recurring job (every 15 minutes, all orgs; an unscoped Worker context, so no org filter applies): emails each opted-in member one digest of the
/// documents that need review or failed since their last email. A setting's <c>LastSentAt</c> moves only after its email is sent, so a retry never mails twice.
/// </summary>
public class DocumentNotificationJob(ProcuLinkDbContext db, IEmailSender email, IConfiguration config, ILogger<DocumentNotificationJob> log)
{
    private static readonly string[] FailedActions = ["document.read_failed", "document.delivery_failed"];

    [DisableConcurrentExecution(600)]
    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var settings = await db.Set<NotificationSetting>().Where(s => s.OnReview || s.OnFailure).ToListAsync(ct);
        foreach (var s in settings)
        {
            if (s.LastSentAt is not { } since) { s.LastSentAt = now; continue; }
            if (string.IsNullOrWhiteSpace(s.Email)) continue;

            var events = await db.AuditEvents.AsNoTracking()
                .Where(a => a.OrgId == s.OrgId && a.EntityType == "FlowDocument" && a.CreatedAt > since && a.CreatedAt <= now
                    && (a.Action == "document.read" || FailedActions.Contains(a.Action)))
                .Select(a => new { a.EntityId, a.Action }).ToListAsync(ct);
            var ids = events.Select(e => e.EntityId).Distinct().ToList();
            var docs = await db.FlowDocuments.AsNoTracking()
                .Where(d => d.OrgId == s.OrgId && !d.IsSample && ids.Contains(d.Id))
                .Select(d => new { d.Id, d.Status, FlowName = d.Flow!.Name }).ToListAsync(ct);

            var review = s.OnReview
                ? docs.Where(d => d.Status == DocumentStatus.NeedsReview && events.Any(e => e.EntityId == d.Id && e.Action == "document.read")).ToList()
                : [];
            var failed = s.OnFailure
                ? docs.Where(d => events.Any(e => e.EntityId == d.Id && FailedActions.Contains(e.Action))).ToList()
                : [];
            var total = review.Count + failed.Count;
            if (total > 0)
            {
                var lines = review.Select(d => d.FlowName).Concat(failed.Select(d => d.FlowName)).Distinct().Order(StringComparer.Ordinal).Select(name =>
                {
                    var parts = new List<string>();
                    if (review.Count(d => d.FlowName == name) is > 0 and var r) parts.Add($"{r} waiting for review");
                    if (failed.Count(d => d.FlowName == name) is > 0 and var f) parts.Add($"{f} couldn't be read or sent");
                    return $"{name}: {string.Join(", ", parts)}";
                });
                var body = $"{string.Join("\n", lines)}\n\nOpen ProcuLink: {AppUrl()}\n\nYou get these emails because you switched them on in Settings → Notifications.";
                try { await email.SendAsync(s.Email, total == 1 ? "ProcuLink: 1 document needs you" : $"ProcuLink: {total} documents need you", body, ct); }
                catch (Exception ex) { log.LogError(ex, "Notification email failed for setting {SettingId}", s.Id); continue; }
            }
            s.LastSentAt = now;
            if (total > 0) await db.SaveChangesAsync(ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private string AppUrl() =>
        (config["Frontend:Url"] ?? "http://localhost:8082").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.TrimEnd('/') ?? "http://localhost:8082";
}
