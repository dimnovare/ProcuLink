namespace ProcuLink.Core.Entities;

/// <summary>
/// Append-only evidence trail for the blob-retention sweep: one row per organisation per
/// run while that org has retention enabled (<c>organisations.retention_days</c> not null).
/// Records WHAT the sweep did (mode <c>delete</c>) or WOULD have done (mode <c>dry_run</c> —
/// the second safety latch, <c>Retention:DryRun</c>, defaults to true even for opted-in orgs).
/// Table <c>retention_audit_log</c> (migration <c>AddBlobRetentionSweep</c>).
/// </summary>
public class RetentionAuditLog
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }

    /// <summary>UTC timestamp of the sweep run that produced this row.</summary>
    public DateTime RunAt { get; set; }

    /// <summary><c>dry_run</c> | <c>delete</c> (see <see cref="Services.RetentionConstants"/>).</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    /// Mode <c>delete</c>: blobs actually deleted from storage this run.
    /// Mode <c>dry_run</c>: blobs that WOULD have been deleted (nothing was touched).
    /// </summary>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Best-effort byte total of the matched blobs (from storage HEAD calls). Blobs whose
    /// size could not be read contribute 0; the unknown count is recorded in <see cref="DetailsJson"/>.
    /// </summary>
    public long BytesEstimated { get; set; }

    /// <summary>
    /// jsonb breakdown: retentionDays, cutoffUtc, ordersExamined, sourceFiles, artifactBlobs,
    /// deleteFailures, unknownSizes, batchLimited.
    /// </summary>
    public string? DetailsJson { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
