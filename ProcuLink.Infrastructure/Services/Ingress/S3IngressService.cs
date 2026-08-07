using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Security;
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
    private readonly IStubOrderCreator _orderService;
    private readonly IParseJobEnqueuer _parseJobEnqueuer;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IAmazonS3ClientFactory _s3ClientFactory;
    private readonly OutboundRequestGuard _guard;
    private readonly ILogger<S3IngressService> _logger;

    public S3IngressService(
        ProcuLinkDbContext db,
        IStubOrderCreator orderService,
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

        string secretKey;
        try
        {
            secretKey = _encryption.Decrypt(
                config.EncryptedSecretKey,
                CredentialScope.ForOrg(organisationId, CredentialPurpose.OrgIngressS3SecretKey));
        }
        catch (CredentialUnbindableException ex)
        {
            _logger.LogWarning(ex,
                "S3 ingress: cannot decrypt secret key for org {OrgId} ({Reason}). Skipping poll.",
                organisationId, ex.Reason);
            return 0;
        }

        // ── SSRF guard on a custom ServiceUrl (SEC-1) ────────────────────────
        // A tenant-supplied ServiceUrl (R2/MinIO/arbitrary S3-compatible endpoint) is
        // the SSRF vector here — it could point at an internal/metadata host. The
        // standard AWS endpoint (ServiceUrl null) resolves to public AWS infra and is
        // not tenant-controlled, so it is not guarded. ValidateAsync runs the same
        // DNS-resolve + IP-range check as the delivery dispatchers.
        //
        // Defense-in-depth (audit finding #6, DNS-rebind TOCTOU): this up-front check is
        // NOT the last line. The AmazonS3ClientFactory wires a GuardedAwsHttpClientFactory
        // so every S3 request (list + download, both control- and data-plane) also connects
        // through the guard's connect-time re-resolve + re-validate + IP-pin — the same
        // ConnectCallback defence the HTTP delivery path uses. So a rebind that flips from
        // public (here) to private/metadata (at the SDK's connect) is still blocked. Unlike
        // this S3-over-HTTP path, the raw-TCP pull/delivery paths (SFTP/FTPS/IMAP) cannot
        // IP-pin without breaking SSH host-key / TLS-certificate-hostname identity, so they
        // retain the up-front re-validate mitigation with a documented residual.
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

                // ── dedupe / resume-on-conflict by (OrgId, BucketName, ObjectKey) + ETag ──
                // Tracked (not AsNoTracking): the same-ETag resume path and the permanent-failure
                // terminal marker both mutate this row.
                var existing = await _db.Set<ImportedS3Object>()
                    .FirstOrDefaultAsync(
                        f => f.OrgId == organisationId
                          && f.BucketName == config.BucketName
                          && f.ObjectKey == s3Object.Key,
                        ct);

                if (existing is not null && existing.ETag == s3Object.ETag &&
                    await IngressDedupe.ClaimSatisfiedAsync(_db, organisationId, existing.OrderId, ct))
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

                // ── CLAIM-FIRST + resume-on-conflict ─────────────────────────
                // Pre-generate the order id and record it on the ledger row BEFORE creating the
                // order, so a later retry can tell an abandoned claim (RESUME) from a completed one
                // (SKIP). The unique index / atomic conditional update is the concurrency guard: a
                // loser SKIPS instead of importing a duplicate. See IngressDedupe.
                Guid orderId;
                ImportedS3Object claim;
                if (existing is not null && existing.ETag == s3Object.ETag)
                {
                    // Same content, unsatisfied claim (transient failure earlier left no order) — RESUME
                    // under the SAME pre-generated id (CreateStubAsync is a find-or-create on that key).
                    claim = existing;
                    orderId = existing.OrderId;
                    _logger.LogInformation(
                        "S3 ingress: org {OrgId} — resuming abandoned import of key={Key} (order {OrderId} was never created).",
                        organisationId, s3Object.Key, orderId);
                }
                else if (existing is not null)
                {
                    // Object was re-uploaded (ETag changed) — claim it with an ATOMIC conditional
                    // update guarded by the OLD ETag, stamping a FRESH pre-generated order id. Two
                    // concurrent polls of the same re-upload both read the old ETag; only the one
                    // whose UPDATE ... WHERE etag = old affects a row (rows == 1) proceeds — the loser
                    // (rows == 0) skips. Npgsql-only, like the atomic claims elsewhere.
                    orderId = Guid.NewGuid();
                    var claimed = await _db.Set<ImportedS3Object>()
                        .Where(f => f.OrgId == organisationId
                                 && f.BucketName == config.BucketName
                                 && f.ObjectKey == s3Object.Key
                                 && f.ETag == existing.ETag)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(f => f.ETag, s3Object.ETag)
                            .SetProperty(f => f.OrderId, orderId)
                            .SetProperty(f => f.ImportedAt, DateTime.UtcNow), ct);
                    if (claimed == 0)
                    {
                        _logger.LogInformation(
                            "S3 ingress: org {OrgId} — re-upload of key={Key} claimed concurrently; skipping duplicate import.",
                            organisationId, s3Object.Key);
                        continue;
                    }
                    // ExecuteUpdate bypasses the tracker — re-read the tracked row so the terminal
                    // marker below (on a permanent stub failure) mutates a fresh, consistent entity.
                    _db.Entry(existing).State = EntityState.Detached;
                    claim = await _db.Set<ImportedS3Object>().FirstAsync(
                        f => f.OrgId == organisationId && f.BucketName == config.BucketName && f.ObjectKey == s3Object.Key, ct);
                }
                else
                {
                    // First import of this key — INSERT the claim with a fresh pre-generated order id.
                    // A concurrent first-import loses on the unique index (23505) and SKIPS.
                    orderId = Guid.NewGuid();
                    claim = new ImportedS3Object
                    {
                        Id         = Guid.NewGuid(),
                        OrgId      = organisationId,
                        BucketName = config.BucketName,
                        ObjectKey  = s3Object.Key,
                        ETag       = s3Object.ETag,
                        OrderId    = orderId,
                        ImportedAt = DateTime.UtcNow,
                    };
                    _db.Set<ImportedS3Object>().Add(claim);
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
                    {
                        _db.Entry(claim).State = EntityState.Detached;
                        _logger.LogInformation(
                            "S3 ingress: org {OrgId} — key={Key} claimed concurrently (unique violation); skipping duplicate import.",
                            organisationId, s3Object.Key);
                        continue;
                    }
                }

                // Routed when a valid default supplier exists; UNROUTED hold otherwise —
                // same creation path browser upload and inbound email use for supplier-less files.
                var stubResult = supplierId is { } routedSupplierId
                    ? await _orderService.CreateStubAsync(
                        organisationId,
                        routedSupplierId,
                        orderId,
                        fileBytes,
                        fileName,
                        contentType,
                        ct)
                    : await _orderService.CreateUnroutedStubAsync(
                        organisationId,
                        orderId,
                        fileBytes,
                        fileName,
                        contentType,
                        ct);

                if (!stubResult.IsSuccess)
                {
                    // Result.Fail is a PERMANENT content error (empty / unsupported): mark the claim
                    // terminal so a genuinely-bad object is bounded (not retried forever). A TRANSIENT
                    // infra failure instead THROWS out of CreateStubAsync and propagates — Hangfire
                    // retries and the claim, still holding its real OrderId with no order, is RESUMED.
                    _logger.LogWarning(
                        "S3 ingress: org {OrgId} — {Mode} order stub creation permanently failed for key={Key}; marking claim terminal: {Error}",
                        organisationId, supplierId is null ? "unrouted" : "routed", s3Object.Key, stubResult.Error);
                    claim.OrderId = IngressDedupe.TerminalOrderId;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                await _parseJobEnqueuer.EnqueueAsync(orderId, organisationId, ct);
                imported++;

                _logger.LogInformation(
                    "S3 ingress: org {OrgId} — imported key={Key} → order {OrderId}.",
                    organisationId, s3Object.Key, orderId);
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
