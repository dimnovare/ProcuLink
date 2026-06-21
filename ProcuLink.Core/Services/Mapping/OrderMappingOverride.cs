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

    /// <summary>
    /// Optional source→canonical remapping (SourceMap engine). When present, the effective
    /// canonical value for each named field is derived from a source token, a fixed value, or
    /// the existing parsed value — then run through the manipulator chain. Null = no remapping,
    /// the existing parsed canonical values are used unchanged (default, byte-for-byte identical).
    ///
    /// Recognised field keys (header): PoNumber, OrderDate, BuyerName, Currency, SupplierName.
    /// Recognised field keys (line): LineNumber, BuyerItemCode, SupplierItemCode, Description,
    /// Quantity, Unit, UnitPrice, LineTotal.
    /// </summary>
    public Dictionary<string, SourceFieldRule>? SourceMap { get; init; }

    /// <summary>
    /// Optional WHOLE-DOCUMENT Scriban template (heart-piece-flex flexible mapping — template mode).
    /// When present and non-blank, the transform renders this single template string against the
    /// canonical order to produce the ENTIRE output document, instead of building it field-by-field
    /// from <see cref="Output"/>. This lets a supplier's exact required structure (e.g. an Ingram
    /// Micro-style nested JSON with a <c>lines</c> array) be expressed directly:
    /// <code>
    /// {"customerOrderNumber":"{{ OrderNr }}","shipToInfo":{"city":"{{ ShippingAddress.City }}"},
    ///  "lines":[{{ for Line in Lines }}{"quantity":{{ Line.Qty }}}{{ if !for.last }},{{ end }}{{ end }}]}
    /// </code>
    /// The template sees the stable model namespace documented in
    /// <c>docs/qa/2026-06-09-scriban-template-namespace.md</c> (top-level globals OrderNr, OrderDate,
    /// Currency, BuyerName, SupplierName, ShippingAddress, CustomFields, Lines, plus intuitive aliases
    /// such as <c>Line.DistributorPid</c> / <c>Line.OrderedPrice</c> / <c>Line.Qty</c>).
    ///
    /// <para><b>Opt-in &amp; safe.</b> Null/blank (the default) → template mode is OFF and behaviour is
    /// byte-for-byte identical to today (fixed transformer, or the field-by-field <see cref="Output"/>
    /// override when present). A compile/render error never crashes the transform — it surfaces as a
    /// clear validation failure and the order stays un-transformed.</para>
    /// </summary>
    public string? OutputTemplate { get; init; }

    /// <summary>
    /// MIME content type to stamp on a template-mode artifact. Optional; defaults to
    /// <c>application/json</c> when <see cref="OutputTemplate"/> is set without an explicit type.
    /// Set e.g. <c>text/csv</c>, <c>application/xml</c>, or <c>text/plain</c> when the template
    /// produces that shape. Ignored when <see cref="OutputTemplate"/> is null/blank.
    /// </summary>
    public string? OutputTemplateContentType { get; init; }

    /// <summary>
    /// Optional STRUCTURED output template (Phase B — the OutputNode AST). When present, the transform
    /// renders the entire document from this recursive tree via <c>OutputTemplateEmitter</c>, letting a
    /// supplier's exact required STRUCTURE (nesting, repeating groups, attributes, custom names) be
    /// expressed VISUALLY — not just leaf-value remapping (<see cref="Output"/>) or a hand-written
    /// Scriban string (<see cref="OutputTemplate"/>). It is the HIGHEST-precedence output mode.
    ///
    /// <para><b>Opt-in &amp; safe.</b> Null (the default) → this mode is OFF and behaviour is byte-for-byte
    /// identical to today. Leaf values resolve through the SAME machinery as <see cref="Output"/>, so
    /// only the surrounding structure is new. An unresolved order fails the same review guard.</para>
    /// </summary>
    public OutputNodeTemplate? OutputTree { get; init; }
}

