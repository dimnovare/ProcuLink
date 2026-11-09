using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Core.Documents;

/// <summary>One place in a draft template that can take a flow value. <paramref name="Allowed"/> holds qualified keys: a header field is <c>key</c>, a table column is <c>tableKey.columnKey</c>.</summary>
public sealed record OutputSlot(string Path, string Name, IReadOnlyList<string> Allowed);

/// <summary>A proposed value for a slot. <paramref name="FieldKey"/> is qualified (see <see cref="OutputSlot"/>).</summary>
public sealed record SlotMatch(string Path, string FieldKey, float Confidence);

public sealed record DraftCite(string Where, string Quote);

/// <summary>
/// One leaf of a draft. <paramref name="FieldKey"/> is unqualified, as written into the rule.
/// <paramref name="By"/> says who chose the value: <c>"name"</c> (matched by name), <c>"ai"</c> (matched by the AI
/// from a sample) or <c>"docs"</c> (proposed from documentation, which carries no confidence); null when unset.
/// </summary>
public sealed record DraftLeaf(string Path, string? FieldKey, string? By, float? Confidence, bool Required = false, DraftCite? Cite = null);

public sealed record OutputDraft(string Name, string Format, string? TableKey, OutputNodeTemplate Template, IReadOnlyList<DraftLeaf> Leaves, IReadOnlyList<string> Notes);

public interface IOutputDesigner
{
    Task<IReadOnlyList<SlotMatch>> BindAsync(Guid orgId, IReadOnlyList<OutputSlot> slots, FlowFieldList fields, CancellationToken ct);
}
