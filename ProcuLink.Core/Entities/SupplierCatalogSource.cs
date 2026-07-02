namespace ProcuLink.Core.Entities;

/// <summary>
/// Pull-sync configuration for one supplier's product catalog: where to fetch the catalog
/// file (SFTP / FTP / FTPS) or query the catalog over an HTTP(S) API, how often, and the
/// honest status of the last sync attempt. ONE source per (org, supplier) — enforced by a
/// unique index.
///
/// Credentials follow the delivery-config precedent: <see cref="EncryptedPassword"/> (the
/// sftp/ftp password) and <see cref="AuthConfigEncrypted"/> (the http auth-config blob) hold
/// AES-256-GCM envelopes produced by <c>DeliveryEncryptionService</c>; the plaintext is
/// write-only (GET responses mask it, PUT semantics are null=keep / ""=clear / value=re-encrypt).
///
/// Tenancy mirrors <see cref="SupplierProduct"/>: every query is scoped to OrgId.
/// </summary>
public class SupplierCatalogSource
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>'sftp' | 'ftp' | 'ftps' | 'http' | 'https' | 'logicom' (vendor fetcher).</summary>
    public string Protocol { get; set; } = "sftp";

    /// <summary>Host for sftp/ftp/ftps. Unused for http/https (the full URL is in <see cref="Url"/>).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Defaults by protocol: 22 (sftp) / 21 (ftp, ftps). Unused for http/https.</summary>
    public int Port { get; set; } = 22;

    /// <summary>Required for sftp/ftps; plain ftp may be anonymous (null/empty). Unused for http/https.</summary>
    public string? Username { get; set; }

    /// <summary>AES-GCM envelope (DeliveryEncryptionService format). Null/empty = no password stored.</summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>Exact remote FILE path (no directory/glob selection in v1). Unused for http/https.</summary>
    public string RemotePath { get; set; } = string.Empty;

    // ── HTTP(S) catalog pull (plan 2026-06-12 v2; null for sftp/ftp rows) ──────

    /// <summary>
    /// Full request URL (scheme+host+path+query) for http/https sources. Null for sftp/ftp/ftps.
    /// Credentials are NEVER embedded here (<c>Uri.UserInfo</c> is rejected at save) — they live
    /// in <see cref="AuthConfigEncrypted"/>.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// HTTP auth method for http/https sources:
    /// 'none' | 'apikey' | 'bearer' | 'basic' | 'oauth2_client_credentials'. Null for sftp/ftp.
    /// </summary>
    public string? AuthMethod { get; set; }

    /// <summary>
    /// AES-GCM envelope (DeliveryEncryptionService format) of the http auth-secrets JSON
    /// (api-key header+value, bearer token, basic user+pass, or oauth2 client-id/secret/token-url/scope).
    /// Write-only — GET masks it, never returns the plaintext or the ciphertext. Null = no secrets stored.
    /// </summary>
    public string? AuthConfigEncrypted { get; set; }

    /// <summary>HTTP method for http/https sources — 'GET' only in v2 (default). Null for sftp/ftp.</summary>
    public string? HttpMethod { get; set; }

    /// <summary>
    /// 'auto' | 'csv' | 'xlsx' | 'json' | 'xml' | 'cif'. 'auto' content-sniffs (then routes on
    /// the remote file extension). ZIP archives are transparently unwrapped before this routing.
    /// </summary>
    public string FileFormat { get; set; } = "auto";

    /// <summary>
    /// Optional per-source column mapping (plan 2026-07-02 D3): a flat JSON object
    /// <c>{"sourceColumn":"canonicalField"}</c> checked BEFORE the global aliases. Enables feeds
    /// whose headers don't alias-match (REDACTED-PARTY <c>{"Id":"code",…}</c>, REDACTED-PARTY named columns) and
    /// headerless feeds via numeric keys + the <c>"__noheader__":"true"</c> / <c>"__encoding__"</c>
    /// directives (Ingram/Also positional, cp1252). Not a secret — echoed back in responses.
    /// </summary>
    public string? ColumnMappingJson { get; set; }

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
