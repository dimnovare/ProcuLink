namespace ProcuLink.Core.Entities;

public class OutboundArtifact
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrgId { get; set; }
    public string Format { get; set; } = string.Empty;
    public string FileKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // ── Provenance (launch batch 3 — nullable; legacy rows stay null) ────────────
    /// <summary>
    /// The <see cref="SupplierConnectionRevision"/> the order was pinned to when this artifact
    /// was generated (copied from the order at transform time). Plain column, no FK — provenance
    /// metadata mirroring <c>purchase_orders.connection_revision_id</c>. Null for legacy artifacts
    /// and zero-config suppliers.
    /// </summary>
    public Guid? ConnectionRevisionId { get; set; }
    /// <summary>
    /// SHA-256 hex digest of the EFFECTIVE transform config that produced this artifact:
    /// the per-order mapping-override JSON when an override/template drove the transform,
    /// else the marker string <c>fixed:{format}</c>. Null when provenance capture failed
    /// (capture is best-effort and never fails the transform).
    /// </summary>
    public string? ConfigDigest { get; set; }
    /// <summary>
    /// SHA-256 hex of the exact generated artifact bytes (corruption / tamper detection —
    /// a delivery can prove the payload it dispatched matches what the transform produced).
    /// Null for legacy artifacts or when hashing failed.
    /// </summary>
    public string? ArtifactSha256 { get; set; }

    // Navigation
    public PurchaseOrderEntity Order { get; set; } = null!;
    public Organisation Organisation { get; set; } = null!;
}
