namespace ProcuLink.Core.Entities;

/// <summary>
/// Pull-sync configuration for one supplier's product catalog: where to fetch the catalog
/// file (SFTP / FTP / FTPS), how often, and the honest status of the last sync attempt.
/// ONE source per (org, supplier) — enforced by a unique index.
///
/// Credentials follow the delivery-config precedent: <see cref="EncryptedPassword"/> holds
/// an AES-256-GCM envelope produced by <c>DeliveryEncryptionService</c>; the plaintext is
/// write-only (GET responses mask it, PUT semantics are null=keep / ""=clear / value=re-encrypt).
///
/// Tenancy mirrors <see cref="SupplierProduct"/>: every query is scoped to OrgId.
/// </summary>
public class SupplierCatalogSource
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>'sftp' | 'ftp' | 'ftps'. Text column — 'http' is reserved for a later, code-only addition.</summary>
    public string Protocol { get; set; } = "sftp";

    public string Host { get; set; } = string.Empty;

    /// <summary>Defaults by protocol: 22 (sftp) / 21 (ftp, ftps).</summary>
    public int Port { get; set; } = 22;

    /// <summary>Required for sftp/ftps; plain ftp may be anonymous (null/empty).</summary>
    public string? Username { get; set; }

    /// <summary>AES-GCM envelope (DeliveryEncryptionService format). Null/empty = no password stored.</summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>Exact remote FILE path (no directory/glob selection in v1).</summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>'auto' | 'csv' | 'xlsx'. 'auto' routes on the remote file extension.</summary>
    public string FileFormat { get; set; } = "auto";

    /// <summary>Server-clamped to [1, 336] hours (14 days).</summary>
    public int SyncIntervalHours { get; set; } = 24;

    public bool IsEnabled { get; set; }

    public DateTime? LastSyncAt { get; set; }

    /// <summary>'running' | 'ok' | 'unchanged' | 'failed' (null = never synced).</summary>
    public string? LastSyncStatus { get; set; }

    /// <summary>≤500 chars; ONLY enumerated safe messages (M4) — never raw transport errors.</summary>
    public string? LastSyncError { get; set; }

    public int? LastSyncCreated { get; set; }
    public int? LastSyncUpdated { get; set; }
    public int? LastSyncSkipped { get; set; }

    /// <summary>SHA-256 hex of the last successfully processed file — the unchanged-skip key.</summary>
    public string? LastFileHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
