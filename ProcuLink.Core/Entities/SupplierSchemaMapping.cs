namespace ProcuLink.Core.Entities;

/// <summary>
/// The supplier-scoped field-mapping moat. When this organisation successfully maps a CSV/XLSX
/// purchase order for a given supplier, the buyer→supplier item-code mapping observed for that
/// file's <b>column layout</b> is captured here, keyed by
/// <c>(OrganisationId, SupplierId, ColumnNameHash)</c>. A later upload of the <b>same layout for
/// the same supplier</b> looks the row up and pre-fills suggestions from the learned mapping —
/// alongside (never overriding) the deterministic item-mapping lookup and the AI suggester.
///
/// <para>
/// This is the field-mapping half of the schema-fingerprint moat and is a sibling of
/// <see cref="SchemaFingerprint"/> (which is the org-scoped format-confidence half). They share the
/// same canonical layout hash (<c>SchemaFingerprintHasher</c>) so a given physical layout always
/// resolves to the same row in both.
/// </para>
///
/// <para>
/// <b>Strictly org-scoped</b> — every read filters by <see cref="OrganisationId"/>. The cross-org
/// shared supplier/mapping catalog (Horizon 3, Group Q) is explicitly out of scope.
/// </para>
/// </summary>
public class SupplierSchemaMapping
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation. Every query must filter by this — no cross-org reads.</summary>
    public Guid OrganisationId { get; set; }

    /// <summary>
    /// Supplier this learned mapping belongs to. The lookup matches on
    /// <c>(OrganisationId, SupplierId, ColumnNameHash)</c>, so a layout learned for one supplier
    /// never pre-fills another supplier's order.
    /// </summary>
    public Guid SupplierId { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the file's column headers after trim + lowercase + ordinal sort,
    /// produced by <c>SchemaFingerprintHasher.ComputeColumnNameHash</c>. Order-independent and
    /// case-insensitive so the same layout always hashes identically. Unique per (org, supplier).
    /// </summary>
    public string ColumnNameHash { get; set; } = string.Empty;

    /// <summary>Format detected for this layout (e.g. <c>"csv"</c>) at the time it was first learned.</summary>
    public string DetectedFormat { get; set; } = string.Empty;

    /// <summary>
    /// jsonb — the learned field mapping for this shape: a JSON object whose keys are normalised
    /// buyer item codes and whose values are the resolved supplier item codes
    /// (e.g. <c>{"buyer-001":"SUP-A","buyer-002":"SUP-B"}</c>). Defaults to an empty object.
    /// </summary>
    public string FieldMappingJson { get; set; } = "{}";

    /// <summary>Best-effort order id the mapping was most recently learned from. Provenance only; nullable.</summary>
    public Guid? LearnedFromOrderId { get; set; }

    /// <summary>Number of times this (org, supplier, layout) mapping has been reinforced by a successful map.</summary>
    public int ObservationCount { get; set; }

    /// <summary>UTC timestamp of the most recent capture into this row.</summary>
    public DateTime LastLearnedAt { get; set; }

    /// <summary>UTC timestamp the row was first created.</summary>
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
