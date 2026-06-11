namespace ProcuLink.Core.Services;

/// <summary>Shared vocabulary for the blob-retention sweep + its downstream guards.</summary>
public static class RetentionConstants
{
    /// <summary>retention_audit_log.mode value: nothing was deleted; counts are "would delete".</summary>
    public const string ModeDryRun = "dry_run";

    /// <summary>retention_audit_log.mode value: blobs were actually deleted from storage.</summary>
    public const string ModeDelete = "delete";

    /// <summary>
    /// Service-level error string for a download/preview hitting a purged blob. The API maps
    /// this EXACT value to 410 Gone with an honest explanation (never a 500). Compared by
    /// reference/equality in controllers — do not reword call-sites independently.
    /// </summary>
    public const string BlobPurgedError =
        "This file has been purged per the organisation's data retention policy. " +
        "The order record, content hashes, provenance and audit trail are retained.";
}
