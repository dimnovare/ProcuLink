namespace ProcuLink.Core.Services.Mapping;

public record PoMappingConfig
{
    public bool HasHeaderRecord { get; init; } = true;
    public string Separator { get; init; } = ",";
    /// <summary>Maps canonical header field names to column mapping entry.</summary>
    public Dictionary<string, FieldMappingEntry> Header { get; init; } = new();
    /// <summary>Maps canonical line field names to column mapping entry.</summary>
    public Dictionary<string, FieldMappingEntry> Lines { get; init; } = new();

    /// <summary>
    /// Optional reusable OUTPUT mapping (canonical → output-field path) promoted from a per-order
    /// <see cref="OrderMappingOverride.Output"/> so the supplier's preferred output layout persists
    /// across re-uploads. Null (the default) means no supplier-level output override — the existing
    /// fixed transformers stay in control, byte-for-byte identical to today.
    ///
    /// <para>
    /// Additive + safe: this property serialises into the SAME <c>SupplierPoMapping.ConfigJson</c>
    /// JSONB column (no new table, no EF migration). Older configs that predate it simply deserialise
    /// it as null. It is NOT consumed by the transform path yet (the per-order override remains the
    /// only output-divert seam in <c>OrderTransformService</c>); persisting it here makes the founder's
    /// "Save mappings" button actually save the output side and report it, removing the silent no-op.
    /// Wiring re-upload consumption is a separate follow-up.
    /// </para>
    /// </summary>
    public OutputMappingConfig? Output { get; init; }
}

public record FieldMappingEntry
{
    /// <summary>Source column name in the supplier CSV. Null if using FixedValue.</summary>
    public string? ExternalField { get; init; }
    /// <summary>Constant value to use when no external column exists.</summary>
    public string? FixedValue { get; init; }
    public List<ManipulatorEntry> FieldManipulators { get; init; } = new();

    /// <remarks>At runtime, at least one of <see cref="ExternalField"/> or <see cref="FixedValue"/> should be set; if both are null and no manipulators exist, the resolved field value will be null. <c>PoMappingEngine</c> enforces this at apply time.</remarks>
}

public record ManipulatorEntry
{
    /// <summary>Manipulator type name, e.g. "Replace", "Trim", "DateFormat". Must not be empty-string at runtime — <c>ManipulatorRegistry</c> will throw <see cref="InvalidOperationException"/> for unknown types.</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>Ordered parameters for the manipulator.</summary>
    public List<string> Params { get; init; } = new();
}
