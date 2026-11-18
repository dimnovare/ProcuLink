using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.PhoneAlerts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>One phone alert. Counts and fixed sentences only: never a flow, file, amount or address.</summary>
public sealed record PhoneAlert(string Tag, string Title, string Body, string Path, int Count);

/// <summary>
/// Hangfire recurring job (every 5 minutes, all orgs; an unscoped Worker context, so every query names its OrgId): reads the audit events the pipeline
/// already writes, like <see cref="DocumentNotificationJob"/>, and sends each device at most one alert per kind, plus one daily summary at 16:00 UTC.
/// A device's watermark and <c>SummaryOn</c> are saved right after its sends, so a second run never repeats an alert.
/// </summary>
public class PhoneAlertJob(ProcuLinkDbContext db, IPhoneAlertSender sender, DocumentEngineGate gate, ILogger<PhoneAlertJob> log)
{
    public const int SummaryHourUtc = 16;
    private const string Read = "document.read", DeliveryFailed = "document.delivery_failed";

    [DisableConcurrentExecution(600)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken ct) => RunAtAsync(DateTime.UtcNow, ct);

    public async Task RunAtAsync(DateTime now, CancellationToken ct)
    {
        if (!sender.CanSend) return;

        // A browser has one subscription per origin: the newest workspace to save it owns it.
        var all = await db.Set<PushDevice>().ToListAsync(ct);
        var dupes = all.GroupBy(d => d.Endpoint).SelectMany(g => g.OrderByDescending(d => d.CreatedAt).Skip(1)).ToList();
        if (dupes.Count > 0)
        {
            db.RemoveRange(dupes);
            // A row deleted meanwhile (sign-out, members sync) must not stop the run; the next run retries the dedupe.
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
        }

        foreach (var orgId in all.Select(d => d.OrgId).Distinct().Where(gate.IsEnabled).ToList())
        {
            try
            {
                var devices = await db.Set<PushDevice>().Where(d => d.OrgId == orgId).ToListAsync(ct);
                if (devices.Count == 0) continue;
                var docs = db.FlowDocuments.AsNoTracking().Where(d => d.OrgId == orgId && !d.IsSample);
                var waitingDocs = await docs.Where(d => d.Status == DocumentStatus.NeedsReview).Select(d => new { d.Id, d.ChecksJson }).ToListAsync(ct);
                var waiting = waitingDocs.Select(d => d.Id).ToHashSet();
                var held = waitingDocs.Where(d => DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson).Any(c => c.Code == DocumentChecks.NewSender && !c.Passed))
                    .Select(d => d.Id).ToHashSet();
                var failed = (await docs.Where(d => d.Status == DocumentStatus.SendFailed).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
                var since = devices.Min(d => d.LastCheckedAt);
                var events = await db.AuditEvents.AsNoTracking()
                    .Where(a => a.OrgId == orgId && a.EntityType == "FlowDocument" && (a.Action == Read || a.Action == DeliveryFailed) && a.CreatedAt > since && a.CreatedAt <= now)
                    .Select(a => new { a.EntityId, a.Action, a.CreatedAt }).ToListAsync(ct);
                int? today = null;

                foreach (var device in devices)
                {
                    var mine = events.Where(e => e.CreatedAt > device.LastCheckedAt).ToList();
                    var read = mine.Where(e => e.Action == Read).Select(e => e.EntityId).ToList();
                    var alerts = new List<(string Tag, int N)>();
                    if (waiting.Count > 0 && read.Any(id => waiting.Contains(id) && !held.Contains(id))) alerts.Add(("needs-you", waiting.Count));
                    if (read.Any(held.Contains)) alerts.Add(("new-address", held.Count));
                    if (mine.Any(e => e.Action == DeliveryFailed && failed.Contains(e.EntityId))) alerts.Add(("send-failed", failed.Count));
                    if (now.Hour == SummaryHourUtc && device.SummaryOn != DateOnly.FromDateTime(now))
                    {
                        today ??= await docs.CountAsync(d => d.CreatedAt >= now.Date, ct);
                        if (today > 0) alerts.Add(("summary", waiting.Count));
                        device.SummaryOn = DateOnly.FromDateTime(now);
                    }

                    var gone = false;
                    foreach (var (tag, n) in alerts)
                    {
                        var json = DocumentJson.Serialize(new PhoneAlert(tag, "ProcuLink", Body(tag, n, today ?? 0), Path(tag), waiting.Count));
                        if (await sender.SendAsync(device, json, tag, ct) != PushResult.Gone) continue;
                        db.Remove(device);
                        gone = true;
                        break;
                    }
                    if (!gone) device.LastCheckedAt = now;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad org must not stop the others.
                log.LogWarning("Phone alerts failed for org {OrgId}: {Error}", orgId, ex.GetType().Name);
                db.ChangeTracker.Clear();
            }
        }
    }

    public const string TestBody = "Phone alerts work on this device.";

    /// <summary>One test alert, queued by the device's own person; a refused device is removed like any other. No count, so the icon badge stays as it is.</summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task SendTestAsync(Guid deviceId, CancellationToken ct)
    {
        if (!sender.CanSend) return;
        var device = await db.Set<PushDevice>().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return;
        var json = DocumentJson.Serialize(new { tag = "test", title = "ProcuLink", body = TestBody, path = "/settings?tab=workspace" });
        if (await sender.SendAsync(device, json, "test", ct) != PushResult.Gone) return;
        db.Remove(device);
        await db.SaveChangesAsync(ct);
    }

    private static string Path(string tag) => tag switch
    {
        "needs-you" or "new-address" => "/documents?show=needs-you",
        "send-failed" => "/documents?show=failed",
        _ => "/documents",
    };

    public static string Body(string tag, int n, int today) => tag switch
    {
        "needs-you" => n == 1 ? "1 document needs you" : $"{n} documents need you",
        "send-failed" => n == 1 ? "1 document couldn't be sent" : $"{n} documents couldn't be sent",
        "new-address" => n == 1 ? "A first document from a new email address waits for you." : $"{n} first documents from new email addresses wait for you.",
        _ => $"{(today == 1 ? "1 document today" : $"{today} documents today")}, {n switch { 0 => "none need you", 1 => "1 needs you", _ => $"{n} need you" }}",
    };
}
