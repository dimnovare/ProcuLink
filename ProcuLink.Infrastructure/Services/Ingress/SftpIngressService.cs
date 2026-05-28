using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production implementation of <see cref="ISftpIngressService"/>.
/// Polls an SFTP server, downloads unseen files, and creates order stubs
/// via <see cref="IOrderService.CreateStubAsync"/>.
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
    private readonly DeliveryEncryptionService _encryption;
    private readonly ISftpClientFactory _sftpClientFactory;
    private readonly ILogger<SftpIngressService> _logger;

    public SftpIngressService(
        ProcuLinkDbContext db,
        IOrderService orderService,
        DeliveryEncryptionService encryption,
        ISftpClientFactory sftpClientFactory,
        ILogger<SftpIngressService> logger)
    {
        _db = db;
        _orderService = orderService;
        _encryption = encryption;
        _sftpClientFactory = sftpClientFactory;
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

        var password = _encryption.Decrypt(config.EncryptedPassword);
        if (password is null)
        {
            _logger.LogWarning(
                "SFTP ingress: cannot decrypt password for org {OrgId}. Skipping poll.",
                organisationId);
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
            return await PollSessionAsync(organisationId, config.RemoteDirectory, session, ct);
        }
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async Task<int> PollSessionAsync(
        Guid organisationId,
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

            // ── download + hash ───────────────────────────────────────────────
            using var fileBytes = session.DownloadFile(remotePath);
            var hash = ComputeSha256Hex(fileBytes);
            fileBytes.Position = 0;

            var fileName = Path.GetFileName(remotePath);
            var contentType = ExtensionToContentType(extension);

            // CreateStubAsync needs a supplierId — SFTP ingress is org-scoped (no single supplier).
            // Use Guid.Empty as a placeholder; downstream parsing / review will resolve it.
            // TODO: once SftpIngressConfig carries a DefaultSupplierId, pass it here.
            var stubResult = await _orderService.CreateStubAsync(
                organisationId,
                Guid.Empty,
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
