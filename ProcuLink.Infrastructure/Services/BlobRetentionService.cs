using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// DESTRUCTIVE JOB — maximum care. The defaults at every layer are "delete nothing":
/// <list type="number">
///   <item><description>Orgs are only swept when they OPTED IN (<c>retention_days</c> not null
///   and &gt; 0). An unconfigured org is never even queried for candidates.</description></item>
///   <item><description><c>Retention:DryRun</c> defaults to TRUE — even an opted-in org only
///   gets a <c>dry_run</c> audit row until the founder flips the knob on the Worker.</description></item>
///   <item><description>Only TERMINAL orders (delivered / dead-letter / rejected / failed /
///   transform_failed — the same conservative set as <see cref="DataRetentionService"/>) are
///   candidates. In-flight orders are untouched regardless of age; notably
///   <c>delivery_failed</c> is EXCLUDED because it is retryable and a retry re-reads the
///   artifact blob.</description></item>
///   <item><description>BLOB-ONLY: DB rows, file keys, <c>ArtifactSha256</c> hashes, provenance
///   and the audit trail all STAY. Rows are marked purged
///   (<c>source_file_purged_at</c> / <c>blob_purged_at</c>) — which is also what makes the
///   sweep idempotent: a re-run matches nothing already purged.</description></item>
///   <item><description>Per-key storage deletes are individually fault-isolated: a failed
///   delete is logged + counted and the purge timestamp is NOT written, so the next run
///   retries exactly that blob.</description></item>
///   <item><description>Bounded: at most <see cref="BlobRetentionOptions.EffectiveOrderBatchSize"/>
///   orders per org per run; a backlog drains over several daily runs.</description></item>
/// </list>
/// Tenancy: the sweep enumerates orgs cross-tenant (maintenance, mirroring
/// <see cref="DataRetentionService"/>), but every candidate query is strictly scoped to the
/// org being processed (<c>OrgId == org.Id</c>).
/// </remarks>
public class BlobRetentionService : IBlobRetentionService
{
    /// <summary>
    /// Statuses whose blobs may be retention-purged. Mirrors
    /// <see cref="DataRetentionService"/>'s terminal set exactly: delivered / dead-letter /
    /// rejected / failed / transform_failed. <c>delivery_failed</c> is deliberately NOT here —
    /// it is retryable, and a retry must still find the artifact blob.
    /// </summary>
    private static readonly string[] TerminalOrderStatuses =
    {
        OrderStatusConstants.Delivered,
        OrderStatusConstants.DeliveryDeadLetter,
        OrderStatusConstants.RejectedBySupplier,
        OrderStatusConstants.Failed,
        OrderStatusConstants.TransformFailed,
    };

    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly BlobRetentionOptions _options;
    private readonly ILogger<BlobRetentionService> _logger;

    public BlobRetentionService(
        ProcuLinkDbContext db,
        IFileStorageService storage,
        BlobRetentionOptions options,
        ILogger<BlobRetentionService> logger)
    {
        _db = db;
        _storage = storage;
        _options = options;
        _logger = logger;
    }

    public async Task<BlobRetentionRunResult> RunAsync(CancellationToken ct)
    {
        // SAFETY LATCH 1 — per-org opt-in. Only orgs that explicitly set a positive
        // retention window are considered. Default (NULL) = nothing deleted anywhere.
        var orgs = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.RetentionDays != null && o.RetentionDays > 0)
            .Select(o => new { o.Id, o.RetentionDays })
            .ToListAsync(ct);

        if (orgs.Count == 0)
        {
            _logger.LogDebug("BlobRetention: no organisation has retention enabled — nothing to do.");
            return BlobRetentionRunResult.Empty;
        }

        // SAFETY LATCH 2 — global dry-run (default TRUE).
        var dryRun = _options.DryRun;
        var mode = dryRun ? RetentionConstants.ModeDryRun : RetentionConstants.ModeDelete;
        var now = DateTime.UtcNow;
        var results = new List<OrgBlobRetentionResult>(orgs.Count);

        foreach (var org in orgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await SweepOrgAsync(org.Id, org.RetentionDays!.Value, now, dryRun, mode, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One org's failure must never block the others. Already-deleted blobs in
                // this org are re-matched next run only if their purge timestamp didn't save —
                // and DeleteAsync on a missing key is idempotent, so a retry is safe.
                _logger.LogError(ex, "BlobRetention: sweep failed for org {OrgId} — continuing with the next org.", org.Id);
            }
            finally
            {
                // Poisoned-context guard (finding C3): every org shares this ONE scoped DbContext.
                // If this org's SaveChanges threw, its staged purge timestamps + audit row stay in
                // the tracker; left un-cleared they would batch into the NEXT org's SaveChanges —
                // re-hitting the fault (stranding the next org's purge) and entangling tenants.
                // On success the entities are already persisted, so clearing is equally safe.
                // Each org re-queries fresh, so starting from an empty tracker is always correct.
                _db.ChangeTracker.Clear();
            }
        }

