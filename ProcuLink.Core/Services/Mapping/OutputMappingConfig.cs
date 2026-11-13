namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// The output-side mapping: which canonical/custom value (and manipulator chain) produces each
/// output field. Header-scope fields appear once; line-scope fields are emitted per resolved line.
/// </summary>
public record OutputMappingConfig
{
    /// <summary>Maps an output field key → the rule that produces its header-level value.</summary>
    public Dictionary<string, OutputFieldRule> Header { get; init; } = new();

    /// <summary>Maps an output field key → the rule that produces its per-line value.</summary>
    public Dictionary<string, OutputFieldRule> Lines { get; init; } = new();
}

/// <summary>
/// One output field's recipe: where the output column lands (<see cref="OutputPath"/>), the source
/// value (a canonical field, a custom field, or a fixed literal), and an optional manipulator chain
/// applied by <c>MappedTransformService.ResolveRule</c>.
/// </summary>
public record OutputFieldRule
{
    /// <summary>The output column/path this rule writes (e.g. CSV header name, JSON property name).</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>
    /// Optional free-form Scriban expression (heart-piece-flex flexible mapping). When non-null and
    /// non-blank it takes PRECEDENCE over <see cref="CanonicalField"/> and <see cref="FixedValue"/>:
    /// the value is the rendered expression, evaluated with the canonical order in scope for a header
    /// rule, or the order + current line in scope for a line rule — e.g. <c>{{ order.PoNumber }}</c>,
    /// <c>{{ line.Quantity * line.UnitPrice }}</c>, <c>{{ order.Currency }}-{{ line.SupplierItemCode }}</c>.
    /// The rendered result is then run through <see cref="FieldManipulators"/> exactly as a
    /// canonical/fixed value would be. Null (the default) → no expression, byte-for-byte identical to
    /// existing output rules. A compile/eval error never crashes the transform.
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// An optional bare source-token id (e.g. <c>"cell:r2c5"</c>, an XPath, an EDI segment address).
    /// Still accepted on the wire, but nothing reads it: the order row builder that put source tokens
    /// into the value bag under <c>src::</c> was deleted, and no engine bag carries one (plan 13b,
    /// 2026-11-13), so a leaf's value comes from Expression, FixedValue or CanonicalField.
    /// </summary>
    public string? SourceToken { get; init; }

    /// <summary>
    /// Name of the canonical (or custom) field to source the value from. Null when using
    /// <see cref="FixedValue"/> only. Recognised canonical names mirror the fixed transformers
    /// (header: PoNumber/OrderDate/BuyerName/Currency/SupplierName; line:
    /// LineNumber/BuyerItemCode/SupplierItemCode/Description/Quantity/Unit/UnitPrice/LineTotal).
    /// Any other name is looked up in the value bag by key.
    /// </summary>
    public string? CanonicalField { get; init; }

    /// <summary>Constant value used when no canonical/custom field applies. Null if using a field. Ignored when <see cref="Expression"/> is set.</summary>
    public string? FixedValue { get; init; }

    /// <summary>Fixed-width column width in characters. Ignored by every other format.</summary>
    public int? Width { get; init; }

    /// <summary>
    /// Ordered manipulator chain applied to the resolved value. REUSES the existing
    /// <see cref="ManipulatorEntry"/> type (no duplication); each entry is resolved through
    /// <c>ManipulatorRegistry</c> at transform time.
    /// </summary>
    public List<ManipulatorEntry> FieldManipulators { get; init; } = new();
}
