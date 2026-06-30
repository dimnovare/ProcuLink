namespace ProcuLink.Core.Entities;

public class SupplierDeliveryConfig
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>'http' | 'sftp' | 'ftps' | 'email' | 'erp_erply' | 'erp_directo' (legacy: 'smtp', 'ftp')</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>When true, dispatch fires automatically after TransformAsync completes.</summary>
    public bool AutoDeliver { get; set; }

    /// <summary>
    /// Non-secret JSONB: endpoint URL, host, remote path, extra headers, timeout, etc.
    ///
    /// SCALE-GATED / SECURITY NOTE: this column is stored in CLEARTEXT (no encryption).
    /// That is BY DESIGN and NOT a P2 secret-at-rest issue: every SECRET (passwords, API
    /// keys, bearer tokens, basic-auth, OAuth2 client secrets, SFTP/FTP credentials) is
    /// kept out of here and stored AES-GCM encrypted in <see cref="EncryptedCredentials"/>.
    /// ConfigJson holds only non-secret connection metadata. INVARIANT to preserve: never
    /// write a credential/secret into ConfigJson — if a new delivery option needs a secret,
    /// add it to the encrypted credential payload instead. See
    /// docs/audit/2026-06-12-scale-gated-constraints.md.
    /// </summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>Authenticated encrypted credential payload. Empty string means no credentials configured.</summary>
    public string EncryptedCredentials { get; set; } = string.Empty;

    /// <summary>
    /// The output format this supplier requires — one of 'xml' | 'csv' | 'cxml' | 'json' | 'ubl' | 'x12'.
    /// When set, "send to supplier" auto-transforms the order into this format before delivery.
    /// Null means the caller must specify a format explicitly.
    /// </summary>
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Non-secret cXML network identities (From/To/Sender domain + identity) as cleartext JSON, or null.
    /// Drives the <c>&lt;Header&gt;</c> credentials of generated cXML so the wire carries the supplier's
    /// REAL cXML network identity (e.g. Coupa <c>NetworkId</c>) instead of ProcuLink's internal
    /// OrgId / SupplierId GUIDs. Only consulted when <see cref="OutputFormat"/> is <c>cxml</c>; null =
    /// legacy GUID identities. The Sender SharedSecret is a SECRET and lives encrypted in
    /// <see cref="EncryptedCxmlSharedSecret"/>, NOT here (same cleartext invariant as
    /// <see cref="ConfigJson"/>).
    /// </summary>
    public string? CxmlConfigJson { get; set; }

    /// <summary>
    /// AES-GCM encrypted cXML Sender <c>SharedSecret</c> (same encryption as
    /// <see cref="EncryptedCredentials"/>), or null/empty when no shared secret is configured.
    /// </summary>
    public string? EncryptedCxmlSharedSecret { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
