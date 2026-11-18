using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire recurring job (daily, all orgs; an unscoped Worker context): mirrors each workspace's Clerk membership into its notification settings. People who left lose their row,
/// new members get a row with both switches off, and a member's email is filled in from Clerk. A failed or incomplete fetch is unknown, not "no members", so that org is skipped untouched.
/// </summary>
public class NotificationMembersJob(IHttpClientFactory http, IConfiguration config, ProcuLinkDbContext db, DocumentEngineGate gate, ILogger<NotificationMembersJob> log)
{
    [DisableConcurrentExecution(600)]
    public async Task RunAsync(CancellationToken ct)
    {
        var key = config["Clerk:SecretKey"];
        if (string.IsNullOrWhiteSpace(key)) { log.LogInformation("Notification member sync skipped: no Clerk secret key is configured"); return; }

        var orgs = await db.Organisations.AsNoTracking().Where(o => o.ClerkOrgId != "").Select(o => new { o.Id, o.ClerkOrgId }).ToListAsync(ct);
        foreach (var org in orgs.Where(o => gate.IsEnabled(o.Id)))
        {
            try
            {
                var members = await ClerkMembers.FetchAsync(http.CreateClient("clerk"), key, org.ClerkOrgId, log, ct);
                if (members is null) continue;

                var rows = await db.Set<NotificationSetting>().Where(s => s.OrgId == org.Id).ToListAsync(ct);
                foreach (var row in rows.Where(r => !members.ContainsKey(r.ClerkUserId))) db.Remove(row);
                db.RemoveRange((await db.Set<PushDevice>().Where(d => d.OrgId == org.Id).ToListAsync(ct)).Where(d => !members.ContainsKey(d.ClerkUserId)));
                foreach (var (userId, email) in members)
                {
                    var row = rows.FirstOrDefault(r => r.ClerkUserId == userId);
                    if (row is null) db.Add(new NotificationSetting { Id = Guid.NewGuid(), OrgId = org.Id, ClerkUserId = userId, Email = email });
                    else row.Email = email ?? row.Email;
                }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad org (for example a unique-index race with the settings PUT) must not stop the others.
                log.LogWarning("Notification member sync failed for org {OrgId}: {Error}", org.Id, ex.GetType().Name);
                db.ChangeTracker.Clear();
            }
        }
    }
}
