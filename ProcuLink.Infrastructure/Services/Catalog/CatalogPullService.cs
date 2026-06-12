using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Transform.Catalog;

namespace ProcuLink.Infrastructure.Services.Catalog;

/// <summary>
/// The single hardened fetch pipeline for catalog pull sources (design §X-cut 1):
/// guard-before-connect → timeouts + 5-minute deadline → bounded read → hash skip →
/// shared parse → idempotent upsert → honest status. Per-protocol seams
/// (<see cref="ISftpClientFactory"/> / <see cref="IFtpFetchClientFactory"/>) only open
/// streams; every security control lives HERE so no channel can drift.
///
/// M4 — error sanitisation: raw transport exceptions are logged at Debug only; the
/// persisted <c>last_sync_error</c> and the rethrown <see cref="CatalogSyncException"/>
/// carry exclusively the enumerated safe messages below (no host/username/banner text,
/// no inner exception), so Hangfire dashboards and Sentry never see tenant secrets.
/// </summary>
public sealed class CatalogPullService : ICatalogPullService
{
    // ── Enumerated safe messages (M4) — the ONLY strings that may be persisted ──
    internal const string ErrAuthFailed       = "Authentication failed — check the username and password.";
    internal const string ErrConnectFailed    = "Could not connect to the server.";
    internal const string ErrTimedOut         = "The catalog sync timed out.";
    internal const string ErrFileNotFound     = "The remote file could not be read — check the remote path.";
    internal const string ErrHostNotAllowed   = "The configured host is not allowed.";
    internal const string ErrCredentialsUnreadable = "Stored credentials could not be read — re-enter the password.";
    internal const string ErrSupplierGone     = "Supplier no longer exists.";
    internal const string ErrNoCodeColumn     = "No rows with a product code were found — ensure the file has a 'code' column.";
    internal const string ErrParseFailed      = "Could not read the catalog file. Provide a CSV or XLSX with a 'code' column.";
    internal const string ErrUnexpected       = "Catalog sync failed before the file could be imported.";
    internal const string ErrNoSourceConfigured = "No catalog source is configured for this supplier.";

    private readonly ProcuLinkDbContext        _db;
    private readonly DeliveryEncryptionService _encryption;
    private readonly OutboundRequestGuard      _guard;
    private readonly ISftpClientFactory        _sftpFactory;
    private readonly IFtpFetchClientFactory    _ftpFactory;
    private readonly ISupplierCatalogService   _catalog;
    private readonly ILogger<CatalogPullService> _logger;

    /// <summary>H3 — overall per-pull deadline (linked CTS over the caller token). Internal test seam.</summary>
    internal TimeSpan OverallDeadline { get; set; } = TimeSpan.FromMinutes(5);

    public CatalogPullService(
        ProcuLinkDbContext          db,
        DeliveryEncryptionService   encryption,
        OutboundRequestGuard        guard,
        ISftpClientFactory          sftpFactory,
        IFtpFetchClientFactory      ftpFactory,
        ISupplierCatalogService     catalog,
        ILogger<CatalogPullService> logger)
    {
        _db          = db;
        _encryption  = encryption;
        _guard       = guard;
        _sftpFactory = sftpFactory;
        _ftpFactory  = ftpFactory;
        _catalog     = catalog;
        _logger      = logger;
    }

    // ── Pull (persists success; throws sanitized on failure) ──────────────────

    public async Task<CatalogPullResult> PullAsync(Guid orgId, Guid sourceId, CancellationToken ct)
    {
        var source = await _db.SupplierCatalogSources
            .FirstOrDefaultAsync(s => s.Id == sourceId && s.OrgId == orgId, ct)
            ?? throw new CatalogSyncException(ErrNoSourceConfigured);

        try
        {
            var fetched = await FetchAndParseAsync(source, ct);

            if (fetched.UnchangedSkip)
            {
                source.LastSyncAt = DateTime.UtcNow;
                source.LastSyncStatus = "unchanged";
                source.LastSyncError = null;
                source.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return new CatalogPullResult(
                    "unchanged",
                    source.LastSyncCreated ?? 0, source.LastSyncUpdated ?? 0, source.LastSyncSkipped ?? 0,
                    fetched.FileHash);
            }

            var drafts = fetched.Parse!.Drafts;
            var withCode = drafts.Count(d => !string.IsNullOrWhiteSpace(d.Code));
            if (withCode == 0)
                throw new CatalogSyncException(ErrNoCodeColumn);

            var (created, updated) = await _catalog.UpsertManyAsync(orgId, source.SupplierId, drafts, ct);
            var skipped = drafts.Count - withCode;

            source.LastSyncAt = DateTime.UtcNow;
            source.LastSyncStatus = "ok";
            source.LastSyncError = null;
            source.LastSyncCreated = created;
            source.LastSyncUpdated = updated;
            source.LastSyncSkipped = skipped;
            source.LastFileHash = fetched.FileHash;
            source.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new CatalogPullResult("ok", created, updated, skipped, fetched.FileHash);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller/host shutdown — let Hangfire reschedule, no status mutation
        }
        catch (Exception ex)
        {
            throw Sanitize(ex, source.Id);
        }
    }

