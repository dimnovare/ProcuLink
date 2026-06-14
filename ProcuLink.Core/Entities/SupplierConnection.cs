namespace ProcuLink.Core.Entities;

/// <summary>
/// The stable aggregate root for a supplier integration (Group V1 — versioned
/// Supplier Connection). One <see cref="SupplierConnection"/> exists per
/// (org, supplier) integration; it is a durable handle that the
/// <see cref="ActiveRevisionId"/> pointer hangs off, so the live revision can change
/// without changing the connection identity.
///
/// All of a supplier's scattered, mutable config (PO mapping, output template,
/// item mappings, delivery channel, acceptance binding) is subsumed as
/// components of an immutable <see cref="SupplierConnectionRevision"/>; the
/// connection just points at whichever revision is currently published.
///
/// Cardinality decision (V1): one connection per supplier — matching the existing
/// UNIQUE(org_id, supplier_id) on every loose-config surface. Multi-connection per
/// supplier is deferred; the schema does not preclude it because the connection
/// carries its own id.
/// </summary>
public class SupplierConnection
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>Display name; defaults to the supplier's name at backfill/create time.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The live pointer to the currently-published revision. Null until the first
    /// publish (e.g. a connection that only has a draft). Nullable + the FK is added
    /// in a second migration step to resolve the connection↔revision circular FK.
    /// </summary>
    public Guid? ActiveRevisionId { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // ── Phase 2 connection-level price-variance guard (additive, defaulted OFF) ──
    /// <summary>When true, lines whose PO unit price drifts from the catalog price by
    /// more than <see cref="PriceVarianceThresholdPercent"/> are flagged NeedsReview and
    /// the order is HELD (pending_review). Catalog price is a SUGGESTION, never a silent
    /// overwrite. Default false = byte-identical to today.</summary>
    public bool PriceVarianceGuardEnabled { get; set; }

    /// <summary>Variance threshold in percent (e.g. 20 = ±20%). Only used when the guard
    /// is enabled. Default 0 (unused while disabled).</summary>
    public decimal PriceVarianceThresholdPercent { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
    // NOTE: there is intentionally NO `ActiveRevision` navigation property. Two navigations from
    // SupplierConnection to SupplierConnectionRevision (ActiveRevision + Revisions) created a
    // relationship ambiguity the EF model validator rejects. The active revision is loaded via an
    // explicit query keyed on ActiveRevisionId; the FK is still enforced (see DbContext config).
    public List<SupplierConnectionRevision> Revisions { get; set; } = new();
}
