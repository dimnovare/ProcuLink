namespace ProcuLink.Core.Entities;

/// <summary>
/// One structured acceptance rule inside a profile version — and (Group V4) a versioned BINDING
/// to a reusable <see cref="RuleDefinition"/>. The rule's own <see cref="Scope"/> /
/// <see cref="FieldPath"/> / <see cref="Operator"/> / <see cref="ExpectedValue"/> /
/// <see cref="Severity"/> remain the SOURCE OF TRUTH the executor reads, so evaluation is
/// byte-identical whether or not a definition is bound. When bound, those columns ARE the
/// per-binding override values: a binding inherits the definition's defaults at creation time but
/// may diverge here (e.g. a stricter expected value or a downgraded severity for this supplier).
/// </summary>
public class SupplierAcceptanceRule
{
    public Guid    Id            { get; set; }
    public Guid    ProfileId     { get; set; }
    /// <summary>order | line</summary>
    public string  Scope         { get; set; } = "line";
    /// <summary>e.g. supplierItemCode, quantity, unitPrice, currency, buyerName</summary>
    public string  FieldPath     { get; set; } = string.Empty;
    /// <summary>required | equals | in | min | max</summary>
    public string  Operator      { get; set; } = "required";
    public string? ExpectedValue { get; set; }
    /// <summary>warning | error</summary>
    public string  Severity      { get; set; } = "error";
    public bool    BlockOnFail   { get; set; }

    // ── Group V4: rule-definition binding (optional; the executor never reads these) ──────────
    /// <summary>
    /// The reusable <see cref="RuleDefinition"/> this rule binds to, if any. Null = a legacy /
    /// free-floating rule (the backfill links existing rows to a definition; new rows created from a
    /// definition set this). The binding does NOT change evaluation — the executor still reads the
    /// scalar rule columns above.
    /// </summary>
    public Guid?   RuleDefinitionId { get; set; }

    /// <summary>
    /// Denormalised <see cref="RuleDefinition.Code"/> for resilient display/grouping even if the
    /// definition row is later regenerated. Null when unbound.
    /// </summary>
    public string? RuleCode      { get; set; }

    public SupplierAcceptanceProfile Profile { get; set; } = null!;
}