/// <summary>
/// One field's re-derive rule in the SourceMap engine: where the effective canonical value
/// comes from (a source token by id, a fixed literal, or — when both are null — the original
/// parsed value), optionally followed by a manipulator chain run with identical semantics to
/// <c>ManipulatorRegistry</c> / <c>PoMappingEngine.ResolveField</c>.
/// </summary>
public record SourceFieldRule
{
    /// <summary>
    /// Optional free-form Scriban expression (heart-piece-flex flexible mapping). When non-null and
    /// non-blank it takes PRECEDENCE over <see cref="SourceToken"/> and <see cref="FixedValue"/>:
    /// the value is the rendered expression, evaluated with the canonical order (and, for line-scope
    /// fields, the current line) in scope — e.g. <c>{{ order.PoNumber }}</c>,
    /// <c>{{ line.Quantity * line.UnitPrice }}</c>, <c>{{ order.Currency }}-{{ line.SupplierItemCode }}</c>.
    /// The result is then run through <see cref="Manipulators"/> exactly as a token/fixed value would
    /// be. Null (the default) → no expression, byte-for-byte identical to existing SourceMap rules.
    /// A compile/eval error never crashes the transform (the resolved value falls back per the
    /// evaluator contract and the line stays subject to the usual review guard).
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// Stable source-token id to use as the raw value input (e.g. "cell:r1c3" for CSV, or an XPath
    /// string for XML). When non-null and the token is found in the tokenizer output, its
    /// <c>Value</c> is used as the starting value before manipulators are applied. When null (or the
    /// token is not found in the current file), falls through to <see cref="FixedValue"/> and then
    /// to the original parsed value. Ignored when <see cref="Expression"/> is set.
    /// </summary>
    public string? SourceToken { get; init; }

    /// <summary>
    /// Constant value used when <see cref="SourceToken"/> is null or yields no match. When both
    /// <see cref="SourceToken"/> and <see cref="FixedValue"/> are null, the existing parsed canonical
    /// value is kept (pass-through), so omitting a field from the SourceMap is always safe.
    /// Ignored when <see cref="Expression"/> is set.
    /// </summary>
    public string? FixedValue { get; init; }

    /// <summary>
    /// Ordered manipulator chain applied to the resolved value. Uses the EXISTING
    /// <see cref="ManipulatorEntry"/> type; each entry is resolved through
    /// <c>ManipulatorRegistry</c> at re-derive time, identical semantics to the output mapping.
    /// </summary>
    public List<ManipulatorEntry> Manipulators { get; init; } = new();
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
    /// F-1 (bind ANY source field) — optional BARE source-token id (e.g. <c>"cell:r2c5"</c>, an XPath,
    /// an EDI segment address, a <c>raw:{label}</c>). The DIRECT TWIN of <see cref="SourceFieldRule.SourceToken"/>
    /// — same name, same semantics, same <c>src::</c> lookup — so there is ONE binding concept across the
    /// input and output sides. When set (and <see cref="Expression"/> is blank) the value is taken from the
    /// addressed source field: the resolver looks up <c>row["src::"+SourceToken]</c> (the row builder
    /// injects every source token under that reserved namespace). Precedence is
    /// <c>Expression → SourceToken → FixedValue → CanonicalField</c>. A token id that is NOT found in the
    /// bag falls through to <see cref="FixedValue"/>/<see cref="CanonicalField"/> (never crashes, never
    /// emits a literal "src::…"). Null (the default) → no token binding, byte-for-byte identical to today.
    /// </summary>
    public string? SourceToken { get; init; }

    /// <summary>
    /// Name of the canonical (or custom) field to source the value from. Null when using
    /// <see cref="FixedValue"/> only. Recognised canonical names mirror the fixed transformers
    /// (header: PoNumber/OrderDate/BuyerName/Currency/SupplierName; line:
    /// LineNumber/BuyerItemCode/SupplierItemCode/Description/Quantity/Unit/UnitPrice/LineTotal).
    /// Any other name is resolved against the override's CustomFields by key.
    /// </summary>
    public string? CanonicalField { get; init; }

    /// <summary>Constant value used when no canonical/custom field applies. Null if using a field. Ignored when <see cref="Expression"/> is set.</summary>
    public string? FixedValue { get; init; }

    /// <summary>
    /// Ordered manipulator chain applied to the resolved value. REUSES the existing
    /// <see cref="ManipulatorEntry"/> type (no duplication); each entry is resolved through
    /// <c>ManipulatorRegistry</c> at transform time, identical to the per-supplier mapping engine.
    /// </summary>
    public List<ManipulatorEntry> FieldManipulators { get; init; } = new();
}
