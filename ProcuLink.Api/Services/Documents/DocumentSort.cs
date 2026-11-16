using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>Sorts a document out of the workspace inbox into the person flow it belongs to. Nothing here saves.</summary>
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

    /// <summary>Moves the document to an inbox-sorted target, or leaves it waiting with its reason. True when it moved.</summary>
    public static async Task<bool> SortAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct)
    {
        var (target, waitReason) = await DecideAsync(db, doc, ct);
        if (target is null)
        {
            doc.StatusReason = waitReason;
            return false;
        }
        MoveTo(db, doc, target.FlowId, target.FlowName, target.Certain, Sorted, null);
        return true;
    }

    /// <summary>Puts the document in <paramref name="flowId"/> to be read there, marked unconfirmed unless <paramref name="certain"/>.</summary>
    public static void MoveTo(ProcuLinkDbContext db, FlowDocument doc, Guid flowId, string flowName, bool certain, string action, string? userId)
    {
        var fromFlowId = doc.FlowId;
        doc.FlowId = flowId;
        doc.Status = DocumentStatus.Received;
        doc.StatusReason = null;
        doc.ChecksJson = "[]";
        doc.SortUnconfirmed = !certain;
        doc.UpdatedAt = DateTime.UtcNow;
        DocumentAudit.Add(db, doc, action, new { fromFlowId, flowId, flow = flowName, certain }, userId);
    }

    /// <summary>Remembers the document's person flow for its sender and kind; the last write wins.</summary>
    public static async Task RememberAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct)
    {
        if (doc.SenderKey is null || doc.Kind is null) return;
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

    /// <summary>Sorts again up to 50 inbox documents of this kind that wait for a person. Returns the ids moved; the caller saves, then enqueues their reads.</summary>
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
