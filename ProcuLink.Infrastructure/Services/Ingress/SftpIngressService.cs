using System.Security.Cryptography;
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
    private readonly IOrderService _orderService;
    private readonly IParseJobEnqueuer _parseJobEnqueuer;
    private readonly DeliveryEncryptionService _encryption;
    private readonly ISftpClientFactory _sftpClientFactory;
    private readonly OutboundRequestGuard _guard;
    private readonly ILogger<SftpIngressService> _logger;

    public SftpIngressService(
        ProcuLinkDbContext db,
        IOrderService orderService,
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

        var password = _encryption.Decrypt(config.EncryptedPassword);
        if (password is null)
        {
            _logger.LogWarning(
                "SFTP ingress: cannot decrypt password for org {OrgId}. Skipping poll.",
                organisationId);
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

        ISftpSession session;
        try
        {
            session = _sftpClientFactory.Connect(config.Host, config.Port, config.Username, password);
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

            // ── dedupe by (OrgId, RemotePath) ────────────────────────────────
            var alreadyImported = await _db.Set<ImportedSftpFile>()
                .AnyAsync(f => f.OrgId == organisationId && f.RemotePath == remotePath, ct);

            if (alreadyImported)
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

            var hash = ComputeSha256Hex(fileBytes);
            fileBytes.Position = 0;

            var fileName = Path.GetFileName(remotePath);
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
                    "SFTP ingress: org {OrgId} — CreateStubAsync failed for {Path}: {Error}",
                    organisationId, remotePath, stubResult.Error);
                continue;
            }

            _db.Set<ImportedSftpFile>().Add(new ImportedSftpFile
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                RemotePath = remotePath,
                FileHash = hash,
                ImportedAt = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);

            await _parseJobEnqueuer.EnqueueAsync(stubResult.Value!.Id, organisationId, ct);
            imported++;

            _logger.LogInformation(
                "SFTP ingress: org {OrgId} — imported {Path} → order {OrderId}.",
                organisationId, remotePath, stubResult.Value!.Id);
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
