using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Api.Services.Documents;

/// <summary>Publishing freezes a flow's fields and outputs as a version; restoring copies one back into the editable changes.</summary>
public static class FlowVersions
{
    private static async Task<FlowSnapshot?> StoredAsync(ProcuLinkDbContext db, Flow flow, int versionNo, CancellationToken ct)
    {
        var json = await db.Set<FlowVersion>().AsNoTracking()
            .Where(v => v.OrgId == flow.OrgId && v.FlowId == flow.Id && v.VersionNo == versionNo)
            .Select(v => v.SnapshotJson).FirstOrDefaultAsync(ct);
        return json is null ? null : DocumentJson.Deserialize<FlowSnapshot>(json);
    }

    /// <summary>Compared as snapshots, never as the stored text: jsonb rewrites whitespace and key order.</summary>
    public static async Task<bool> HasChangesAsync(ProcuLinkDbContext db, Flow flow, CancellationToken ct)
    {
        if (flow.PublishedVersionNo is not { } no || await StoredAsync(db, flow, no, ct) is not { } stored) return false;
        var live = await FlowConfigLoader.LiveAsync(db, flow, ct);
        return stored.Canonical() != FlowSnapshot.Of(flow, live.Outputs).Canonical();
    }

    public static async Task<(FlowVersion? Version, string? Error)> PublishAsync(ProcuLinkDbContext db, Flow flow, string? userId, CancellationToken ct)
    {
        if (flow.PublishedVersionNo is not null && !await HasChangesAsync(db, flow, ct)) return (null, "nothing_to_publish");
        var previous = flow.PublishedVersionNo;
        var live = await FlowConfigLoader.LiveAsync(db, flow, ct);
        var version = new FlowVersion
        {
            Id = Guid.NewGuid(), OrgId = flow.OrgId, FlowId = flow.Id, VersionNo = (previous ?? 0) + 1,
            SnapshotJson = FlowSnapshot.Of(flow, live.Outputs).Canonical(), PublishedAt = DateTime.UtcNow, PublishedBy = userId,
        };
        db.Set<FlowVersion>().Add(version);
        flow.PublishedVersionNo = version.VersionNo;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
        {
            db.Entry(version).State = EntityState.Detached;
            flow.PublishedVersionNo = previous;
            return (null, "publish_conflict");
        }
        return (version, null);
    }

    /// <summary>Null when restored. Writes the changes only: it never publishes.</summary>
    public static async Task<string?> RestoreAsync(ProcuLinkDbContext db, Flow flow, int versionNo, CancellationToken ct)
    {
        if (await StoredAsync(db, flow, versionNo, ct) is not { } snap) return "version_not_found";

        var destinations = snap.Outputs.Select(o => o.DestinationId).OfType<Guid>().ToHashSet();
        var lookups = snap.Outputs.SelectMany(o => DocumentLookups.UsedBy(DocumentJson.Deserialize<OutputNodeTemplate>(o.TemplateJson))).ToHashSet();
        if ((destinations.Count > 0 && await db.Set<FlowDestination>().CountAsync(d => d.OrgId == flow.OrgId && destinations.Contains(d.Id), ct) != destinations.Count)
            || (lookups.Count > 0 && await db.Set<Lookup>().CountAsync(l => l.OrgId == flow.OrgId && lookups.Contains(l.Id), ct) != lookups.Count))
            return "version_references_missing";

        var now = DateTime.UtcNow;
        flow.FieldsJson = DocumentJson.Serialize(snap.Fields);
        flow.FieldsConfirmed = snap.FieldsConfirmed;
        flow.UpdatedAt = now;

        var live = await db.Set<FlowOutput>().Where(o => o.OrgId == flow.OrgId && o.FlowId == flow.Id).ToListAsync(ct);
        db.Set<FlowOutput>().RemoveRange(live.Where(o => snap.Outputs.All(s => s.Id != o.Id)));
        foreach (var s in snap.Outputs)
        {
            var o = live.FirstOrDefault(x => x.Id == s.Id);
            if (o is null)
            {
                o = s.ToEntity(flow.OrgId, flow.Id);
                o.CreatedAt = now;
                db.Set<FlowOutput>().Add(o);
            }
            (o.Name, o.Format, o.TemplateJson, o.TableKey, o.DestinationId, o.SendWhenJson, o.LayoutJson, o.Logo) =
                (s.Name, s.Format, s.TemplateJson, s.TableKey, s.DestinationId, s.SendWhenJson, s.LayoutJson, s.Logo);
            o.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>The outputs every flow's live version sends, so a destination or lookup they use cannot be deleted.</summary>
    public static async Task<IReadOnlyList<FlowSnapshotOutput>> LiveVersionOutputsAsync(ProcuLinkDbContext db, Guid orgId, CancellationToken ct)
    {
        var json = await db.Set<FlowVersion>().AsNoTracking()
            .Where(v => v.OrgId == orgId && db.Flows.Any(f => f.Id == v.FlowId && f.PublishedVersionNo == v.VersionNo))
            .Select(v => v.SnapshotJson).ToListAsync(ct);
        return json.SelectMany(j => DocumentJson.Deserialize<FlowSnapshot>(j).Outputs).ToList();
    }
}
