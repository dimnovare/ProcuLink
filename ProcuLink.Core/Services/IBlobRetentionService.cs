namespace ProcuLink.Core.Services;

/// <summary>
/// Recurring per-org BLOB retention sweep — the founder-approved "Flip C" design:
/// <list type="bullet">
///   <item><description>OFF until an org opts in: only orgs with a non-null
///   <c>organisations.retention_days</c> are even considered (NULL = disabled, the default).</description></item>
///   <item><description>BLOB-ONLY deletion: deletes the R2 source-file blob + generated
///   artifact blobs of orders older than the org's window AND in a TERMINAL status
///   (delivered / failed / transform_failed / dead-letter / rejected — never in-flight).
///   DB rows, <c>ArtifactSha256</c> hashes, provenance and the audit trail always STAY.</description></item>
///   <item><description>DRY-RUN second latch: while <see cref="BlobRetentionOptions.DryRun"/>
///   is true (the default) the sweep deletes NOTHING anywhere — it writes a
///   <c>dry_run</c> audit row describing what it would delete.</description></item>
///   <item><description>Audit log: one <c>retention_audit_log</c> row per enabled org per run.</description></item>
///   <item><description>Idempotent: purged blobs are marked
///   (<c>purchase_orders.source_file_purged_at</c> / <c>outbound_artifacts.blob_purged_at</c>),
///   so a re-run finds nothing new to delete.</description></item>
/// </list>
/// Distinct from <see cref="IDataRetentionService"/>, which prunes append-only DB ROWS;
/// this service touches FILE BLOBS only.
/// </summary>
public interface IBlobRetentionService
{
    /// <summary>
    /// Runs one sweep across all opted-in organisations. Returns per-org outcomes.
    /// A no-op (empty result) when no org has retention enabled.
    /// </summary>
    Task<BlobRetentionRunResult> RunAsync(CancellationToken ct);
}

/// <summary>Outcome of one sweep run for one opted-in organisation.</summary>
/// <param name="OrgId">The organisation swept.</param>
/// <param name="Mode"><c>dry_run</c> or <c>delete</c> (see <see cref="RetentionConstants"/>).</param>
/// <param name="OrdersExamined">Candidate orders (terminal + aged + with at least one unpurged blob) this run.</param>
/// <param name="SourceFilesPurged">Source-file blobs deleted (delete) or matched (dry_run).</param>
/// <param name="ArtifactBlobsPurged">Artifact blobs deleted (delete) or matched (dry_run).</param>
/// <param name="DeleteFailures">Storage deletes that failed (delete mode only; retried next run).</param>
/// <param name="BytesEstimated">Best-effort byte total of the matched blobs.</param>
public sealed record OrgBlobRetentionResult(
    Guid OrgId,
    string Mode,
    int OrdersExamined,
    int SourceFilesPurged,
    int ArtifactBlobsPurged,
    int DeleteFailures,
    long BytesEstimated)
{
    public int TotalBlobs => SourceFilesPurged + ArtifactBlobsPurged;
}

/// <summary>Aggregate result of one <see cref="IBlobRetentionService.RunAsync"/> sweep.</summary>
public sealed record BlobRetentionRunResult(IReadOnlyList<OrgBlobRetentionResult> Organisations)
{
    public static readonly BlobRetentionRunResult Empty = new(Array.Empty<OrgBlobRetentionResult>());

    public int TotalBlobs => Organisations.Sum(o => o.TotalBlobs);
    public int TotalFailures => Organisations.Sum(o => o.DeleteFailures);
}
