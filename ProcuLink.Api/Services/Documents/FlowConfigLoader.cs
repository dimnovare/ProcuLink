using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

public sealed record FlowConfig(FlowFieldList Fields, bool FieldsConfirmed, IReadOnlyList<FlowOutput> Outputs, int? VersionNo);

/// <summary>The one place processing reads a flow's setup: the published snapshot, or the live tables when never published.</summary>
public static class FlowConfigLoader
{
    public static async Task<FlowConfig> LiveAsync(ProcuLinkDbContext db, Flow flow, CancellationToken ct)
    {
        var outputs = await db.Set<FlowOutput>().Where(o => o.FlowId == flow.Id && o.OrgId == flow.OrgId).ToListAsync(ct);
        return new FlowConfig(DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson), flow.FieldsConfirmed,
            outputs.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToList(), null);
    }

    public static async Task<FlowConfig> CurrentAsync(ProcuLinkDbContext db, Flow flow, CancellationToken ct)
    {
        if (flow.PublishedVersionNo is not { } no) return await LiveAsync(db, flow, ct);
        var json = await db.Set<FlowVersion>().Where(v => v.FlowId == flow.Id && v.OrgId == flow.OrgId && v.VersionNo == no)
            .Select(v => v.SnapshotJson).SingleAsync(ct);
        var snap = DocumentJson.Deserialize<FlowSnapshot>(json);
        return new FlowConfig(snap.Fields, snap.FieldsConfirmed,
            snap.Outputs.Select(o => o.ToEntity(flow.OrgId, flow.Id)).ToList(), no);
    }
}
