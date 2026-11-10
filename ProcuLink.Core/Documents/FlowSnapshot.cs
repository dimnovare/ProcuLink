using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Documents;

/// <summary>What a flow version freezes. No timestamps: precision changes across a DB round trip and would make every flow look changed.</summary>
public sealed record FlowSnapshot(FlowFieldList Fields, bool FieldsConfirmed, IReadOnlyList<FlowSnapshotOutput> Outputs)
{
    public static FlowSnapshot Of(Flow flow, IEnumerable<FlowOutput> outputs) => new(
        DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson),
        flow.FieldsConfirmed,
        outputs.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new FlowSnapshotOutput(o.Id, o.Name, o.Format, o.TemplateJson, o.TableKey,
                o.DestinationId, o.SendWhenJson, o.LayoutJson, o.Logo))
            .ToList());

    public string Canonical() => DocumentJson.Serialize(this);
}

public sealed record FlowSnapshotOutput(Guid Id, string Name, string Format, string TemplateJson, string? TableKey,
    Guid? DestinationId, string? SendWhenJson, string? LayoutJson, byte[]? Logo)
{
    /// <summary>An untracked entity.</summary>
    public FlowOutput ToEntity(Guid orgId, Guid flowId)
    {
        var o = new FlowOutput { Id = Id, OrgId = orgId, FlowId = flowId };
        CopyTo(o);
        return o;
    }

    /// <summary>The one list of the columns a version freezes.</summary>
    public void CopyTo(FlowOutput o)
    {
        o.Name = Name; o.Format = Format; o.TemplateJson = TemplateJson; o.TableKey = TableKey;
        o.DestinationId = DestinationId; o.SendWhenJson = SendWhenJson; o.LayoutJson = LayoutJson; o.Logo = Logo;
    }
}
