using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Api.Services.Documents;

/// <summary>Sorts a document out of the workspace inbox into the person flow it belongs to. Only SaveRememberedAsync saves.</summary>
public static class DocumentSort
{
    /// <summary>Waits in the inbox: no person flow holds this kind.</summary>
    public const string NewKind = "new_kind";
    /// <summary>Waits in the inbox: two or more person flows hold this kind.</summary>
    public const string ChooseFlow = "choose_flow";
    /// <summary>Sorted into a flow without certainty; waits for a person.</summary>
    public const string CheckSort = "check_sort";
    public const string Downloaded = "downloaded";

    private const string Sorted = "document.sorted";
    private const int ResortBatch = 50;

    public sealed record SortTarget(Guid FlowId, string FlowName, bool Certain);

    /// <summary>
    /// Certain: a remembered person flow for this sender and kind. Not certain: the one person flow holding a non-sample document
    /// of this kind. Otherwise no target, and the reason the document waits.
    /// </summary>
    public static async Task<(SortTarget? Target, string? WaitReason)> DecideAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct)
    {
        if (doc.Kind is null) return (null, NewKind);
        if (doc.SenderKey is not null)
        {
            var remembered = await db.Set<FlowSortMemory>()
                .Where(m => m.OrgId == doc.OrgId && m.SenderKey == doc.SenderKey && m.Kind == doc.Kind)
                .Join(db.Flows.PersonFlows(doc.OrgId), m => m.FlowId, f => f.Id, (m, f) => new { f.Id, f.Name })
                .FirstOrDefaultAsync(ct);
            if (remembered is not null) return (new SortTarget(remembered.Id, remembered.Name, true), null);
        }
        var holding = await db.Flows.PersonFlows(doc.OrgId)
            .Where(f => db.FlowDocuments.Any(d => d.OrgId == doc.OrgId && d.FlowId == f.Id && d.Kind == doc.Kind && !d.IsSample))
            .Select(f => new { f.Id, f.Name })
            .Take(2)
            .ToListAsync(ct);
        return holding.Count switch
        {
            0 => (null, NewKind),
            1 => (new SortTarget(holding[0].Id, holding[0].Name, false), null),
            _ => (null, ChooseFlow),
        };
    }

    /// <summary>Moves the document to an inbox-sorted target, or leaves it waiting with its reason. True when it moved and must be read there.</summary>
    public static async Task<bool> SortAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct)
    {
        var (target, waitReason) = await DecideAsync(db, doc, ct);
        if (target is null)
        {
            doc.StatusReason = waitReason;
            return false;
        }
        return await MoveToAsync(db, doc, target.FlowId, target.FlowName, target.Certain, Sorted, null, ct);
    }

    /// <summary>
    /// Puts the document in <paramref name="flowId"/>, marked unconfirmed unless <paramref name="certain"/>. True when it must be read
    /// there; false when that flow already holds the same file, and the document waits there as a duplicate file, unread.
    /// </summary>
    public static async Task<bool> MoveToAsync(
        ProcuLinkDbContext db, FlowDocument doc, Guid flowId, string flowName, bool certain, string action, string? userId, CancellationToken ct,
        bool newFlow = false)
    {
        var duplicateFile = doc.Sha256.Length > 0 && await db.FlowDocuments.AnyAsync(
            d => d.OrgId == doc.OrgId && d.FlowId == flowId && d.Sha256 == doc.Sha256 && d.Id != doc.Id && !d.IsSample, ct);
        var fromFlowId = doc.FlowId;
        doc.FlowId = flowId;
        doc.Status = DocumentStatus.Received;
        doc.StatusReason = null;
        doc.ChecksJson = "[]";
        if (duplicateFile) DocumentIntakeService.HoldAsDuplicateFile(doc);
        doc.SortUnconfirmed = !certain;
        doc.UpdatedAt = DateTime.UtcNow;
        DocumentAudit.Add(db, doc, action, new { fromFlowId, flowId, flow = flowName, certain, duplicateFile, newFlow }, userId);
        return !duplicateFile;
    }

    /// <summary>Remembers the document's person flow for its sender and kind; the last write wins.</summary>
    public static async Task RememberAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct)
    {
        // A sample's sender is public: it never teaches memory.
        if (doc.IsSample || doc.SenderKey is null || doc.Kind is null) return;
        if (!await db.Flows.PersonFlows(doc.OrgId).AnyAsync(f => f.Id == doc.FlowId, ct)) return;
        var memory = db.Set<FlowSortMemory>();
        var row = memory.Local.FirstOrDefault(m => m.OrgId == doc.OrgId && m.SenderKey == doc.SenderKey && m.Kind == doc.Kind)
            ?? await memory.FirstOrDefaultAsync(m => m.OrgId == doc.OrgId && m.SenderKey == doc.SenderKey && m.Kind == doc.Kind, ct);
        var now = DateTime.UtcNow;
        if (row is null)
            memory.Add(new FlowSortMemory
            {
                Id = Guid.NewGuid(), OrgId = doc.OrgId, SenderKey = doc.SenderKey, Kind = doc.Kind, FlowId = doc.FlowId,
                CreatedAt = now, UpdatedAt = now,
            });
        else
        {
            row.FlowId = doc.FlowId;
            row.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Saves every pending change after <see cref="RememberAsync"/>. When another request added the same sender and kind first,
    /// its row is updated to this document's flow instead, and the save runs once more.
    /// </summary>
    public static async Task SaveRememberedAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
        {
            var lost = db.ChangeTracker.Entries<FlowSortMemory>()
                .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.OrgId == doc.OrgId && e.Entity.SenderKey == doc.SenderKey && e.Entity.Kind == doc.Kind);
            if (lost is null) throw;
            lost.State = EntityState.Detached;
            var row = await db.Set<FlowSortMemory>()
                .FirstOrDefaultAsync(m => m.OrgId == doc.OrgId && m.SenderKey == doc.SenderKey && m.Kind == doc.Kind, ct) ?? throw ex;
            row.FlowId = lost.Entity.FlowId;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>A person confirmed the document's flow: the hold and its reason are lifted and the flow remembered for its sender and kind.</summary>
    public static async Task ConfirmAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct)
    {
        doc.SortUnconfirmed = false;
        if (doc.StatusReason == CheckSort) doc.StatusReason = null;
        await RememberAsync(db, doc, ct);
    }

    /// <summary>Sorts again up to 50 inbox documents of this kind that wait for a person. Returns the ids moved to be read; the caller saves, then enqueues their reads.</summary>
    public static async Task<IReadOnlyList<Guid>> ResortWaitingAsync(ProcuLinkDbContext db, Guid orgId, string kind, CancellationToken ct)
    {
        var waiting = await db.FlowDocuments
            .Where(d => d.OrgId == orgId && d.Flow!.IsInbox && !d.IsSample && d.Kind == kind && d.Status == DocumentStatus.NeedsReview
                        && (d.StatusReason == NewKind || d.StatusReason == ChooseFlow))
            .OrderBy(d => d.CreatedAt)
            .Take(ResortBatch)
            .ToListAsync(ct);
        var moved = new List<Guid>();
        foreach (var doc in waiting)
            if (await SortAsync(db, doc, ct)) moved.Add(doc.Id);
        return moved;
    }
}
