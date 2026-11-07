using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>Claim-first ledger for flow intake: an item is taken once, by exactly one caller.</summary>
public static class IntakeLedger
{
    /// <summary>The <c>DocumentId</c> of an item the engine refused; it is never retried.</summary>
    public static readonly Guid Refused = Guid.Empty;

    public static readonly TimeSpan RetakeAfter = TimeSpan.FromMinutes(30);

    /// <summary>Returns the claim, or null when the item is already taken.</summary>
    public static async Task<FlowIntakeItem?> ClaimAsync(
        ProcuLinkDbContext db, Guid orgId, Guid scopeId, string itemKey, CancellationToken ct)
    {
        var item = new FlowIntakeItem
        {
            Id = Guid.NewGuid(), OrgId = orgId, ScopeId = scopeId, ItemKey = itemKey, CreatedAt = DateTime.UtcNow,
        };
        db.FlowIntakeItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            return item;
        }
        catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
        {
            db.Entry(item).State = EntityState.Detached;
        }

        // A claim whose worker died before it produced a document may be taken again; the conditional
        // update means exactly one caller wins.
        var now = DateTime.UtcNow;
        var cutoff = now - RetakeAfter;
        var retaken = await db.FlowIntakeItems
            .Where(i => i.OrgId == orgId && i.ScopeId == scopeId && i.ItemKey == itemKey
                        && i.DocumentId == null && i.CreatedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CreatedAt, now), ct);
        if (retaken == 0) return null;
        return await db.FlowIntakeItems.AsNoTracking()
            .FirstAsync(i => i.OrgId == orgId && i.ScopeId == scopeId && i.ItemKey == itemKey, ct);
    }
}
