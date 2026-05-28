using System.ComponentModel.DataAnnotations.Schema;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Tracks which S3/R2 objects have already been ingested, enabling idempotent polling.
/// A (OrgId, BucketName, ObjectKey) triple that exists in this table with a matching
/// ETag is skipped on subsequent polls.
/// EF table: <c>imported_s3_objects</c>.
/// </summary>
[Table("imported_s3_objects")]
public class ImportedS3Object
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Name of the S3/R2 bucket the object was read from.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Full object key within the bucket (e.g. <c>incoming/po-20260528.csv</c>).</summary>
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>S3 ETag at the time of ingestion. Used to detect updated objects.</summary>
    public string ETag { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the object was ingested.</summary>
    public DateTime ImportedAt { get; set; }
}
