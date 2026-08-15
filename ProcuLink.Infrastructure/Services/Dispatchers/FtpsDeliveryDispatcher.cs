using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// FTPS (explicit-TLS FTP) delivery dispatcher — uploads the artifact to the
/// configured remote directory via FTPS using FluentFTP. Mirrors the
/// SftpDeliveryDispatcher contract: never throws, always returns a DeliveryResult
/// with a humanised error message.
/// </summary>
public sealed class FtpsDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly ILogger<FtpsDeliveryDispatcher> _logger;
    private readonly OutboundRequestGuard _guard;

    /// <summary>
    /// Test seam — how a connected upload session is obtained. Null in production (the only PUBLIC
    /// constructor leaves it null, and Microsoft DI only ever sees public constructors), where the
    /// session is a connected FluentFTP client.
    ///
    /// <para>
    /// Same reason as <see cref="SftpDeliveryDispatcher"/>: the single expression that carries the
    /// operator's <c>overwriteExisting</c> setting out of the saved config and onto the transfer
    /// lives inside <see cref="DispatchAsync"/>, and was reachable by no test at all — hardcoding
    /// it to <c>true</c> left the whole suite green while the operator's OFF setting became a no-op
    /// for real purchase orders.
    /// </para>
    /// </summary>
    private readonly Func<IFtpsUploadSession>? _sessionFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Ftps;

    // Safe in the sense ResendSafety means, and ONLY that sense: a re-send after an unknown outcome
    // cannot DUPLICATE at the supplier. The remote path is a deterministic function of the order, so
    // a re-send either replaces its own file (overwriteExisting on, the default) or refuses outright
    // (overwriteExisting off) — neither produces a second copy.
    //
    // It does NOT mean the re-send completes: with overwriteExisting off it refuses, and
    // DeliveryService parks that combination for a human rather than retrying it into dead-letter
    // (CannotRepairItsOwnFile). See SftpDeliveryDispatcher.OverwriteExistingFromConfig.
    public ResendSafety ResendSafety => ResendSafety.Safe;

    // No HTTP status codes exist on this channel at all — every DeliveryResult it returns carries a
    // null ResponseCode, so the classification never reaches its 400 branch and there is no supplier
    // reason to capture. Declared explicitly rather than inherited: the whole point of the capability
    // is that a dispatcher states what it can see, and "nothing, because there is nothing to see" is
    // an answer, not an omission.
    public bool CapturesSupplierResponseBody => false;

    public FtpsDeliveryDispatcher(ILogger<FtpsDeliveryDispatcher> logger, OutboundRequestGuard guard)
        : this(logger, guard, sessionFactory: null)
    {
    }

    internal FtpsDeliveryDispatcher(
        ILogger<FtpsDeliveryDispatcher> logger,
        OutboundRequestGuard guard,
        Func<IFtpsUploadSession>? sessionFactory)
    {
        _logger = logger;
        _guard = guard;
        _sessionFactory = sessionFactory;
    }

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct,
        string? idempotencyKey = null,
        bool isTestFire = false)
    {
        // isTestFire is deliberately unused: a file drop has no covering message to reword. What a
        // test leaves behind here is a FILE, disclosed to the operator in the UI before they fire it.
        // A3 idempotency: FTPS is already idempotent by construction. The remote filename is a
        // deterministic function of the ORDER (PO number + order id — see DeliveryService.BuildFileName),
        // so a crash-recovery re-upload targets the same path rather than creating a second file —
        // no supplier idempotency key is needed (idempotencyKey is intentionally unused here).
        FtpsConfig? cfg;
        FtpsCredentials? creds;

        try
        {
            cfg = JsonSerializer.Deserialize<FtpsConfig>(config.ConfigJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FTPS delivery config JSON malformed.");
            return new DeliveryResult(false, "FTPS delivery configuration could not be parsed.");
        }

        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Host))
            return new DeliveryResult(false, "FTPS delivery configuration is invalid — host is required.");

        try
        {
            creds = string.IsNullOrEmpty(decryptedCredentials)
                ? null
                : JsonSerializer.Deserialize<FtpsCredentials>(decryptedCredentials, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FTPS delivery credentials JSON malformed.");
            return new DeliveryResult(false, "FTPS delivery configuration could not be parsed.");
        }

        if (creds is null || string.IsNullOrWhiteSpace(creds.Username))
            return new DeliveryResult(false, "FTPS delivery credentials are missing — username is required.");

        var host = cfg.Host;
        var port = cfg.Port > 0 ? cfg.Port : 21;
        var remoteDir = NormaliseRemoteDir(cfg.RemotePath);
        var remotePath = $"{remoteDir.TrimEnd('/')}/{SanitiseFileName(fileName)}";
        var makeDirectories = cfg.MakeDirectories;
        var timeoutSeconds = cfg.TimeoutSeconds is > 0
            ? cfg.TimeoutSeconds!.Value
            : SftpDeliveryDispatcher.DefaultTimeoutSeconds;
        var timeoutMs = timeoutSeconds * 1000;

        // Linked token source so we can enforce our own timeout on top of the caller's token.
        // Unlike SFTP before this packet, this channel always HAD a deadline covering the transfer;
        // what it did not have was a deadline that holds when the transfer ignores the token. That
        // is enforced in UploadCoreAsync — see the WaitAsync note there.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linkedCts.Token;

        var allowInvalidCertificate = cfg.AllowInvalidCertificate;

        var client = new AsyncFtpClient(host, creds.Username, creds.Password ?? string.Empty, port);

        // Secure by default: reject certificates that do not pass OS/CA validation.
        // An explicit opt-in escape hatch (AllowInvalidCertificate = true in the per-supplier
        // delivery config JSON) allows self-signed or expired certificates only when the
        // operator has consciously accepted the risk for a specific supplier.
        client.Config.ValidateAnyCertificate = false;
        client.ValidateCertificate += (_, e) =>
        {
            e.Accept = ShouldAcceptCertificate(e.PolicyErrors, allowInvalidCertificate);
        };

        client.Config.EncryptionMode = FtpEncryptionMode.Explicit; // FTPS = explicit TLS (AUTH TLS on port 21)
        client.Config.ConnectTimeout = timeoutMs;
        client.Config.ReadTimeout = timeoutMs;
        client.Config.DataConnectionConnectTimeout = timeoutMs;
        client.Config.DataConnectionReadTimeout = timeoutMs;

        try
        {
            await using (client.ConfigureAwait(false))
            {
                // ── SSRF guard — re-validated IMMEDIATELY before Connect to shrink the
                // DNS-rebinding TOCTOU window. FluentFTP connects by hostname (re-resolving) and
                // pinning the IP would break TLS certificate/hostname validation, so the tightest
                // available mitigation is to re-resolve+validate right before connect. Kept inside
                // the await-using so the client is disposed even on the guard-block return path.
                var guardResult = await _guard.ValidateHostAsync(host, port, token).ConfigureAwait(false);
                if (!guardResult.Allowed)
                    return new DeliveryResult(false, $"FTPS delivery blocked: {guardResult.Reason}");

                IFtpsUploadSession session;
                if (_sessionFactory is null)
                {
                    await client.Connect(token).ConfigureAwait(false);
                    session = new FluentFtpUploadSession(client);
                }
                else
                {
                    session = _sessionFactory();
                }

                return await UploadCoreAsync(
                    session,
                    content,
                    remotePath,
                    makeDirectories,
                    // THE wire between the operator's saved setting and the live transfer. Covered
                    // end-to-end by FileDropOverwriteWiringTests — hardcoding this to true makes an
                    // OFF setting a no-op on the live path and must not be able to pass.
                    SftpDeliveryDispatcher.OverwriteExistingFromConfig(config.ConfigJson),
                    token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout fired, not the caller's cancellation.
            return new DeliveryResult(false, SftpDeliveryDispatcher.TransferTimedOut("FTPS", timeoutSeconds));
        }
        catch (FtpAuthenticationException)
        {
            return new DeliveryResult(false, "FTPS authentication failed — check the username and password.");
        }
        catch (FtpSecurityNotAvailableException)
        {
            return new DeliveryResult(false, "FTPS encryption could not be negotiated with the server.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new DeliveryResult(false, $"FTPS connection failed: {ex.Message}");
        }
        catch (TimeoutException)
        {
            return new DeliveryResult(false, "FTPS connection timed out.");
        }
        catch (AuthenticationException ex)
        {
            // The TLS handshake itself failed. Overwhelmingly this is our own ValidateCertificate
            // callback refusing the server's certificate, and until this catch existed that landed in
            // the catch-all below — so an operator whose supplier runs a self-signed or misissued
            // certificate was told only "FTPS delivery failed before the upload could complete."
            // while the real cause sat in a log they cannot read. Proven live against a throwaway
            // pure-ftpd holding a self-signed certificate; see docs/ops/2026-08-01-wp38-delivery-channel-proof.md.
            _logger.LogWarning(ex, "FTPS delivery could not complete the TLS handshake.");
            return new DeliveryResult(false, DescribeTlsHandshakeFailure(allowInvalidCertificate));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FTPS delivery config or credentials JSON malformed.");
            return new DeliveryResult(false, "FTPS delivery configuration could not be parsed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTPS delivery failed unexpectedly.");
            return new DeliveryResult(false, "FTPS delivery failed before the upload could complete.");
        }
    }

    /// <summary>
    /// The operator-facing sentence for a failed TLS handshake. Branches on the supplier's own
    /// setting because the two situations need opposite next steps: with the override OFF the fix is
    /// almost always "get a trusted certificate, or consciously accept this one", while with it
    /// already ON the certificate cannot be the cause, so pointing at it would send the operator
    /// somewhere there is nothing to find.
    /// </summary>
    internal static string DescribeTlsHandshakeFailure(bool allowInvalidCertificate) =>
        allowInvalidCertificate
            ? "FTPS delivery failed: the secure connection to the server could not be established. " +
              "\"Allow invalid certificate\" is already on for this supplier, so the certificate is not " +
              "the cause — ask the supplier which TLS versions and ciphers their server accepts."
            : "FTPS delivery failed: the server's security certificate was not trusted. It is either " +
              "self-signed, expired, or issued to a different host name than the one configured. Ask the " +
              "supplier for a certificate signed by a public certificate authority for this host — or, if " +
              "you have confirmed the server's identity another way, turn on \"Allow invalid certificate\" " +
              "for this supplier.";

    /// <summary>
    /// The upload itself, once connected: the overwrite decision, the transfer to a temporary name,
    /// the move onto the supplier's file name, and the mapping of FluentFTP's status to a delivery
    /// outcome. Split out from the transport so the overwrite behaviour — a live-path decision about
    /// real purchase orders — is covered by a test that fails when it changes.
    ///
    /// <para><b>Nothing is ever written directly to the name the supplier reads</b>, for the same
    /// reason as SFTP: an FTP <c>STOR</c> creates the file at its final name and fills it, so a
    /// supplier polling the drop directory can collect it mid-transfer and import half a purchase
    /// order. The bytes go to <see cref="SftpDeliveryDispatcher.PartialUploadPath"/> in the same
    /// directory, and only a completed transfer is moved onto the real name — <c>RNFR</c>/<c>RNTO</c>,
    /// which the server performs as a single filesystem rename.
    /// </para>
    /// <para>
    /// Every remote call is wrapped in <c>WaitAsync(token)</c> as well as being handed the token.
    /// The token alone is a request the transfer may decline; <c>WaitAsync</c> returns on the
    /// deadline regardless, and the client disposal in <see cref="DispatchAsync"/> ends whatever was
    /// abandoned. An order that sat in <c>delivering</c> for hours is what the difference costs.
    /// </para>
    /// </summary>
    internal static async Task<DeliveryResult> UploadCoreAsync(
        IFtpsUploadSession session,
        byte[] content,
        string remotePath,
        bool makeDirectories,
        bool overwriteExisting,
        CancellationToken token)
    {
        var partialPath = SftpDeliveryDispatcher.PartialUploadPath(remotePath);

        // Refuse before spending the transfer, mirroring the SFTP path. The move below refuses
        // again — that one is the enforcement, this one is the courtesy.
        if (!overwriteExisting &&
            await session.FileExists(remotePath, token).WaitAsync(token).ConfigureAwait(false))
        {
            return new DeliveryResult(false, SftpDeliveryDispatcher.RefusedBecauseFileExists(remotePath));
        }

        await using var ms = new MemoryStream(content);
        var status = await session.UploadStream(
            ms,
            partialPath,
            // Always Overwrite, whatever the operator set: this is OUR temporary name, and a
            // partial left by a crashed earlier attempt must be replaceable or the supplier is
            // wedged for good. The operator's setting governs the destination, in the move below.
            FtpRemoteExists.Overwrite,
            createRemoteDir: makeDirectories,
            token: token).WaitAsync(token).ConfigureAwait(false);

        if (status != FtpStatus.Success)
        {
            return new DeliveryResult(false, "FTPS upload did not complete successfully.");
        }

        var moved = await session
            .MoveFile(partialPath, remotePath, RemoteExistsModeFor(overwriteExisting), token)
            .WaitAsync(token)
            .ConfigureAwait(false);

        if (moved)
        {
            return new DeliveryResult(true, null);
        }

        await TryDeleteAsync(session, partialPath, token).ConfigureAwait(false);

        // A move that declined under Skip mode means a file is at the destination — the operator's
        // "do not replace" doing its job, and it has to read to them exactly as the pre-transfer
        // refusal does. FluentFTP reports it as a bare false, so the reason is re-established here.
        return await ExistsQuietlyAsync(session, remotePath, token).ConfigureAwait(false)
            ? new DeliveryResult(false, SftpDeliveryDispatcher.RefusedBecauseFileExists(remotePath))
            : new DeliveryResult(false, SftpDeliveryDispatcher.CouldNotPublish(remotePath));
    }

    /// <summary>
    /// How the MOVE onto the supplier's file name treats a file already there. Overwrite (the
    /// default) keeps a crash-recovery re-drive able to repair its own earlier delivery; Skip
    /// refuses to touch what is there and <see cref="UploadCoreAsync"/> turns that refusal into a
    /// failed delivery.
    ///
    /// <para>
    /// This used to govern the UPLOAD. It governs the move now because the upload no longer targets
    /// the supplier's file name at all — but the setting it carries, and what each value means to an
    /// operator, are unchanged.
    /// </para>
    /// </summary>
    internal static FtpRemoteExists RemoteExistsModeFor(bool overwriteExisting) =>
        overwriteExisting ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip;

    /// <summary>
    /// Best-effort removal of our own temporary file. Never changes a delivery outcome the caller
    /// has already decided — a directory the account cannot delete from is not a delivery failure.
    /// </summary>
    private static async Task TryDeleteAsync(
        IFtpsUploadSession session, string path, CancellationToken token)
    {
        try { await session.DeleteFile(path, token).WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* deadline already fired; client disposal cleans up */ }
        catch (Exception) { /* leftover temp is litter, not a delivery outcome */ }
    }

    /// <summary>
    /// An existence probe used only to CHOOSE A SENTENCE after a failure has already been decided.
    /// A probe that throws must not be able to change the outcome, so it answers false.
    /// </summary>
    private static async Task<bool> ExistsQuietlyAsync(
        IFtpsUploadSession session, string path, CancellationToken token)
    {
        try { return await session.FileExists(path, token).WaitAsync(token).ConfigureAwait(false); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Determines whether a server certificate should be accepted.
    /// Secure by default: accept only when there are no policy errors.
    /// When <paramref name="allowInvalidCertificate"/> is explicitly <c>true</c> (opt-in per
    /// supplier config), policy errors are tolerated — this is an operator-conscious override
    /// for suppliers whose FTPS server uses a self-signed or expired certificate.
    /// </summary>
    internal static bool ShouldAcceptCertificate(SslPolicyErrors policyErrors, bool allowInvalidCertificate)
    {
        if (policyErrors == SslPolicyErrors.None) return true;
        return allowInvalidCertificate;
    }

    private static string NormaliseRemoteDir(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return ".";
        var trimmed = remotePath.Replace('\\', '/').Trim();
        return trimmed.StartsWith('/') ? trimmed : $"./{trimmed}";
    }

    internal static string SanitiseFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "delivery.bin";
        var safe = new string(fileName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        return safe.Trim('_').Length > 0 ? safe : "delivery.bin";
    }

    // ── Config + credentials POCOs ────────────────────────────────────────────

    private sealed record FtpsConfig(
        string Host,
        int Port,
        string? RemotePath,
        bool MakeDirectories,
        int? TimeoutSeconds,
        bool AllowInvalidCertificate = false);

    private sealed record FtpsCredentials(
        string Username,
        string? Password);

    // ── Upload seam ───────────────────────────────────────────────────────────

    /// <summary>
    /// The FluentFTP calls the upload makes. Exists so <see cref="UploadCoreAsync"/> — which owns
    /// the overwrite decision and the move onto the supplier's file name for real purchase orders —
    /// is testable without an FTPS server.
    /// </summary>
    internal interface IFtpsUploadSession
    {
        Task<bool> FileExists(string path, CancellationToken token);

        Task<FtpStatus> UploadStream(
            Stream input, string remotePath, FtpRemoteExists existsMode,
            bool createRemoteDir, CancellationToken token);

        /// <returns>
        /// False when the destination was occupied and <paramref name="existsMode"/> was
        /// <see cref="FtpRemoteExists.Skip"/> — FluentFTP's way of saying it declined, not that it
        /// broke. <see cref="UploadCoreAsync"/> turns that into the operator's refusal message.
        /// </returns>
        Task<bool> MoveFile(
            string path, string dest, FtpRemoteExists existsMode, CancellationToken token);

        Task DeleteFile(string path, CancellationToken token);
    }

    private sealed class FluentFtpUploadSession : IFtpsUploadSession
    {
        private readonly AsyncFtpClient _client;
        public FluentFtpUploadSession(AsyncFtpClient client) => _client = client;

        public Task<bool> FileExists(string path, CancellationToken token) =>
            _client.FileExists(path, token);

        public Task<FtpStatus> UploadStream(
            Stream input, string remotePath, FtpRemoteExists existsMode,
            bool createRemoteDir, CancellationToken token) =>
            _client.UploadStream(input, remotePath, existsMode, createRemoteDir, progress: null, token: token);

        // FluentFTP's MoveFile checks the destination itself and, under Overwrite, deletes it before
        // issuing RNFR/RNTO — the same delete-then-rename an SFTP server without posix-rename needs,
        // and with the same property: the window exposes an ABSENT destination, never a partial one.
        public Task<bool> MoveFile(
            string path, string dest, FtpRemoteExists existsMode, CancellationToken token) =>
            _client.MoveFile(path, dest, existsMode, token);

        public Task DeleteFile(string path, CancellationToken token) =>
            _client.DeleteFile(path, token);
    }
}
