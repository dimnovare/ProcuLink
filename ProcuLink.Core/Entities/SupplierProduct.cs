namespace ProcuLink.Core.Entities;

/// <summary>
/// A single product a supplier/distributor can actually fulfil — the authoritative
/// "ground truth" set of real codes for one (org, supplier).
///
/// Distinct from <see cref="ItemMapping"/>: an ItemMapping records a learned buyer→supplier
/// resolution, whereas a SupplierProduct records that a supplier code provably EXISTS.
/// The catalog is optional; when a supplier has none, behaviour is unchanged (mappings only).
///
/// Tenancy mirrors <see cref="ItemMapping"/> exactly: every query is scoped to
/// (OrgId, SupplierId) — never cross-tenant.
/// </summary>
public class SupplierProduct
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>The supplier's real product code (the value the AI may legitimately suggest). Required.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Product name / description as the supplier calls it.</summary>
    public string? Name { get; set; }

    /// <summary>Unit of measure (PCS / M / KG …).</summary>
    public string? Unit { get; set; }

    /// <summary>Optional list price; advisory only.</summary>
    public decimal? Price { get; set; }

    /// <summary>ISO-4217 currency, pairs with <see cref="Price"/>.</summary>
    public string? Currency { get; set; }

    /// <summary>GTIN / EAN / UPC — a strong exact-match key when buyers carry it.</summary>
    public string? Barcode { get; set; }

    /// <summary>Source system identifier (e.g. ERP product id) for idempotent re-sync.</summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The MANUFACTURER's part number for this product, exactly as the supplier's feed prints it
    /// (e.g. <c>LTQ2500-BK-BTK1</c>). Distinct from <see cref="Code"/>: a distributor sells the
    /// same manufacturer part under its own internal code. This is the only key that survives a
    /// punchout order, where the buyer's SupplierPartID is the buying network's internal id and
    /// matches nothing. Never used for comparison directly — compare on
    /// <see cref="ManufacturerPartNumberNormalized"/>.
    /// </summary>
    public string? ManufacturerPartNumber { get; set; }

    /// <summary>
    /// <see cref="ManufacturerPartNumber"/> run through
    /// <see cref="ProcuLink.Core.Catalog.ProductKeyNormalizer.Normalize"/> (upper-cased, every
    /// non-alphanumeric character stripped). Persisted rather than computed at query time so the
    /// lookup is served by a plain btree index instead of a full scan with a function applied to
    /// every row. Written by <c>ISupplierCatalogService.UpsertManyAsync</c> — no caller sets it.
    /// </summary>
    public string? ManufacturerPartNumberNormalized { get; set; }

    /// <summary>
    /// The manufacturer / brand (e.g. <c>Litware</c>). Advisory: shown to the operator alongside
    /// a manufacturer-part-number match so they can sanity-check it at a glance, and passed to the
    /// AI as candidate evidence. Deliberately NOT part of the match predicate — feeds spell the
    /// same brand a dozen ways ("Litware" / "LITWARE ADC" / "Litware S.p.A.") and requiring
    /// it to agree would reject correct matches.
    /// </summary>
    public string? ManufacturerName { get; set; }

    /// <summary>Discontinued products stay for audit but are excluded from active listings.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
