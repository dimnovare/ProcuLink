namespace ProcuLink.Core.Entities;

/// <summary>
/// The immutable, versioned configuration bundle for a <see cref="SupplierConnection"/>
/// (Group V1). This is the unit the North Star describes: input/parse mapping, output
/// template, product-code mapping, delivery channel + credentials, and validation
/// binding, all snapshotted as one publishable revision.
///
/// <para>
/// <b>This entity's <see cref="Id"/> is the <c>ConnectionRevisionId</c> pinned onto every
/// order at ingest</b> — the defining reproducibility invariant. Once a revision is
/// <c>published</c> it is immutable and retained forever, because orders pin to it.
/// </para>
///
/// Bundle components are stored as first-class columns / child rows (NOT folded into
/// <c>canonical_json</c>): structural identity + lifecycle live in columns; the
/// already-JSON config blobs the engine deserializes are carried as dedicated jsonb
/// columns so the existing typed readers can be re-pointed with near-zero reshaping.
/// Catalog binding is a live reference in V1 (see <see cref="CatalogMode"/>).
/// </summary>
public class SupplierConnectionRevision
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }

    /// <summary>Denormalised for tenant-scoped queries + index (mirrors how orders carry org_id).</summary>
    public Guid OrgId { get; set; }

    /// <summary>Denormalised for ingest-time "resolve active revision" lookup without a join.</summary>
    public Guid SupplierId { get; set; }

    /// <summary>Monotonic per connection (UNIQUE(connection_id, version_no)).</summary>
    public int VersionNo { get; set; }

    /// <summary>draft | test | published | archived</summary>
    public string Status { get; set; } = "draft";

    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime? PublishedAt { get; set; }

    public string? CreatedBy { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    // ── Bundle: input / parse mapping ────────────────────────────────────────
    /// <summary>Snapshot of <c>SupplierPoMapping.ConfigJson</c> (= serialized PoMappingConfig). Null = none.</summary>
    public string? InputMappingJson { get; set; }

    // ── Bundle: output template / field-map ──────────────────────────────────
    /// <summary>Snapshot of the assigned org <c>OutputTemplate.ConfigJson</c>, else null (= fixed transformer).</summary>
    public string? OutputMappingJson { get; set; }

    /// <summary>'xml' | 'csv' | 'cxml' | 'json' | 'ubl' | 'x12' — from SupplierDeliveryConfig.OutputFormat.</summary>
    public string? OutputFormat { get; set; }

    // ── Bundle: delivery channel + credentials ───────────────────────────────
    /// <summary>'http' | 'sftp' | 'ftp' | 'erp_erply' | 'erp_directo'; null = no delivery configured.</summary>
    public string? DeliveryProtocol { get; set; }

    /// <summary>Non-secret delivery config jsonb (endpoint, host, path, headers, timeout…).</summary>
    public string? DeliveryConfigJson { get; set; }

    public bool DeliveryAutoDeliver { get; set; }

    /// <summary>Authenticated-encrypted credential payload snapshot (empty/null = none).</summary>
    public string? CredentialsRef { get; set; }

    // ── Bundle: validation binding (bind, don't copy) ────────────────────────
    public Guid? AcceptanceProfileId { get; set; }
    public int? AcceptanceVersionNo { get; set; }

    // ── Bundle: catalog binding ──────────────────────────────────────────────
    /// <summary>'live' in V1 — the catalog stays mutable and is read live at ingest (no snapshot).</summary>
    public string CatalogMode { get; set; } = "live";

    // Navigation
    public SupplierConnection Connection { get; set; } = null!;
    public Organisation Organisation { get; set; } = null!;
    public List<ConnectionRevisionItemMapping> ItemMappings { get; set; } = new();
    public List<ConnectionRevisionTestCase> TestCases { get; set; } = new();
}
