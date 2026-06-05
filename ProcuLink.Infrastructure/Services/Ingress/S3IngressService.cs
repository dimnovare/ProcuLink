using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production implementation of <see cref="IS3IngressService"/>.
/// Polls an S3 or Cloudflare R2 bucket for new purchase-order files and feeds
/// them into the order-ingestion pipeline via <see cref="IOrderService.CreateStubAsync"/>.
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
    private readonly DeliveryEncryptionService _encryption;
    private readonly IAmazonS3ClientFactory _s3ClientFactory;
    private readonly ILogger<S3IngressService> _logger;

    public S3IngressService(
        ProcuLinkDbContext db,
        IOrderService orderService,
        DeliveryEncryptionService encryption,
        IAmazonS3ClientFactory s3ClientFactory,
        ILogger<S3IngressService> logger)
    {
        _db = db;
        _orderService = orderService;
        _encryption = encryption;
        _s3ClientFactory = s3ClientFactory;
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

        if (supplierId is null)
        {
            _logger.LogWarning(
                "S3 ingress: org {OrgId} has no valid default supplier. Skipping poll.",
                organisationId);
            return 0;
        }

        var secretKey = _encryption.Decrypt(config.EncryptedSecretKey);
        if (secretKey is null)
        {
            _logger.LogWarning(
                "S3 ingress: cannot decrypt secret key for org {OrgId}. Skipping poll.",
                organisationId);
            return 0;
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

                await using var responseStream = getResponse.ResponseStream;
                using var fileBytes = new MemoryStream();
                await responseStream.CopyToAsync(fileBytes, ct);
                fileBytes.Position = 0;

                var fileName = Path.GetFileName(s3Object.Key);
                var contentType = ExtensionToContentType(extension);

                var stubResult = await _orderService.CreateStubAsync(
                    organisationId,
                    supplierId.Value,
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
