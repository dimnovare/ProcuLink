namespace ProcuLink.Core.Entities;

/// <summary>
/// Phase 2 Tier-2 "extensible canonical": a user-defined canonical field added to the
/// spine WITHOUT a per-field migration. Scoped to an org and (optionally) a single
/// <see cref="SupplierConnection"/> so one supplier's custom fields don't leak to another.
/// VALUES are NOT stored here — they ride on the existing per-order
/// <c>OrderMappingOverride.CustomFields</c> mechanism (header <c>Value</c> / line
/// <c>LineValues</c>), keyed by <see cref="Key"/>. This row is the DEFINITION only
/// (label/scope/type/order/standards) so the mapper can render the field as a wireable
/// node and validate it. Removal is a SOFT DELETE (<see cref="DeletedAt"/>) so pinned
/// revisions keep a stable view of the field set. Table <c>canonical_field_defs</c>
/// (migration <c>AddCanonicalFieldDefs</c>).
/// </summary>
public class CanonicalFieldDef
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }

    /// <summary>Null = org-wide custom field; set = scoped to one supplier connection.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>Machine key referenced from an OutputFieldRule / CustomField (e.g. "incoterms2").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable label for the mapper UI.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>"header" | "line" — one order-level value vs per-line values.</summary>
    public string Scope { get; set; } = "header";

    /// <summary>"string" | "number" | "date" | "bool" — drives validation + numeric exposure.</summary>
    public string Type { get; set; } = "string";

    /// <summary>Optional standards reference (e.g. UBL "cbc:CustomizationID"), surfaced on demand.</summary>
    public string? StandardsRef { get; set; }

    /// <summary>Stable display order in the canonical pane (ascending).</summary>
    public int Order { get; set; }

    /// <summary>Soft-delete marker. Non-null = removed; pinned revisions still see the def.</summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
