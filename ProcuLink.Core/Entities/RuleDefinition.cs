namespace ProcuLink.Core.Entities;

/// <summary>
/// Group V4 — a reusable, org-level validation rule TEMPLATE. This is the first-class home for
/// what used to live in two disconnected places: the DESCRIPTIVE global rule catalog (docs /
/// non-executable) and the EXECUTABLE free-floating <see cref="SupplierAcceptanceRule"/> rows.
///
/// <para>
/// A <see cref="RuleDefinition"/> describes WHAT a rule checks (field, operator, default severity,
/// the standards references that make it trustworthy to procurement veterans, and human-readable
/// title / description / param hints). A supplier's executable rules then BIND to a definition by
/// id/code (see <see cref="SupplierAcceptanceRule.RuleDefinitionId"/> / <c>RuleCode</c>), carrying
/// only the per-binding overrides (a different expected value or severity for this supplier).
/// </para>
///
/// <para>
/// <b>The executor does NOT read this table.</b> <see cref="SupplierAcceptanceProfile"/> rules
/// remain the source of truth for evaluation — only the SOURCE of those rules changes (a definition
/// + binding instead of a hand-typed row). This deliberately AVOIDS building a second rules engine
/// (North Star V4: "bind, don't re-implement").
/// </para>
///
/// Org-scoped: each org owns its own catalog (UNIQUE(org_id, code)). The well-known global-catalog
/// rules are SEEDED as definitions per org so every org starts with a usable catalog it can extend.
/// </summary>
public class RuleDefinition
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }

    /// <summary>
    /// Stable, machine-friendly identifier UNIQUE within an org (e.g. <c>supplierItemCode.required</c>).
    /// This is the resilient join key bindings denormalise, so a binding survives even if a definition
    /// row is regenerated. Derived as <c>{fieldPath}.{operator}</c> for the seeded / backfilled rules.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable title (e.g. "Supplier item code is required").</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Longer description of what the rule checks and why it matters.</summary>
    public string? Description { get; set; }

    /// <summary>order | line — which part of the canonical model the rule applies to.</summary>
    public string Scope { get; set; } = "line";

    /// <summary>Canonical field the rule targets (e.g. supplierItemCode, quantity, currency).</summary>
    public string FieldPath { get; set; } = string.Empty;

    /// <summary>
    /// required | equals | in | min | max | not_equals | contains | greater_than | less_than | max_length.
    /// Exactly the operators the existing <see cref="SupplierAcceptanceService"/> executor understands —
    /// no new operators are introduced here.
    /// </summary>
    public string Operator { get; set; } = "required";

    /// <summary>warning | error — the severity a fresh binding inherits unless it overrides.</summary>
    public string DefaultSeverity { get; set; } = "error";

    /// <summary>
    /// Default expected value a fresh binding inherits (null for parameterless operators like
    /// <c>required</c>). Per-binding overrides live on the bound rule row, not here.
    /// </summary>
    public string? DefaultExpectedValue { get; set; }

    /// <summary>
    /// Free-form hint for the UI about the expected-value parameter (e.g. "ISO 4217 list, comma
    /// separated" for an <c>in</c> operator). Non-executable; aids the binding editor only.
    /// </summary>
    public string? ParamHint { get; set; }

    // ── Standards references (the trust surface; mirrors docs/standards-matrix.md) ─────────
    public string? UblRef { get; set; }
    public string? EdifactRef { get; set; }
    public string? X12Ref { get; set; }
    public string? CxmlRef { get; set; }

    /// <summary>
    /// True for the seeded well-known catalog definitions (vs. an org-authored custom definition).
    /// Lets the UI distinguish "ProcuLink standard rule" from "your rule" and lets the seeder skip
    /// re-creating its own rows idempotently.
    /// </summary>
    public bool IsSystem { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public Organisation Organisation { get; set; } = null!;
}
