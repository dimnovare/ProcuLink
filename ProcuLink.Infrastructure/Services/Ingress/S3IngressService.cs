using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production implementation of <see cref="IS3IngressService"/>.
/// Polls an S3 or Cloudflare R2 bucket for new purchase-order files and feeds
/// them into the order-ingestion pipeline via <see cref="IOrderService.CreateStubAsync"/>
/// (routed to the configured default supplier) or
/// <see cref="IOrderService.CreateUnroutedStubAsync"/> (unrouted hold, when no valid
/// default supplier is configured).
/// </summary>
/// <remarks>
/// A per-org <see cref="IAmazonS3"/> is built inside <see cref="PollAsync"/> from
/// the org's decrypted <c>S3IngressConfig</c> credentials, so each tenant
/// authenticates against their own bucket.
/// </remarks>
public sealed class S3IngressService : IS3IngressService
{
    private static readonly HashSet<string> AcceptedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv",
            ".xlsx",
            ".pdf",
            ".xml",
            ".edi",
        };

    private readonly ProcuLinkDbContext _db;
    private readonly IOrderService _orderService;
    private readonly IParseJobEnqueuer _parseJobEnqueuer;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IAmazonS3ClientFactory _s3ClientFactory;
    private readonly OutboundRequestGuard _guard;
    private readonly ILogger<S3IngressService> _logger;

    public S3IngressService(
        ProcuLinkDbContext db,
        IOrderService orderService,
        IParseJobEnqueuer parseJobEnqueuer,
        DeliveryEncryptionService encryption,
        IAmazonS3ClientFactory s3ClientFactory,
        OutboundRequestGuard guard,
        ILogger<S3IngressService> logger)
    {
        _db = db;
        _orderService = orderService;
        _parseJobEnqueuer = parseJobEnqueuer;
        _encryption = encryption;
        _s3ClientFactory = s3ClientFactory;
        _guard = guard;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PollAsync(Guid organisationId, CancellationToken ct)
    {
        var config = await _db.Set<S3IngressConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrgId == organisationId, ct);

        if (config is null)
        {
            _logger.LogDebug("S3 ingress: no config for org {OrgId}.", organisationId);
            return 0;
        }

        if (!config.IsEnabled)
        {
            _logger.LogInformation("S3 ingress: config disabled for org {OrgId}.", organisationId);
            return 0;
        }

        var supplierId = await ResolveDefaultSupplierIdAsync(
            organisationId,
            config.DefaultSupplierId,
            ct);

        // Phase 1b routing: a missing (or stale/soft-deleted) default supplier no longer
        // blocks the poll. Objects are imported UNROUTED (SupplierId = null) via
        // CreateUnroutedStubAsync — the parse job parks them 'unrouted' and
        // POST /api/orders/{id}/assign-supplier routes + re-parses them later.
        if (supplierId is null)
        {
            if (config.DefaultSupplierId is null)
            {
                _logger.LogInformation(
                    "S3 ingress: org {OrgId} has no default supplier configured — new objects will be imported unrouted.",
                    organisationId);
            }
            else
            {
                _logger.LogWarning(
                    "S3 ingress: org {OrgId} default supplier {SupplierId} no longer exists — new objects will be imported unrouted.",
                    organisationId, config.DefaultSupplierId);
            }
        }

        var secretKey = _encryption.Decrypt(config.EncryptedSecretKey);
        if (secretKey is null)
        {
            _logger.LogWarning(
                "S3 ingress: cannot decrypt secret key for org {OrgId}. Skipping poll.",
                organisationId);
            return 0;
        }

        // ── SSRF guard on a custom ServiceUrl (SEC-1) ────────────────────────
        // A tenant-supplied ServiceUrl (R2/MinIO/arbitrary S3-compatible endpoint) is
        // the SSRF vector here — it could point at an internal/metadata host. The
        // standard AWS endpoint (ServiceUrl null) resolves to public AWS infra and is
        // not tenant-controlled, so it is not guarded. ValidateAsync runs the same
        // DNS-resolve + IP-range check as the delivery dispatchers.
        if (!string.IsNullOrWhiteSpace(config.ServiceUrl))
        {
            var guardResult = await _guard.ValidateAsync(config.ServiceUrl, ct);
            if (!guardResult.Allowed)
            {
                _logger.LogWarning(
                    "S3 ingress: org {OrgId} — ServiceUrl blocked by SSRF guard, skipping poll. {Reason}",
                    organisationId, guardResult.Reason);
                return 0;
            }
        }

        // Pass the per-org ServiceUrl when set (Cloudflare R2 / MinIO / other
        // S3-compatible stores). When null/empty the factory resolves the
        // standard AWS endpoint from the region.
        var s3Client = _s3ClientFactory.Create(
            config.AccessKeyId,
            secretKey,
            config.Region,
            serviceUrl: string.IsNullOrWhiteSpace(config.ServiceUrl) ? null : config.ServiceUrl);

        _logger.LogInformation(
            "S3 ingress: listing bucket={Bucket} prefix={Prefix} for org {OrgId}.",
            config.BucketName, config.KeyPrefix, organisationId);

        var listRequest = new ListObjectsV2Request
        {
            BucketName = config.BucketName,
            Prefix     = string.IsNullOrEmpty(config.KeyPrefix) ? null : config.KeyPrefix,
        };

        var imported = 0;

        ListObjectsV2Response listResponse;
        do
        {
            listResponse = await s3Client.ListObjectsV2Async(listRequest, ct);

            foreach (var s3Object in listResponse.S3Objects)
            {
                ct.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(s3Object.Key);
                if (!AcceptedExtensions.Contains(extension))
                {
                    _logger.LogDebug(
                        "S3 ingress: skipping unsupported extension {Ext} (key={Key}).",
                        extension, s3Object.Key);
                    continue;
                }

                // ── dedupe by (OrgId, BucketName, ObjectKey) + ETag ───────────
                var existing = await _db.Set<ImportedS3Object>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        f => f.OrgId == organisationId
                          && f.BucketName == config.BucketName
                          && f.ObjectKey == s3Object.Key,
                        ct);

                if (existing is not null && existing.ETag == s3Object.ETag)
                {
                    _logger.LogDebug(
                        "S3 ingress: org {OrgId} already imported {Key} (ETag={ETag}). Skipping.",
                        organisationId, s3Object.Key, s3Object.ETag);
                    continue;
                }

                // ── size cap (pre-download, server-reported size) ─────────────
                // Skip oversized objects before we even download them. Size is
                // server-reported (nullable); a post-download re-check below is the
                // defense-in-depth guard for an understated/missing size.
                if (s3Object.Size is > IngressLimits.MaxFileBytes)
                {
                    _logger.LogWarning(
                        "S3 ingress: org {OrgId} — skipping {Key} ({Bytes} bytes > {Max} byte cap).",
                        organisationId, s3Object.Key, s3Object.Size, IngressLimits.MaxFileBytes);
                    continue;
                }

                // ── download ─────────────────────────────────────────────────
                GetObjectResponse getResponse;
                try
                {
                    getResponse = await s3Client.GetObjectAsync(config.BucketName, s3Object.Key, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "S3 ingress: org {OrgId} — failed to download key={Key}.",
                        organisationId, s3Object.Key);
                    continue;
                }

                // Bounded streamed read (H2): the server-reported Size pre-check above is
                // only advisory — a lying/understated Size would let an unbounded
                // CopyToAsync materialize a multi-GB object into memory. Stream through the
                // shared bounded-read helper so at most MaxFileBytes+1 bytes are ever read;
                // overflow throws CatalogFileTooLargeException, which we catch and skip.
                await using var responseStream = getResponse.ResponseStream;
                MemoryStream fileBytes;
                try
                {
                    fileBytes = await BoundedRead.CopyAsync(responseStream, IngressLimits.MaxFileBytes, ct);
                }
                catch (CatalogFileTooLargeException)
                {
                    _logger.LogWarning(
                        "S3 ingress: org {OrgId} — skipping {Key} during download (exceeds {Max} byte cap).",
                        organisationId, s3Object.Key, IngressLimits.MaxFileBytes);
                    continue;
                }

                using var _fileBytes = fileBytes;
                fileBytes.Position = 0;

                var fileName = Path.GetFileName(s3Object.Key);
                var contentType = ExtensionToContentType(extension);

                // Routed when a valid default supplier exists; UNROUTED hold otherwise —
                // same creation path browser upload and inbound email use for supplier-less files.
                var stubResult = supplierId is { } routedSupplierId
                    ? await _orderService.CreateStubAsync(
                        organisationId,
                        routedSupplierId,
                        fileBytes,
                        fileName,
                        contentType,
                        ct)
                    : await _orderService.CreateUnroutedStubAsync(
                        organisationId,
                        fileBytes,
                        fileName,
                        contentType,
                        ct);

                if (!stubResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "S3 ingress: org {OrgId} — CreateStubAsync failed for key={Key}: {Error}",
                        organisationId, s3Object.Key, stubResult.Error);
                    continue;
                }

                // ── record import ────────────────────────────────────────────
                if (existing is not null)
                {
                    // Object was re-uploaded (ETag changed) — update the record.
                    var tracked = await _db.Set<ImportedS3Object>()
                        .FirstAsync(f => f.OrgId == organisationId
                                      && f.BucketName == config.BucketName
                                      && f.ObjectKey == s3Object.Key, ct);
                    tracked.ETag = s3Object.ETag;
                    tracked.ImportedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.Set<ImportedS3Object>().Add(new ImportedS3Object
                    {
                        Id         = Guid.NewGuid(),
                        OrgId      = organisationId,
                        BucketName = config.BucketName,
                        ObjectKey  = s3Object.Key,
                        ETag       = s3Object.ETag,
                        ImportedAt = DateTime.UtcNow,
                    });
                }

                await _db.SaveChangesAsync(ct);

                await _parseJobEnqueuer.EnqueueAsync(stubResult.Value!.Id, organisationId, ct);
                imported++;

                _logger.LogInformation(
                    "S3 ingress: org {OrgId} — imported key={Key} → order {OrderId}.",
                    organisationId, s3Object.Key, stubResult.Value!.Id);
            }

            listRequest.ContinuationToken = listResponse.NextContinuationToken;

        } while (listResponse.IsTruncated ?? false);

        _logger.LogInformation(
            "S3 ingress: org {OrgId} — poll complete. Imported={Imported}.",
            organisationId, imported);

        return imported;
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async Task<Guid?> ResolveDefaultSupplierIdAsync(
        Guid organisationId,
        Guid? defaultSupplierId,
        CancellationToken ct)
    {
        if (defaultSupplierId is null || defaultSupplierId == Guid.Empty)
        {
            return null;
        }

        var exists = await _db.Suppliers
            .AsNoTracking()
            .AnyAsync(
                s => s.OrgId == organisationId
                  && s.Id == defaultSupplierId
                  && s.DeletedAt == null,
                ct);

        return exists ? defaultSupplierId.Value : null;
    }

    private static string ExtensionToContentType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".csv"  => "text/csv",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pdf"  => "application/pdf",
            ".xml"  => "application/xml",
            ".edi"  => "application/edifact",
            _       => "application/octet-stream",
        };
}
