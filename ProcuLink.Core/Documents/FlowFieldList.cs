namespace ProcuLink.Core.Documents;

public sealed record FlowField(string Key, string Label, string Type, string? Meaning, bool Required);

public sealed record FlowTable(string Key, string Label, IReadOnlyList<FlowField> Columns);

/// <summary>Missing collections in stored JSON come back empty, never null.</summary>
public sealed record FlowFieldList(IReadOnlyList<FlowField>? Fields = null, IReadOnlyList<FlowTable>? Tables = null)
{
    public IReadOnlyList<FlowField> Fields { get; } = Fields ?? [];
    public IReadOnlyList<FlowTable> Tables { get; } = Tables ?? [];

    public static FlowFieldList Empty { get; } = new();
}
