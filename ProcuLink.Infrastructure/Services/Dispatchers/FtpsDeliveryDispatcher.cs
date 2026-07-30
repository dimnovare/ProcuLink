using System.Net.Security;
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
        string? idempotencyKey = null)
    {
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
        var timeoutSeconds = cfg.TimeoutSeconds is > 0 ? cfg.TimeoutSeconds!.Value : 30;
        var timeoutMs = timeoutSeconds * 1000;

        // Linked token source so we can enforce our own timeout on top of the caller's token.
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
            return new DeliveryResult(false, "FTPS delivery timed out.");
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
    /// The upload itself, once connected: the overwrite decision, the transfer, and the mapping of
    /// FluentFTP's status to a delivery outcome. Split out from the transport so the overwrite
    /// behaviour — a live-path decision about real purchase orders — is covered by a test that
    /// fails when it changes.
    /// </summary>
    internal static async Task<DeliveryResult> UploadCoreAsync(
        IFtpsUploadSession session,
        byte[] content,
        string remotePath,
        bool makeDirectories,
        bool overwriteExisting,
        CancellationToken token)
    {
        await using var ms = new MemoryStream(content);
        var status = await session.UploadStream(
            ms,
            remotePath,
            RemoteExistsModeFor(overwriteExisting),
            createRemoteDir: makeDirectories,
            token: token).ConfigureAwait(false);

        return status switch
        {
            FtpStatus.Success => new DeliveryResult(true, null),
            // Skipped is only reachable when the operator turned overwrite off AND a file is already
            // there. FluentFTP treats that as a benign no-op; a purchase order that was not sent is
            // not benign, so it becomes an explicit failure with the reason spelled out.
            FtpStatus.Skipped => new DeliveryResult(false,
                SftpDeliveryDispatcher.RefusedBecauseFileExists(remotePath)),
            _ => new DeliveryResult(false, "FTPS upload did not complete successfully."),
        };
    }

    /// <summary>
    /// How the transfer treats a file already at the remote path. Overwrite (the default) keeps a
    /// crash-recovery re-drive able to repair its own partial upload; Skip refuses to touch what is
    /// there and <see cref="UploadCoreAsync"/> turns that refusal into a failed delivery.
    /// </summary>
    internal static FtpRemoteExists RemoteExistsModeFor(bool overwriteExisting) =>
        overwriteExisting ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip;

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
    /// The one FluentFTP call the upload makes. Exists so <see cref="UploadCoreAsync"/> — which owns
    /// the overwrite decision for real purchase orders — is testable without an FTPS server.
    /// </summary>
    internal interface IFtpsUploadSession
    {
        Task<FtpStatus> UploadStream(
            Stream input, string remotePath, FtpRemoteExists existsMode,
            bool createRemoteDir, CancellationToken token);
    }

    private sealed class FluentFtpUploadSession : IFtpsUploadSession
    {
        private readonly AsyncFtpClient _client;
        public FluentFtpUploadSession(AsyncFtpClient client) => _client = client;

        public Task<FtpStatus> UploadStream(
            Stream input, string remotePath, FtpRemoteExists existsMode,
            bool createRemoteDir, CancellationToken token) =>
            _client.UploadStream(input, remotePath, existsMode, createRemoteDir, progress: null, token: token);
    }
}
