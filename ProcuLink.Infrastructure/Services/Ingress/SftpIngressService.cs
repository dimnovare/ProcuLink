using System.Security.Cryptography;
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
/// Production implementation of <see cref="ISftpIngressService"/>.
/// Polls an SFTP server, downloads unseen files, and creates order stubs
/// via <see cref="IOrderService.CreateStubAsync"/> (routed to the configured
/// default supplier) or <see cref="IOrderService.CreateUnroutedStubAsync"/>
/// (unrouted hold, when no valid default supplier is configured).
/// </summary>
public sealed class SftpIngressService : ISftpIngressService
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
    private readonly ISftpClientFactory _sftpClientFactory;
    private readonly OutboundRequestGuard _guard;
    private readonly ILogger<SftpIngressService> _logger;

    public SftpIngressService(
        ProcuLinkDbContext db,
        IStubOrderCreator orderService,
        IParseJobEnqueuer parseJobEnqueuer,
        DeliveryEncryptionService encryption,
        ISftpClientFactory sftpClientFactory,
        OutboundRequestGuard guard,
        ILogger<SftpIngressService> logger)
    {
        _db = db;
        _orderService = orderService;
        _parseJobEnqueuer = parseJobEnqueuer;
        _encryption = encryption;
        _sftpClientFactory = sftpClientFactory;
        _guard = guard;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PollAsync(Guid organisationId, CancellationToken ct)
    {
        var config = await _db.Set<SftpIngressConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrgId == organisationId, ct);

        if (config is null)
        {
            _logger.LogDebug("SFTP ingress: no config for org {OrgId}.", organisationId);
            return 0;
        }

        if (!config.IsEnabled)
        {
            _logger.LogInformation("SFTP ingress: config disabled for org {OrgId}.", organisationId);
            return 0;
        }

        var supplierId = await ResolveDefaultSupplierIdAsync(
            organisationId,
            config.DefaultSupplierId,
            ct);

        // Phase 1b routing: a missing (or stale/soft-deleted) default supplier no longer
        // blocks the poll. Files are imported UNROUTED (SupplierId = null) via
        // CreateUnroutedStubAsync — the parse job parks them 'unrouted' and
        // POST /api/orders/{id}/assign-supplier routes + re-parses them later.
        if (supplierId is null)
        {
            if (config.DefaultSupplierId is null)
            {
                _logger.LogInformation(
                    "SFTP ingress: org {OrgId} has no default supplier configured — new files will be imported unrouted.",
                    organisationId);
            }
            else
            {
                _logger.LogWarning(
                    "SFTP ingress: org {OrgId} default supplier {SupplierId} no longer exists — new files will be imported unrouted.",
                    organisationId, config.DefaultSupplierId);
            }
        }

        string password;
        try
        {
            password = _encryption.Decrypt(
                config.EncryptedPassword,
                CredentialScope.ForOrg(organisationId, CredentialPurpose.OrgIngressSftpPassword));
        }
        catch (CredentialUnbindableException ex)
        {
            _logger.LogWarning(ex,
                "SFTP ingress: cannot decrypt password for org {OrgId} ({Reason}). Skipping poll.",
                organisationId, ex.Reason);
            return 0;
        }

        // ── SSRF guard IMMEDIATELY before connect (SEC-1) ────────────────────
        // The order poller connects to a tenant-configured host; without this a tenant
        // could point it at 169.254.169.254 (cloud metadata) or an RFC-1918 internal host.
        // Same shared guard the delivery dispatchers + catalog pull use. Residual L1
        // (DNS-rebind TOCTOU between this resolve and SSH.NET's own resolve at connect) is
        // the documented accepted risk the SFTP delivery dispatcher also carries — pinning
        // the IP would break SSH host-key semantics.
        var guardResult = await _guard.ValidateHostAsync(config.Host, config.Port, ct);
        if (!guardResult.Allowed)
        {
            _logger.LogWarning(
                "SFTP ingress: org {OrgId} — host blocked by SSRF guard, skipping poll. {Reason}",
                organisationId, guardResult.Reason);
            return 0;
        }

        _logger.LogInformation(
            "SFTP ingress: connecting to {User}@{Host}:{Port} dir={Dir} for org {OrgId}.",
            config.Username, config.Host, config.Port, config.RemoteDirectory, organisationId);

        // Who this server claims to be is part of the decision. Before this, the poller connected
        // to, listed and imported files from ANY server that answered on the configured host and
        // port with the configured password — a rebuilt (or substituted) supplier server was
        // indistinguishable from the original, with no warning and no log line.
        var verifier = new SshHostKeyVerifier(
            "SFTP polling", SshHostKeyPolicy.Parse(config.HostKeyFingerprints));

        ISftpSession session;
        try
        {
            session = _sftpClientFactory.Connect(
                config.Host, config.Port, config.Username, password, verifier);
        }
        catch (SshHostKeyRejectedException ex)
        {
            // Logged as its own case rather than folded into the generic connect failure: an
            // operator looking at why polling stopped must be able to tell "the supplier's server
            // identity changed" from "the network was down", because only one of them needs them to
            // pick up the phone. Rethrown so the job fails visibly instead of reporting 0 files —
            // silently importing nothing looks exactly like an empty directory.
            _logger.LogError(
                "SFTP ingress: refused to poll for org {OrgId} ({Host}:{Port}) — the server's host key " +
                "changed. Seen {Observed}, pinned {Pinned}. {Guidance}",
                organisationId, config.Host, config.Port, ex.Observed, string.Join(", ", ex.Pinned), ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SFTP ingress: connection failed for org {OrgId} ({Host}:{Port}).",
                organisationId, config.Host, config.Port);
            throw;
        }

        using (session)
        {
            // Trust-on-first-use: record what this server presented so the NEXT poll is verified
            // against it. Written before any file is read, so a poll that crashes mid-import still
            // leaves the connection pinned.
            //
            // The config above was read AsNoTracking, so mutating THAT instance would be a write the
            // change tracker never sees — a silent no-op with the shape of a fix. Re-read tracked,
            // org-scoped, and save.
            if (verifier.LearnedFingerprint is { } learned)
            {
                var tracked = await _db.Set<SftpIngressConfig>()
                    .FirstOrDefaultAsync(c => c.Id == config.Id && c.OrgId == organisationId, ct);

                if (tracked is not null)
                {
                    tracked.HostKeyFingerprints = learned;
                    tracked.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);

                    _logger.LogInformation(
                        "SFTP ingress: recorded host key {Fingerprint} for org {OrgId} ({Host}:{Port}) on first " +
                        "connection. Later polls will refuse a different key.",
                        learned, organisationId, config.Host, config.Port);
                }
            }

            return await PollSessionAsync(organisationId, supplierId, config.RemoteDirectory, session, ct);
        }
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async Task<int> PollSessionAsync(
        Guid organisationId,
        Guid? supplierId,
        string remoteDirectory,
        ISftpSession session,
        CancellationToken ct)
    {
        var remoteFiles = session.ListFileNames(remoteDirectory).ToList();
        _logger.LogInformation(
            "SFTP ingress: org {OrgId} — {Total} file(s) found in {Dir}.",
            organisationId, remoteFiles.Count, remoteDirectory);

        var imported = 0;

        foreach (var remotePath in remoteFiles)
        {
            ct.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(remotePath);
            if (!AcceptedExtensions.Contains(extension))
            {
                _logger.LogDebug(
                    "SFTP ingress: skipping unsupported extension {Ext} ({Path}).",
                    extension, remotePath);
                continue;
            }

            // ── dedupe / resume-on-conflict by (OrgId, RemotePath) ───────────
            // A pre-existing claim (a prior scheduled poll or a Hangfire retry) is either already
            // satisfied (SKIP) or an abandoned claim whose order a transient failure never created
            // (RESUME under the same pre-generated id). See IngressDedupe for the full contract.
            var existingClaim = await _db.Set<ImportedSftpFile>()
                .FirstOrDefaultAsync(f => f.OrgId == organisationId && f.RemotePath == remotePath, ct);

            if (existingClaim is not null &&
                await IngressDedupe.ClaimSatisfiedAsync(_db, organisationId, existingClaim.OrderId, ct))
            {
                _logger.LogDebug(
                    "SFTP ingress: org {OrgId} already imported {Path}. Skipping.",
                    organisationId, remotePath);
                continue;
            }

            // ── bounded download + hash (H2) ─────────────────────────────────
            // Stream through the shared bounded-read helper so a lying/endless remote
            // file can never materialize more than the cap+1 bytes in memory (the prior
            // DownloadFile materialized the WHOLE file before the size check — an OOM
            // vector). At most MaxFileBytes+1 bytes are ever read; overflow throws
            // CatalogFileTooLargeException, which we catch and skip the file.
            MemoryStream fileBytes;
            try
            {
                using var remote = session.OpenRead(remotePath);
                fileBytes = await BoundedRead.CopyAsync(remote, IngressLimits.MaxFileBytes, ct);
            }
            catch (CatalogFileTooLargeException)
            {
                _logger.LogWarning(
                    "SFTP ingress: org {OrgId} — skipping {Path} (exceeds {Max} byte cap).",
                    organisationId, remotePath, IngressLimits.MaxFileBytes);
                continue;
            }

            using var _fileBytes = fileBytes;
            fileBytes.Position = 0;

            var fileName = Path.GetFileName(remotePath);
            var contentType = ExtensionToContentType(extension);

            Guid orderId;
            ImportedSftpFile claim;
            if (existingClaim is not null)
            {
                // RESUME: the order under this pre-generated id was never created (transient failure).
                // Recreate it under the SAME id — CreateStubAsync is a find-or-create on that key.
                claim = existingClaim;
                orderId = existingClaim.OrderId;
                _logger.LogInformation(
                    "SFTP ingress: org {OrgId} — resuming abandoned import of {Path} (order {OrderId} was never created).",
                    organisationId, remotePath, orderId);
            }
            else
            {
                // ── CLAIM-FIRST (idempotency) ────────────────────────────────
                // Pre-generate the order id, store it on the claim, and commit the claim BEFORE
                // creating the order. The unique index on (OrgId, RemotePath) is the real guard: a
                // concurrent same-org poll that got past the fast-path above hits a 23505 here and
                // SKIPS (it must not resume — the winner is mid-flight), so the file can never be
                // imported as a DUPLICATE order. The stored OrderId lets a LATER retry tell a
                // transient-abandoned claim (resume) from a completed one (skip).
                orderId = Guid.NewGuid();
                claim = new ImportedSftpFile
                {
                    Id = Guid.NewGuid(),
                    OrgId = organisationId,
                    RemotePath = remotePath,
                    FileHash = ComputeSha256Hex(fileBytes),
                    OrderId = orderId,
                    ImportedAt = DateTime.UtcNow,
                };
                fileBytes.Position = 0;
                _db.Set<ImportedSftpFile>().Add(claim);
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
                {
                    _db.Entry(claim).State = EntityState.Detached;
                    _logger.LogInformation(
                        "SFTP ingress: org {OrgId} — {Path} claimed concurrently (unique violation); skipping duplicate import.",
                        organisationId, remotePath);
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
                // terminal so a genuinely-bad file is bounded (not retried forever). A TRANSIENT
                // infra failure instead THROWS out of CreateStubAsync and propagates — Hangfire
                // retries the job and the claim, still holding its real OrderId with no order,
                // is RESUMED next run (no silent lost order).
                _logger.LogWarning(
                    "SFTP ingress: org {OrgId} — {Mode} order stub creation permanently failed for {Path}; marking claim terminal: {Error}",
                    organisationId, supplierId is null ? "unrouted" : "routed", remotePath, stubResult.Error);
                claim.OrderId = IngressDedupe.TerminalOrderId;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            await _parseJobEnqueuer.EnqueueAsync(orderId, organisationId, ct);
            imported++;

            _logger.LogInformation(
                "SFTP ingress: org {OrgId} — imported {Path} → order {OrderId}.",
                organisationId, remotePath, orderId);
        }

        _logger.LogInformation(
            "SFTP ingress: org {OrgId} — poll complete. Imported={Imported}.",
            organisationId, imported);

        return imported;
    }

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

    private static string ComputeSha256Hex(Stream stream)
    {
        var startPos = stream.Position;
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        stream.Position = startPos;
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
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
