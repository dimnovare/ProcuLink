namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Per-ORDER mapping/override layer (heart-piece-flex Phase 1). It sits BETWEEN the fixed
/// canonical model and the fixed output transforms WITHOUT changing the proven
/// parse → canonical → deliver path. The whole override is stored in the EXISTING
/// <c>purchase_orders.canonical_json</c> jsonb under the key <c>"mappingOverride"</c>
/// (no new DB table, no EF migration in v1). When an order has no override, behaviour is
/// byte-for-byte identical to today.
///
/// Two additive concepts:
/// <list type="number">
///   <item>
///     <description>
///       <b>Custom canonical fields</b> (<see cref="CustomFields"/>) — user-added
///       {key, label, scope, value | per-line values} that NEVER touch the typed columns,
///       so existing parsers/transforms are untouched.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Per-order output mapping override</b> (<see cref="Output"/>) — a canonical-field →
///       output-field-path map with optional <see cref="ManipulatorEntry"/> chains, reusing the
///       EXISTING <c>ManipulatorEntry</c> / <c>ManipulatorRegistry</c> model verbatim
///       (the "Scriban-style manipulate" surface — no new templating engine).
///     </description>
///   </item>
/// </list>
/// </summary>
public record OrderMappingOverride
{
    /// <summary>User-added custom canonical fields (header- or line-scoped). Never written to typed columns.</summary>
    public List<CustomField> CustomFields { get; init; } = new();

    /// <summary>
    /// Optional per-order output mapping. When present (and the format is supported), the
    /// override-aware transform path builds the output document from this config instead of
    /// the fixed columns. Null = use the existing fixed transformer unchanged.
    /// </summary>
    public OutputMappingConfig? Output { get; init; }
}

/// <summary>
/// A single user-added canonical field. Lives only in <c>canonical_json</c> + (optionally) the
/// override-driven output. It must never be written to a typed column or trip the resolve send-guard.
/// </summary>
public record CustomField
{
    /// <summary>Machine key used to reference this field from an <see cref="OutputFieldRule"/>.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable label for display on the review screen.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>"header" | "line" — whether the field carries one order-level value or per-line values.</summary>
    public string Scope { get; init; } = "header";

    /// <summary>Header-scope value (used when <see cref="Scope"/> is "header"). Null when unset.</summary>
    public string? Value { get; init; }

    /// <summary>
    /// Line-scope values keyed by line number (used when <see cref="Scope"/> is "line").
    /// Null when the field is header-scoped or has no per-line values yet.
    /// </summary>
    public Dictionary<int, string>? LineValues { get; init; }
}

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
/// applied with EXACTLY the same semantics as <c>PoMappingEngine.ResolveField</c>.
/// </summary>
public record OutputFieldRule
{
    /// <summary>The output column/path this rule writes (e.g. CSV header name, JSON property name).</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>
    /// Name of the canonical (or custom) field to source the value from. Null when using
    /// <see cref="FixedValue"/> only. Recognised canonical names mirror the fixed transformers
    /// (header: PoNumber/OrderDate/BuyerName/Currency/SupplierName; line:
    /// LineNumber/BuyerItemCode/SupplierItemCode/Description/Quantity/Unit/UnitPrice/LineTotal).
    /// Any other name is resolved against the override's CustomFields by key.
    /// </summary>
    public string? CanonicalField { get; init; }

    /// <summary>Constant value used when no canonical/custom field applies. Null if using a field.</summary>
    public string? FixedValue { get; init; }

    /// <summary>
    /// Ordered manipulator chain applied to the resolved value. REUSES the existing
    /// <see cref="ManipulatorEntry"/> type (no duplication); each entry is resolved through
    /// <c>ManipulatorRegistry</c> at transform time, identical to the per-supplier mapping engine.
    /// </summary>
    public List<ManipulatorEntry> FieldManipulators { get; init; } = new();
}