        var run = new BlobRetentionRunResult(results);
        _logger.LogInformation(
            "BlobRetention: run complete (mode={Mode}) — {OrgCount} org(s) swept, {Blobs} blob(s) {Verb}, {Failures} delete failure(s).",
            mode, results.Count, run.TotalBlobs, dryRun ? "matched (NOT deleted — dry run)" : "deleted", run.TotalFailures);
        return run;
    }

    private async Task<OrgBlobRetentionResult> SweepOrgAsync(
        Guid orgId, int retentionDays, DateTime now, bool dryRun, string mode, CancellationToken ct)
    {
        var cutoff = now.AddDays(-retentionDays);

        // Candidates: STRICTLY this org, STRICTLY older than the window, STRICTLY terminal,
        // and still owning at least one unpurged blob (purged rows fall out = idempotency).
        var candidateOrders = await _db.PurchaseOrders
            .Where(o => o.OrgId == orgId
                     && o.CreatedAt < cutoff
                     && TerminalOrderStatuses.Contains(o.Status)
                     && ((o.SourceFileKey != null && o.SourceFilePurgedAt == null)
                         || o.OutboundArtifacts.Any(a => a.BlobPurgedAt == null)))
            .OrderBy(o => o.CreatedAt)
            .Take(_options.EffectiveOrderBatchSize)
            .ToListAsync(ct);

        var batchLimited = candidateOrders.Count == _options.EffectiveOrderBatchSize;

        var orderIds = candidateOrders.Select(o => o.Id).ToList();
        var candidateArtifacts = orderIds.Count == 0
            ? new List<OutboundArtifact>()
            : await _db.OutboundArtifacts
                .Where(a => a.OrgId == orgId
                         && a.BlobPurgedAt == null
                         && orderIds.Contains(a.OrderId))
                .ToListAsync(ct);

        int sourceFiles = 0, artifactBlobs = 0, deleteFailures = 0, unknownSizes = 0;
        long bytesEstimated = 0;

        foreach (var order in candidateOrders)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(order.SourceFileKey) || order.SourceFilePurgedAt is not null)
                continue;

            var sourceSize = await EstimateAsync(order.SourceFileKey, ct);
            if (sourceSize is null) unknownSizes++;
            bytesEstimated += sourceSize ?? 0;

            if (dryRun)
            {
                sourceFiles++; // would delete — nothing touched
            }
            else if (await TryDeleteAsync(orgId, order.SourceFileKey, ct))
            {
                order.SourceFilePurgedAt = now; // blob gone; row + key stay
                sourceFiles++;
            }
            else
            {
                deleteFailures++; // timestamp NOT set → retried next run
            }
        }

        foreach (var artifact in candidateArtifacts)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(artifact.FileKey))
                continue;

            var artifactSize = await EstimateAsync(artifact.FileKey, ct);
            if (artifactSize is null) unknownSizes++;
            bytesEstimated += artifactSize ?? 0;

            if (dryRun)
            {
                artifactBlobs++;
            }
            else if (await TryDeleteAsync(orgId, artifact.FileKey, ct))
            {
                artifact.BlobPurgedAt = now; // row, hash + provenance stay
                artifactBlobs++;
            }
            else
            {
                deleteFailures++;
            }
        }

        // Evidence trail: one audit row per enabled org per run — including zero-match runs,
        // so "the sweep ran and found nothing" is itself auditable.
        var details = JsonSerializer.Serialize(new
        {
            retentionDays,
            cutoffUtc = cutoff,
            ordersExamined = candidateOrders.Count,
            sourceFiles,
            artifactBlobs,
            deleteFailures,
            unknownSizes,
            batchLimited,
        });

        _db.RetentionAuditLogs.Add(new RetentionAuditLog
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            RunAt = now,
            Mode = mode,
            FilesDeleted = sourceFiles + artifactBlobs,
            BytesEstimated = bytesEstimated,
            DetailsJson = details,
        });

        // One save per org: purge timestamps + the audit row land together.
        await _db.SaveChangesAsync(ct);

        if (sourceFiles + artifactBlobs + deleteFailures > 0)
            _logger.LogInformation(
                "BlobRetention: org {OrgId} (window {Days}d, mode={Mode}) — {Orders} order(s), {Source} source blob(s) + {Artifacts} artifact blob(s) {Verb}, {Failures} failure(s), ~{Bytes} bytes.",
                orgId, retentionDays, mode, candidateOrders.Count, sourceFiles, artifactBlobs,
                dryRun ? "WOULD be deleted (dry run)" : "deleted", deleteFailures, bytesEstimated);

        return new OrgBlobRetentionResult(
            OrgId: orgId,
            Mode: mode,
            OrdersExamined: candidateOrders.Count,
            SourceFilesPurged: sourceFiles,
            ArtifactBlobsPurged: artifactBlobs,
            DeleteFailures: deleteFailures,
            BytesEstimated: bytesEstimated);
    }

    private async Task<long?> EstimateAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _storage.TryGetSizeAsync(key, ct);
        }
        catch
        {
            // TryGetSizeAsync is contracted not to throw; belt-and-braces anyway —
            // a size estimate must never fail (or worse, abort) the sweep.
            return null;
        }
    }

    private async Task<bool> TryDeleteAsync(Guid orgId, string key, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteAsync(key, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BlobRetention: failed to delete blob {Key} for org {OrgId} — will retry next run.",
                key, orgId);
            return false;
        }
    }
}
