using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>How many of a flow's latest sent documents went out without a correction.</summary>
public static class FlowStreak
{
    private const int Window = 201; // 201 means "more than 200"

    /// <summary>Null when nothing was sent. A taught value, or an edit that changed the value, is a correction; confirming as is (old = new) is not.</summary>
    public static async Task<int?> CountAsync(ProcuLinkDbContext db, Guid orgId, Guid flowId, CancellationToken ct)
    {
        var ids = await db.FlowDocuments.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.FlowId == flowId && d.Status == DocumentStatus.Sent && !d.IsSample)
            .OrderByDescending(d => d.ReadAt).ThenByDescending(d => d.Id)
            .Select(d => d.Id).Take(Window).ToListAsync(ct);
        if (ids.Count == 0) return null;
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.EntityType == "FlowDocument" && ids.Contains(a.EntityId)
                && (a.Action == "document.value_taught" || a.Action == "document.value_edited"))
            .Select(a => new { a.EntityId, a.Action, a.Payload }).ToListAsync(ct);
        var corrected = rows.Where(r => r.Action == "document.value_taught" || Changed(r.Payload.RootElement))
            .Select(r => r.EntityId).ToHashSet();
        return ids.TakeWhile(id => !corrected.Contains(id)).Count();
    }

    private static bool Changed(System.Text.Json.JsonElement p) =>
        (p.TryGetProperty("old", out var o) ? o.GetRawText() : "null") != (p.TryGetProperty("new", out var n) ? n.GetRawText() : "null");
}
