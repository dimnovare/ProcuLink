using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
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
// Not sealed: a test subclass overrides CreateHttpClient() to inject a fake HTTP transport
// (mirrors HttpDeliveryDispatcher's CreateSendClient seam). The per-protocol factories are
// already faked; this seam lets the http branch run end-to-end without a live server.
public class CatalogPullService : ICatalogPullService
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
    internal const string ErrParseFailed      = "Could not read the catalog file. Provide a CSV, XLSX, or JSON catalog with a 'code' column.";
    internal const string ErrUnexpected       = "Catalog sync failed before the file could be imported.";
    internal const string ErrNoSourceConfigured = "No catalog source is configured for this supplier.";
    internal const string ErrUrlNotAllowed    = "The configured URL is not allowed.";
    internal const string ErrAuthConfigUnreadable = "Stored authentication settings could not be read — re-enter the credentials.";
    internal const string ErrHttpAuthFailed   = "Authentication failed.";
    internal const string ErrHttpStatus       = "The catalog server returned an error response.";

    private readonly ProcuLinkDbContext        _db;
    private readonly DeliveryEncryptionService _encryption;
    private readonly OutboundRequestGuard      _guard;
    private readonly ISftpClientFactory        _sftpFactory;
    private readonly IFtpFetchClientFactory    _ftpFactory;
    private readonly IHttpClientFactory        _httpClientFactory;
    private readonly HttpAuthApplier           _auth;
    private readonly ISupplierCatalogService   _catalog;
    private readonly ILogger<CatalogPullService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>H3 — overall per-pull deadline (linked CTS over the caller token). Internal test seam.</summary>
    internal TimeSpan OverallDeadline { get; set; } = TimeSpan.FromMinutes(5);

    public CatalogPullService(
        ProcuLinkDbContext          db,
        DeliveryEncryptionService   encryption,
        OutboundRequestGuard        guard,
        ISftpClientFactory          sftpFactory,
        IFtpFetchClientFactory      ftpFactory,
        IHttpClientFactory          httpClientFactory,
        ISupplierCatalogService     catalog,
        ILogger<CatalogPullService> logger)
    {
        _db                = db;
        _encryption        = encryption;
        _guard             = guard;
        _sftpFactory       = sftpFactory;
        _ftpFactory        = ftpFactory;
        _httpClientFactory = httpClientFactory;
        // ONE shared auth applier (B1) — identical none/apikey/bearer/basic/oauth2 model and
        // SSRF-guarded OAuth token fetch used by the delivery dispatcher.
        _auth              = new HttpAuthApplier(guard, logger);
        _catalog           = catalog;
        _logger            = logger;
    }

    /// <summary>
    /// Test seam: resolves the HTTP client used for the catalog fetch (and OAuth token request).
    /// Production returns the named <c>delivery</c> client, whose primary handler is the guard's
    /// connect-time-revalidating <see cref="SocketsHttpHandler"/> (DNS-rebind / redirect-to-private
    /// blocked at TCP connect) with a 30 s timeout. Tests override this to inject a fake transport.
    /// </summary>
    internal virtual HttpClient CreateHttpClient() => _httpClientFactory.CreateClient("delivery");

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

    /// <summary>Bytes downloaded from a source plus the routing hints used to pick a parser.</summary>
    private sealed record DownloadOutcome(MemoryStream Data, string? FileName, string? ContentType);

    private async Task<FetchOutcome> FetchAndParseAsync(
        SupplierCatalogSource source, CancellationToken ct, bool skipUnchangedShortCircuit = false)
    {
        // Supplier must still exist — a pull for a soft-deleted supplier would silently
        // resurrect catalog rows under it.
        var supplierExists = await _db.Suppliers
            .AnyAsync(s => s.Id == source.SupplierId && s.OrgId == source.OrgId && s.DeletedAt == null, ct);
        if (!supplierExists)
            throw new CatalogSyncException(ErrSupplierGone);

        var isHttp = source.Protocol is "http" or "https";

        // Credentials: write-only AES-GCM envelope. Decrypt returns null on any error
        // (wrong key, corrupt envelope) — surface that honestly instead of an auth error.
        var password = string.Empty;
        if (!isHttp && !string.IsNullOrEmpty(source.EncryptedPassword))
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

        var downloaded = isHttp
            ? await DownloadHttpAsync(source, token)
            : await DownloadFileChannelAsync(source, password, token);

        string fileHash;
        using (downloaded.Data)
        {
            var data = downloaded.Data;
            var byteCount = data.Length; // captured up front — the CSV parser's StreamReader closes the stream
            fileHash = Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, (int)byteCount)));

            var fileName = downloaded.FileName;

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
                "json" => await SupplierCatalogFileParser.ParseJsonAsync(data, token),
                // 'auto': http uses the server's Content-Type first (then filename, then CSV);
                // the file channels route on the remote file extension.
                _ when isHttp => await SupplierCatalogFileParser.ParseByContentTypeAsync(
                                     data, downloaded.ContentType, fileName, token),
                _      => await SupplierCatalogFileParser.ParseByFileNameAsync(data, fileName, token),
            };

            return new FetchOutcome(UnchangedSkip: false, fileHash, fileName, byteCount, parse);
        }
    }

    // ── SFTP / FTP(S) channel ─────────────────────────────────────────────────

    private async Task<DownloadOutcome> DownloadFileChannelAsync(
        SupplierCatalogSource source, string password, CancellationToken token)
    {
        // SSRF guard IMMEDIATELY before connect (every poll AND test-fetch). Residual risk
        // L1 (accepted): a DNS rebind between this resolution and the library's own
        // re-resolution at connect is the same documented TOCTOU window the delivery
        // dispatchers carry (SftpDeliveryDispatcher) — pinning the IP would break SFTP
        // host-key / TLS hostname semantics. The separate PASV-redirect hole is closed by
        // PASVEX in the FTP factory (H1).
        var guardResult = await _guard.ValidateHostAsync(source.Host, source.Port, token);
        if (!guardResult.Allowed)
            throw new CatalogSyncException(ErrHostNotAllowed);

        var fileName = Path.GetFileName(source.RemotePath.Replace('\\', '/'));

        switch (source.Protocol)
        {
            case "sftp":
                // SSH.NET's Connect is synchronous (no CT). The factory's 30 s socket
                // timeouts (H3) bound it; Task.Run + WaitAsync additionally honours the
                // overall deadline so a stalling server can never pin this job past it.
                var sftpData = await Task.Run(async () =>
                {
                    using var session = _sftpFactory.Connect(
                        source.Host, source.Port, source.Username ?? string.Empty, password);
                    using var remote = session.OpenRead(source.RemotePath);
                    return await BoundedRead.CopyAsync(remote, IngressLimits.MaxFileBytes, token);
                }, CancellationToken.None).WaitAsync(token);
                return new DownloadOutcome(sftpData, fileName, ContentType: null);

            case "ftp":
            case "ftps":
                using (var ftp = _ftpFactory.Connect(
                           source.Host, source.Port,
                           string.IsNullOrWhiteSpace(source.Username) ? "anonymous" : source.Username,
                           password, explicitTls: source.Protocol == "ftps"))
                {
                    var ftpData = await ftp.DownloadAsync(source.RemotePath, IngressLimits.MaxFileBytes, token);
                    return new DownloadOutcome(ftpData, fileName, ContentType: null);
                }

            default:
                throw new CatalogSyncException(ErrUnexpected); // unreachable: protocol validated at save
        }
    }

    // ── HTTP(S) channel (plan 2026-06-12 v2 — B3) ─────────────────────────────

    /// <summary>
    /// Fetches the catalog over http/https. Security controls, in order:
    ///  1. up-front <see cref="OutboundRequestGuard.ValidateAsync"/> on the URL (and the OAuth
    ///     token URL inside the shared auth applier) — rejects loopback/private/metadata targets;
    ///  2. the request itself goes through the named <c>delivery</c> client, whose primary
    ///     handler is the guard's connect-time-revalidating socket handler — so a DNS rebind or
    ///     an http redirect to a private IP is blocked AT TCP CONNECT (no fetch reaches the
    ///     attacker's internal target);
    ///  3. the response body is read through <see cref="BoundedRead"/> (cap+1) so a lying /
    ///     endless server can never materialize more than the ingress cap in memory;
    ///  4. the 30 s client timeout + the overall deadline bound the whole exchange.
    /// All failures map to enumerated safe messages via <see cref="Sanitize"/> upstream; the
    /// auth applier returns safe strings (never host/token/secret) on its own failures.
    /// </summary>
    private async Task<DownloadOutcome> DownloadHttpAsync(SupplierCatalogSource source, CancellationToken token)
    {
        var url = source.Url;
        if (string.IsNullOrWhiteSpace(url))
            throw new CatalogSyncException(ErrUnexpected); // url required for http — validated at save

        // (1) Up-front SSRF validation on the URL (scheme + DNS range).
        var guardResult = await _guard.ValidateAsync(url, token);
        if (!guardResult.Allowed)
            throw new CatalogSyncException(ErrUrlNotAllowed);

        // Decrypt the write-only auth-config envelope (decrypt returns null on any error).
        JsonElement creds = default;
        if (!string.IsNullOrEmpty(source.AuthConfigEncrypted))
        {
            var json = _encryption.Decrypt(source.AuthConfigEncrypted)
                ?? throw new CatalogSyncException(ErrAuthConfigUnreadable);
            try { creds = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts); }
            catch (JsonException) { throw new CatalogSyncException(ErrAuthConfigUnreadable); }
        }

        var method = string.IsNullOrWhiteSpace(source.HttpMethod) ? "GET" : source.HttpMethod.Trim().ToUpperInvariant();
        var client = CreateHttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        // (2) Apply auth via the shared applier (oauth2 fetches a fresh token, SSRF-guarded).
        var authError = await _auth.ApplyAsync(request, creds, client, token);
        if (authError is not null)
            // Auth applier returns SAFE strings; collapse to a single enumerated message so the
            // persisted status never carries a token-URL or guard reason fragment.
            throw new CatalogSyncException(ErrHttpAuthFailed);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new CatalogSyncException(ErrHttpStatus);

        // (3) Bounded streamed read — at most cap+1 bytes ever buffered.
        await using var responseStream = await response.Content.ReadAsStreamAsync(token);
        var data = await BoundedRead.CopyAsync(responseStream, IngressLimits.MaxFileBytes, token);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        // Filename hint: the URL's last path segment (drives 'auto' extension routing as a
        // fallback when the server sends no decisive Content-Type).
        var fileName = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetFileName(uri.AbsolutePath)
            : null;

        return new DownloadOutcome(data, string.IsNullOrWhiteSpace(fileName) ? null : fileName, contentType);
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
            // http(s): a guarded connect-time block (redirect/rebind to a private IP) surfaces
            // as HttpRequestException; map it (and any other transport failure) to the safe
            // connect message — the raw text (which may carry the host) is logged at Debug only.
            HttpRequestException                               => ErrConnectFailed,
            IOException                                        => ErrConnectFailed,
            InvalidDataException                               => ErrParseFailed,
            FormatException                                    => ErrParseFailed,
            _                                                  => ErrUnexpected,
        };

        return new CatalogSyncException(safe);
    }
}
