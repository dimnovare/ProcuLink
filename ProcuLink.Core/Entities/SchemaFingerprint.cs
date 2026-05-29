namespace ProcuLink.Core.Entities;

/// <summary>
/// Org-scoped record of a column-header layout this organisation has successfully parsed
/// before. Each successful <c>ParseOrderJob</c> run upserts the row for its source file's
/// column layout (incrementing <see cref="ParseSuccessCount"/>); the detect-format endpoint
/// looks the layout up by hash to answer "we've seen this layout N times" and boost confidence.
///
/// This is the first slice of the schema-fingerprint moat. It is <b>strictly org-scoped</b> —
/// the cross-org shared supplier/profile catalog (Horizon 3, Group Q) is explicitly out of scope.
/// </summary>
public class SchemaFingerprint
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation. Every query must filter by this — no cross-org reads.</summary>
    public Guid OrganisationId { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the file's column headers after trim + lowercase + ordinal sort.
    /// Order-independent and case-insensitive so the same layout always hashes identically.
    /// Unique per organisation.
    /// </summary>
    public string ColumnNameHash { get; set; } = string.Empty;

    /// <summary>Format detected for this layout (e.g. <c>"csv"</c>) at the time it was first recorded.</summary>
    public string DetectedFormat { get; set; } = string.Empty;

    /// <summary>Best-effort supplier name associated with the layout when first seen. Display only; nullable.</summary>
    public string? SampleSupplierName { get; set; }

    /// <summary>Number of distinct orders this org has successfully parsed with this layout.</summary>
    public int ParseSuccessCount { get; set; }

    /// <summary>UTC timestamp of the most recent successful parse with this layout.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>UTC timestamp the fingerprint row was first created.</summary>
    public DateTime CreatedAt { get; set; }
}
