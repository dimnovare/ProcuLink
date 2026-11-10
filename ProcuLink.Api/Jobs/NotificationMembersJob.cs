using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
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
    private const int PageSize = 100;
    private const int MaxOffset = 100_000;

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
                var members = await FetchAsync(http.CreateClient("clerk"), key, org.Id, org.ClerkOrgId, ct);
                if (members is null) continue;

                var rows = await db.Set<NotificationSetting>().Where(s => s.OrgId == org.Id).ToListAsync(ct);
                foreach (var row in rows.Where(r => !members.ContainsKey(r.ClerkUserId))) db.Remove(row);
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

    /// <summary>All members as user id to email (null when the identifier is not an address), or null when the read failed or was incomplete.</summary>
    private async Task<Dictionary<string, string?>?> FetchAsync(HttpClient client, string key, Guid orgId, string clerkOrgId, CancellationToken ct)
    {
        var members = new Dictionary<string, string?>();
        try
        {
            for (var offset = 0; offset < MaxOffset; offset += PageSize)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"organizations/{Uri.EscapeDataString(clerkOrgId)}/memberships?limit={PageSize}&offset={offset}");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) { log.LogWarning("Notification member sync skipped org {OrgId}: Clerk answered {Status}", orgId, (int)response.StatusCode); return null; }

                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var page = doc.RootElement.GetProperty("data");
                foreach (var m in page.EnumerateArray())
                {
                    var user = m.GetProperty("public_user_data");
                    var identifier = user.TryGetProperty("identifier", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()?.Trim() : null;
                    members[user.GetProperty("user_id").GetString()!] = identifier?.Contains('@') == true ? identifier : null;
                }
                if (page.GetArrayLength() < PageSize)
                {
                    if (doc.RootElement.TryGetProperty("total_count", out var t) && t.ValueKind == JsonValueKind.Number && members.Count < t.GetInt32())
                    {
                        log.LogWarning("Notification member sync skipped org {OrgId}: read {Count} of {Total} members", orgId, members.Count, t.GetInt32());
                        return null;
                    }
                    return members;
                }
            }
            log.LogWarning("Notification member sync skipped org {OrgId}: paging passed {Max} members", orgId, MaxOffset);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentNullException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Notification member sync skipped org {OrgId}: {Error}", orgId, ex.GetType().Name);
            return null;
        }
    }
}
