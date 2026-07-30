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
    /// it as null. It IS consumed by the transform path (launch batch 4A): when an order carries no
    /// usable per-order template/output override, <c>OrderTransformService</c> (and the
    /// mapping-override preview / replay current side) apply this promoted output mapping; the
    /// per-order override always stays the higher-priority seam, and a malformed/unusable value
    /// falls back to the fixed transformer.
    /// </para>
    /// </summary>
    public OutputMappingConfig? Output { get; init; }

    /// <summary>
    /// Optional reusable STRUCTURED OUTPUT TEMPLATE (the OutputNode AST) promoted from a per-order
    /// <see cref="OrderMappingOverride.OutputTree"/> so the supplier's exact required DOCUMENT — its
    /// nesting, repeating groups, attributes, namespaces and <see cref="OutputNode.IncludeWhen"/>
    /// conditionals — persists across re-uploads. Null (the default) means no supplier-level output
    /// tree: the flat <see cref="Output"/> mapping (if any) or the fixed transformers stay in
    /// control, byte-for-byte identical to today.
    ///
    /// <para>
    /// Additive + safe, EXACTLY like <see cref="Output"/>: this property serialises into the SAME
    /// <c>SupplierPoMapping.ConfigJson</c> JSONB column (no new table, no EF migration). Older
    /// configs that predate it simply deserialise it as null. It IS consumed by the transform path:
    /// when an order carries no usable per-order tree/template/output override,
    /// <c>OrderTransformService</c> renders this promoted tree through <c>OutputTemplateEmitter</c>.
    /// The per-order override always stays the higher-priority seam, this tree outranks the flat
    /// <see cref="Output"/> (mirroring the per-order precedence), and an unreadable/unusable value
    /// falls back to the fixed transformer instead of failing the order.
    /// </para>
    ///
    /// <para>
    /// Before this existed the visual output designer's work died with the order it was designed on:
    /// the operator built a supplier's document, it delivered correctly, and the very next order from
    /// that supplier silently reverted to the fixed transformer.
    /// </para>
    /// </summary>
    public OutputNodeTemplate? OutputTree { get; init; }
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