    // ── Test fetch (read-only honesty probe — never writes) ───────────────────

    public async Task<CatalogTestFetchResult> TestFetchAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var source = await _db.SupplierCatalogSources
            .AsNoTracking() // read-only by construction — the source row stays byte-identical
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.SupplierId == supplierId, ct);

        if (source is null)
            return CatalogTestFetchResult.Failure(ErrNoSourceConfigured);

        FetchOutcome fetched;
        try
        {
            fetched = await FetchAndParseAsync(source, ct, skipUnchangedShortCircuit: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CatalogTestFetchResult.Failure(Sanitize(ex, source.Id).Message);
        }

        var parse = fetched.Parse!;
        var headers = parse.HeaderColumns;
        var colMap = SupplierCatalogFileParser.MapHeaderColumns(headers);

        var mapped = new Dictionary<string, string>();
        var unmapped = new List<string>();
        for (var i = 0; i < headers.Count; i++)
        {
            var name = headers[i]?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;
            if (colMap.TryGetValue(i, out var canonical))
                mapped.TryAdd(name, canonical);
            else
                unmapped.Add(name);
        }

        var withCode = parse.Drafts.Count(d => !string.IsNullOrWhiteSpace(d.Code));

        return new CatalogTestFetchResult(
            Ok: true,
            Error: null,
            FileName: fetched.FileName,
            Bytes: fetched.Bytes,
            DetectedFormat: parse.Format,
            HeaderColumns: headers,
            MappedFields: mapped,
            UnmappedColumns: unmapped,
            ParsedRows: parse.Drafts.Count,
            RowsWithCode: withCode,
            SampleRows: parse.Drafts.Take(5)
                .Select(d => new CatalogSampleRow(d.Code, d.Name, d.Unit, d.Price, d.Currency, d.Barcode, d.ExternalId))
                .ToList());
    }

    // ── Shared hardened fetch path ─────────────────────────────────────────────

    private sealed record FetchOutcome(
        bool UnchangedSkip,
        string FileHash,
        string? FileName,
        long Bytes,
        CatalogFileParseResult? Parse);

    private async Task<FetchOutcome> FetchAndParseAsync(
        SupplierCatalogSource source, CancellationToken ct, bool skipUnchangedShortCircuit = false)
    {
        // Supplier must still exist — a pull for a soft-deleted supplier would silently
        // resurrect catalog rows under it.
        var supplierExists = await _db.Suppliers
            .AnyAsync(s => s.Id == source.SupplierId && s.OrgId == source.OrgId && s.DeletedAt == null, ct);
        if (!supplierExists)
            throw new CatalogSyncException(ErrSupplierGone);

        // Credentials: write-only AES-GCM envelope. Decrypt returns null on any error
        // (wrong key, corrupt envelope) — surface that honestly instead of an auth error.
        var password = string.Empty;
        if (!string.IsNullOrEmpty(source.EncryptedPassword))
        {
            password = _encryption.Decrypt(source.EncryptedPassword)
                ?? throw new CatalogSyncException(ErrCredentialsUnreadable);
        }
        else if (source.Protocol is "sftp" or "ftps")
        {
            // sftp/ftps require a stored password (validated at save time; defensive here).
            throw new CatalogSyncException(ErrCredentialsUnreadable);
        }

        // H3: one overall deadline for guard + connect + download + parse.
        using var deadlineCts = new CancellationTokenSource(OverallDeadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
        var token = linkedCts.Token;

        // SSRF guard IMMEDIATELY before connect (every poll AND test-fetch). Residual risk
        // L1 (accepted): a DNS rebind between this resolution and the library's own
        // re-resolution at connect is the same documented TOCTOU window the delivery
        // dispatchers carry (SftpDeliveryDispatcher) — pinning the IP would break SFTP
        // host-key / TLS hostname semantics. The separate PASV-redirect hole is closed by
        // PASVEX in the FTP factory (H1).
        var guardResult = await _guard.ValidateHostAsync(source.Host, source.Port, token);
        if (!guardResult.Allowed)
            throw new CatalogSyncException(ErrHostNotAllowed);

        var data = await DownloadAsync(source, password, token);

        string fileHash;
        using (data)
        {
            var byteCount = data.Length; // captured up front — the CSV parser's StreamReader closes the stream
            fileHash = Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, (int)byteCount)));

            var fileName = Path.GetFileName(source.RemotePath.Replace('\\', '/'));

            // Unchanged-skip: same bytes as the last completed import AND no failure since
            // (LastSyncError == null distinguishes "last completed fine" even while the
            // job's soft lock has already flipped LastSyncStatus to 'running').
            if (!skipUnchangedShortCircuit
                && !string.IsNullOrEmpty(source.LastFileHash)
                && string.Equals(fileHash, source.LastFileHash, StringComparison.OrdinalIgnoreCase)
                && source.LastSyncError is null
                && source.LastSyncStatus is "ok" or "unchanged" or "running")
            {
                return new FetchOutcome(UnchangedSkip: true, fileHash, fileName, byteCount, Parse: null);
            }

            data.Position = 0;
            var parse = source.FileFormat switch
            {
                "csv"  => await SupplierCatalogFileParser.ParseCsvAsync(data, token),
                "xlsx" => SupplierCatalogFileParser.ParseXlsx(data),
                _      => await SupplierCatalogFileParser.ParseByFileNameAsync(data, fileName, token),
            };

            return new FetchOutcome(UnchangedSkip: false, fileHash, fileName, byteCount, parse);
        }
    }

    private async Task<MemoryStream> DownloadAsync(
        SupplierCatalogSource source, string password, CancellationToken token)
    {
        switch (source.Protocol)
        {
            case "sftp":
                // SSH.NET's Connect is synchronous (no CT). The factory's 30 s socket
                // timeouts (H3) bound it; Task.Run + WaitAsync additionally honours the
                // overall deadline so a stalling server can never pin this job past it.
                return await Task.Run(async () =>
                {
                    using var session = _sftpFactory.Connect(
                        source.Host, source.Port, source.Username ?? string.Empty, password);
                    using var remote = session.OpenRead(source.RemotePath);
                    return await BoundedRead.CopyAsync(remote, IngressLimits.MaxFileBytes, token);
                }, CancellationToken.None).WaitAsync(token);

            case "ftp":
            case "ftps":
                using (var ftp = _ftpFactory.Connect(
                           source.Host, source.Port,
                           string.IsNullOrWhiteSpace(source.Username) ? "anonymous" : source.Username,
                           password, explicitTls: source.Protocol == "ftps"))
                {
                    return await ftp.DownloadAsync(source.RemotePath, IngressLimits.MaxFileBytes, token);
                }

            default:
                throw new CatalogSyncException(ErrUnexpected); // unreachable: protocol validated at save
        }
    }

    // ── M4 — sanitized error mapping ───────────────────────────────────────────

    /// <summary>
    /// Maps any pipeline exception to a <see cref="CatalogSyncException"/> carrying ONLY an
    /// enumerated safe message and NO inner exception. The raw exception (which may embed
    /// <c>user@host</c>, banners, paths) is logged at Debug level only.
    /// </summary>
    private CatalogSyncException Sanitize(Exception ex, Guid sourceId)
    {
        if (ex is CatalogSyncException already)
            return already;

        _logger.LogDebug(ex, "Catalog pull failed for source {SourceId} (sanitized for persistence).", sourceId);

        var safe = ex switch
        {
            CatalogFileTooLargeException tooLarge => tooLarge.Message,
            CatalogTooLargeException rowCap       => rowCap.Message,
            Renci.SshNet.Common.SshAuthenticationException     => ErrAuthFailed,
            FluentFTP.Exceptions.FtpAuthenticationException    => ErrAuthFailed,
            Renci.SshNet.Common.SftpPathNotFoundException      => ErrFileNotFound,
            Renci.SshNet.Common.SftpPermissionDeniedException  => ErrFileNotFound,
            FluentFTP.Exceptions.FtpCommandException           => ErrFileNotFound,
            FluentFTP.Exceptions.FtpSecurityNotAvailableException => ErrConnectFailed,
            Renci.SshNet.Common.SshOperationTimeoutException   => ErrTimedOut,
            TimeoutException                                   => ErrTimedOut,
            OperationCanceledException                         => ErrTimedOut, // overall deadline (caller ct rethrown upstream)
            SocketException                                    => ErrConnectFailed,
            Renci.SshNet.Common.SshConnectionException         => ErrConnectFailed,
            FluentFTP.Exceptions.FtpException                  => ErrConnectFailed,
            IOException                                        => ErrConnectFailed,
            InvalidDataException                               => ErrParseFailed,
            FormatException                                    => ErrParseFailed,
            _                                                  => ErrUnexpected,
        };

        return new CatalogSyncException(safe);
    }
}
