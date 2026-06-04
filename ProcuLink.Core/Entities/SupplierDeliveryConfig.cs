namespace ProcuLink.Core.Entities;

public class SupplierDeliveryConfig
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>'http' | 'sftp' | 'ftp' | 'erp_erply' | 'erp_directo'</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>When true, dispatch fires automatically after TransformAsync completes.</summary>
    public bool AutoDeliver { get; set; }

    /// <summary>Non-secret JSONB: endpoint URL, host, remote path, extra headers, timeout, etc.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>Authenticated encrypted credential payload. Empty string means no credentials configured.</summary>
    public string EncryptedCredentials { get; set; } = string.Empty;

    /// <summary>
    /// The output format this supplier requires — one of 'xml' | 'csv' | 'cxml' | 'json' | 'ubl' | 'x12'.
    /// When set, "send to supplier" auto-transforms the order into this format before delivery.
    /// Null means the caller must specify a format explicitly.
    /// </summary>
    public string? OutputFormat { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
