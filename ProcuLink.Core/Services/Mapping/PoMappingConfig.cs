namespace ProcuLink.Core.Services.Mapping;

public record PoMappingConfig
{
    public bool HasHeaderRecord { get; init; } = true;
    public string Separator { get; init; } = ",";
    /// <summary>Maps canonical header field names to column mapping entry.</summary>
    public Dictionary<string, FieldMappingEntry> Header { get; init; } = new();
    /// <summary>Maps canonical line field names to column mapping entry.</summary>
    public Dictionary<string, FieldMappingEntry> Lines { get; init; } = new();
}

public record FieldMappingEntry
{
    /// <summary>Source column name in the supplier CSV. Null if using FixedValue.</summary>
    public string? ExternalField { get; init; }
    /// <summary>Constant value to use when no external column exists.</summary>
    public string? FixedValue { get; init; }
    public List<ManipulatorEntry> FieldManipulators { get; init; } = new();
}

public record ManipulatorEntry
{
    /// <summary>Manipulator type name, e.g. "Replace", "Trim", "DateFormat".</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>Ordered parameters for the manipulator.</summary>
    public List<string> Params { get; init; } = new();
}
