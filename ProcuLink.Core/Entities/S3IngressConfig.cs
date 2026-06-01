using System.ComponentModel.DataAnnotations.Schema;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Stores per-organisation S3/R2 bucket watch configuration for pull-ingress.
/// Secret keys are stored encrypted via <c>DeliveryEncryptionService</c>.
/// EF table: <c>s3_ingress_configs</c>.
/// </summary>
[Table("s3_ingress_configs")]
public class S3IngressConfig
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Name of the S3 or R2 bucket to watch.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// Object key prefix to filter which keys are processed (e.g. <c>incoming/</c>).
    /// Empty string means the entire bucket root is watched.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>AWS region identifier (e.g. <c>eu-west-1</c>, or <c>auto</c> for Cloudflare R2).</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>AWS access key ID used to authenticate against the bucket.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>AES-256-GCM ciphertext of the AWS secret access key (base64, same format as delivery credentials).</summary>
    public string EncryptedSecretKey { get; set; } = string.Empty;

    /// <summary>Supplier used when importing files from this assisted pull source.</summary>
    public Guid? DefaultSupplierId { get; set; }

    /// <summary>Whether polling is active for this configuration.</summary>
    public bool IsEnabled { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
