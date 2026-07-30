using System.Text.Json;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// SFTP delivery dispatcher — uploads the artifact to the configured remote
/// directory via SSH/SFTP using SSH.NET. Supports password and private-key auth
/// (with optional key passphrase). Mirrors the HttpDeliveryDispatcher contract:
/// never throws, always returns a DeliveryResult with a humanised error message.
/// </summary>
public sealed class SftpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly ILogger<SftpDeliveryDispatcher> _logger;
    private readonly OutboundRequestGuard _guard;

    /// <summary>
    /// Test seam — how a connected upload session is obtained. Null in production (the only PUBLIC
    /// constructor leaves it null, and Microsoft DI only ever sees public constructors), where the
    /// session comes from a real SSH.NET connect.
    ///
    /// <para>
    /// It exists because the step that carries the operator's <c>overwriteExisting</c> setting OUT
    /// OF THE SAVED CONFIG and onto the upload is a single expression inside
    /// <see cref="DispatchAsync"/>. Without a seam that expression cannot be reached by any test —
    /// <see cref="OverwriteExistingFromConfig"/> is provable as a pure function and
    /// <see cref="UploadCore"/> is provable with an injected bool, and BOTH can be green while the
    /// wire between them is cut. Replacing that expression with a hardcoded <c>true</c> — the
    /// operator's OFF setting silently ignored on the live path for real purchase orders — left the
    /// entire suite passing. It does not any more.
    /// </para>
    /// <para>
    /// The seam substitutes the SESSION only, never the upload. There is exactly ONE
    /// <see cref="UploadCore"/> call in this class, and the connect step it skips
    /// (<see cref="TryConnect"/>) carries no part of the overwrite decision — so a test taking this
    /// branch exercises the same expression production does. An earlier shape had a second,
    /// production-only forward of the flag, which no test could reach and which therefore survived
    /// the same one-token mutation.
    /// </para>
    /// </summary>
    private readonly Func<ConnectionInfo, ISftpUploadSession>? _sessionFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Sftp;

    // Safe in the sense ResendSafety means, and ONLY that sense: a re-send after an unknown outcome
    // cannot DUPLICATE at the supplier. The remote path is a deterministic function of the order, so
    // a re-send either replaces its own file (overwriteExisting on, the default) or refuses outright
    // (overwriteExisting off) — neither produces a second copy.
    //
    // It does NOT mean the re-send completes. With overwriteExisting off it refuses, which would
    // walk the order into dead-letter while the supplier may already hold the document, so
    // DeliveryService parks that combination for a human rather than re-driving it
    // (CannotRepairItsOwnFile). See OverwriteExistingFromConfig for the trade-off the off setting makes.
    public ResendSafety ResendSafety => ResendSafety.Safe;

    // No HTTP status codes exist on this channel at all — every DeliveryResult it returns carries a
    // null ResponseCode, so the classification never reaches its 400 branch and there is no supplier
    // reason to capture. Declared explicitly rather than inherited: the whole point of the capability
    // is that a dispatcher states what it can see, and "nothing, because there is nothing to see" is
    // an answer, not an omission.
    public bool CapturesSupplierResponseBody => false;

    public SftpDeliveryDispatcher(ILogger<SftpDeliveryDispatcher> logger, OutboundRequestGuard guard)
        : this(logger, guard, sessionFactory: null)
    {
    }

    internal SftpDeliveryDispatcher(
        ILogger<SftpDeliveryDispatcher> logger,
        OutboundRequestGuard guard,
        Func<ConnectionInfo, ISftpUploadSession>? sessionFactory)
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
        // A3 idempotency: SFTP is already idempotent by construction. The remote filename is a
        // deterministic function of the ORDER (PO number + order id — see DeliveryService.BuildFileName),
        // so a crash-recovery re-upload targets the same path rather than creating a second file —
        // no supplier idempotency key is needed (idempotencyKey is intentionally unused here).
        try
        {
            var cfg = JsonSerializer.Deserialize<SftpConfig>(config.ConfigJson, JsonOpts);
            if (cfg is null || string.IsNullOrWhiteSpace(cfg.Host))
                return new DeliveryResult(false, "SFTP delivery configuration is invalid — host is required.");

            var creds = string.IsNullOrEmpty(decryptedCredentials)
                ? null
                : JsonSerializer.Deserialize<SftpCredentials>(decryptedCredentials, JsonOpts);

            if (creds is null || string.IsNullOrWhiteSpace(creds.Username))
                return new DeliveryResult(false, "SFTP delivery credentials are missing — username is required.");

            var port = cfg.Port > 0 ? cfg.Port : 22;
            var remoteDir = NormaliseRemoteDir(cfg.RemotePath);
            var remotePath = $"{remoteDir.TrimEnd('/')}/{SanitiseFileName(fileName)}";

            var connectionInfo = BuildConnectionInfo(cfg.Host, port, creds);
            var timeoutSeconds = cfg.TimeoutSeconds is > 0 ? cfg.TimeoutSeconds!.Value : 30;
            connectionInfo.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            // ── SSRF guard — re-validated IMMEDIATELY before connecting to shrink the
            // DNS-rebinding TOCTOU window. SSH.NET reconnects by hostname (re-resolving) and
            // pinning the IP would break host-key/hostname semantics, so the tightest available
            // mitigation is to re-resolve+validate right before dispatch.
            var guardResult = await _guard.ValidateHostAsync(cfg.Host, port, ct);
            if (!guardResult.Allowed)
                return new DeliveryResult(false, $"SFTP delivery blocked: {guardResult.Reason}");

            var fakeSession = _sessionFactory?.Invoke(connectionInfo);

            // SSH.NET is synchronous — wrap in Task.Run so we honour the CancellationToken.
            return await Task.Run(
                () =>
                {
                    // Where the session comes from is the ONLY thing that differs between production
                    // and a test: production connects, a test hands one over. Everything after that
                    // — including the operator's overwriteExisting setting — travels through the
                    // SINGLE UploadCore call below. Deliberately not two calls: while there were
                    // two, the production one was reachable by no test, so hardcoding ITS
                    // overwriteExisting argument to true left the whole suite green while an
                    // operator's OFF setting became a no-op for real purchase orders. Mirrors
                    // FtpsDeliveryDispatcher, which has always had one call site.
                    SftpClient? client = null;
                    ISftpUploadSession session;

                    if (fakeSession is null)
                    {
                        client = new SftpClient(connectionInfo);
                        var connectFailure = TryConnect(client);
                        if (connectFailure is not null)
                        {
                            client.Dispose();
                            return connectFailure;
                        }

                        session = new SshNetUploadSession(client);
                    }
                    else
                    {
                        session = fakeSession;
                    }

                    try
                    {
                        return UploadCore(
                            session, content, remotePath, cfg.MakeDirectories,
                            // THE wire between the operator's saved setting and the live upload,
                            // read from the config row the operator actually edited. Covered
                            // end-to-end by FileDropOverwriteWiringTests — hardcoding this to true
                            // must not be able to pass, and now cannot.
                            OverwriteExistingFromConfig(config.ConfigJson),
                            _logger, ct);
                    }
                    finally
                    {
                        if (client is not null)
                        {
                            try { client.Disconnect(); } catch { /* swallow */ }
                            client.Dispose();
                        }
                    }
                },
                ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryResult(false, "SFTP delivery timed out.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SFTP delivery config or credentials JSON malformed.");
            return new DeliveryResult(false, "SFTP delivery configuration could not be parsed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SFTP delivery failed unexpectedly.");
            return new DeliveryResult(false, "SFTP delivery failed before the upload could complete.");
        }
    }

    /// <summary>
    /// Opens the SSH connection, mapping the three connect failures an operator can actually fix to
    /// their own sentence. Returns null on success — transport only, so it carries no part of the
    /// upload decision.
    /// </summary>
    private static DeliveryResult? TryConnect(SftpClient client)
    {
        try
        {
            client.Connect();
            return null;
        }
        catch (SshAuthenticationException)
        {
            return new DeliveryResult(false, "SFTP authentication failed — check the username, password, or private key.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new DeliveryResult(false, $"SFTP connection failed: {ex.Message}");
        }
        catch (SshOperationTimeoutException)
        {
            return new DeliveryResult(false, "SFTP connection timed out.");
        }
    }

    /// <summary>
    /// Everything that happens on a connected SFTP session: the overwrite decision, directory
    /// creation, the upload, and the error mapping. Split out from the transport so it can be
    /// exercised against a fake session — the overwrite behaviour is a live-path decision about
    /// real purchase orders and must be covered by a test that fails when it changes.
    /// </summary>
    internal static DeliveryResult UploadCore(
        ISftpUploadSession session,
        byte[] content,
        string remotePath,
        bool makeDirectories,
        bool overwriteExisting,
        ILogger logger,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (makeDirectories)
        {
            EnsureRemoteDirectoryExists(session, GetDirectoryPath(remotePath));
        }

        // Explicit refusal rather than a silent replace, when the operator has turned overwrite off.
        // Reported as a normal failed delivery (retry/dead-letter as usual), never as a success.
        if (!overwriteExisting && session.Exists(remotePath))
        {
            return new DeliveryResult(false, RefusedBecauseFileExists(remotePath));
        }

        using var ms = new MemoryStream(content);
        try
        {
            session.UploadFile(ms, remotePath, canOverride: overwriteExisting);
        }
        catch (SftpPathNotFoundException)
        {
            return new DeliveryResult(false, $"SFTP remote directory '{GetDirectoryPath(remotePath)}' does not exist. Set makeDirectories=true to auto-create.");
        }
        catch (SftpPermissionDeniedException)
        {
            return new DeliveryResult(false, $"SFTP permission denied writing to '{remotePath}'.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SFTP upload failed after connection.");
            return new DeliveryResult(false, "SFTP upload failed after a successful connection.");
        }

        return new DeliveryResult(true, null);
    }

    /// <summary>
    /// What an operator reads when overwrite is off and something is already at the path. Shared by
    /// both file-drop dispatchers so the two wordings cannot drift.
    ///
    /// <para>
    /// Two situations, two sentences, because one sentence cannot be true of both. A PURCHASE ORDER
    /// writes to a path that is a deterministic function of the order, so whatever is sitting on it
    /// is that order's own document: the message must not say "remove or rename the file there" —
    /// renaming it and re-sending manufactures the second copy at the supplier that
    /// <see cref="ResendSafety.Safe"/> promises cannot happen, and deleting it throws away a
    /// purchase order the supplier may already be working from. A TEST FIRE
    /// (<see cref="DeliveryTestArtifact"/>) has no order behind it at all, so every clause about
    /// "this order" would be a fabrication — and there, deleting the file IS a legitimate remedy,
    /// because it is our own previous test and not the customer's document.
    /// </para>
    /// </summary>
    internal static string RefusedBecauseFileExists(string remotePath) =>
        DeliveryTestArtifact.IsAtPath(remotePath)
            ? RefusedTestFileExists(remotePath)
            : RefusedOrderFileExists(remotePath);

    private static string RefusedOrderFileExists(string remotePath) =>
        $"Nothing was sent. A file named '{remotePath}' is already on the supplier's server and this " +
        "connection is set not to replace existing files. That remote path belongs to this order and " +
        "no other, so the file there is almost certainly this same purchase order already delivered " +
        "— check with the supplier before sending it again. To let a repeat send replace its own " +
        "file, turn \"replace existing files\" back on for this supplier.";

    /// <summary>
    /// The same refusal, told truthfully about a connection test. No order exists, so nothing here
    /// may suggest one was or was not delivered: this is the previous test's own file, and the
    /// connection behaving exactly as configured.
    /// </summary>
    private static string RefusedTestFileExists(string remotePath) =>
        $"The connection worked, but nothing was written: a file named '{remotePath}' is already on " +
        "the supplier's server — left by an earlier test — and this connection is set not to replace " +
        "existing files. No purchase order is involved, and none was affected. Delete that test file " +
        "on the server to run the test again, or turn \"replace existing files\" back on for this " +
        "supplier.";

    /// <summary>
    /// Whether an upload may replace a file already at the remote path. Absent key ⇒ TRUE, which is
    /// exactly what every SFTP connection did before this setting existed, so no configured supplier
    /// changes behaviour on deploy.
    ///
    /// <para>
    /// True is also the safe default on the reliability axis, not merely the compatible one.
    /// Delivery here is at-least-once: a crash between the network write and the outcome commit
    /// leaves a re-drive that must be able to REPAIR its own possibly-truncated file. With overwrite
    /// off that re-drive refuses instead, and the supplier keeps a partial document until a human
    /// clears it. The clobber this packet fixes — two DIFFERENT orders sharing a PO number — is
    /// solved by the filename carrying the order id, not by this flag; the flag exists for operators
    /// whose remote directory must be strictly append-only and who accept that trade.
    /// </para>
    /// </summary>
    internal static bool OverwriteExistingFromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return true;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return !doc.RootElement.TryGetProperty("overwriteExisting", out var flag)
                || flag.ValueKind switch
                {
                    JsonValueKind.False => false,
                    JsonValueKind.True  => true,
                    // Anything else (null, a string, a number) is not an operator saying "no".
                    _ => true,
                };
        }
        catch (JsonException)
        {
            // Malformed config is reported by the caller's own parse; never silently flip to refuse.
            return true;
        }
    }

    private static ConnectionInfo BuildConnectionInfo(string host, int port, SftpCredentials creds)
    {
        // Prefer key-based auth if a private key is configured; fall back to password.
        if (!string.IsNullOrWhiteSpace(creds.PrivateKey))
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(creds.PrivateKey));
            var keyFile = string.IsNullOrEmpty(creds.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, creds.PrivateKeyPassphrase);
            return new ConnectionInfo(host, port, creds.Username, new PrivateKeyAuthenticationMethod(creds.Username, keyFile));
        }

        if (!string.IsNullOrWhiteSpace(creds.Password))
        {
            return new ConnectionInfo(host, port, creds.Username, new PasswordAuthenticationMethod(creds.Username, creds.Password));
        }

        // Shouldn't reach here because the calling code validates credentials, but guard anyway.
        throw new InvalidOperationException("SFTP credentials must include either a password or a private key.");
    }

    private static void EnsureRemoteDirectoryExists(ISftpUploadSession session, string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || dirPath == "/" || dirPath == ".") return;
        if (session.Exists(dirPath)) return;

        var parent = GetDirectoryPath(dirPath);
        if (!string.IsNullOrEmpty(parent) && parent != "/" && parent != dirPath)
            EnsureRemoteDirectoryExists(session, parent);

        try { session.CreateDirectory(dirPath); }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { /* race or parent missing */ }
        catch (Renci.SshNet.Common.SshException) { /* already exists or permission */ }
    }

    // internal static (not private) so the pure path/filename logic can be unit-tested
    // directly via InternalsVisibleTo without standing up a live SFTP server.
    internal static string NormaliseRemoteDir(string? remotePath)
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

    internal static string GetDirectoryPath(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : remotePath[..lastSlash];
    }

    // ── Config + credentials POCOs ────────────────────────────────────────────

    private sealed record SftpConfig(
        string Host,
        int Port,
        string? RemotePath,
        bool MakeDirectories,
        int? TimeoutSeconds);

    // ── Upload seam ───────────────────────────────────────────────────────────

    /// <summary>
    /// The three SSH.NET calls the upload actually makes. Exists so <see cref="UploadCore"/> — which
    /// owns the overwrite decision for real purchase orders — is testable without an SSH server.
    /// </summary>
    internal interface ISftpUploadSession
    {
        bool Exists(string path);
        void CreateDirectory(string path);
        void UploadFile(Stream input, string path, bool canOverride);
    }

    private sealed class SshNetUploadSession : ISftpUploadSession
    {
        private readonly SftpClient _client;
        public SshNetUploadSession(SftpClient client) => _client = client;

        public bool Exists(string path) => _client.Exists(path);
        public void CreateDirectory(string path) => _client.CreateDirectory(path);
        public void UploadFile(Stream input, string path, bool canOverride) =>
            _client.UploadFile(input, path, canOverride);
    }

    private sealed record SftpCredentials(
        string Username,
        string? Password,
        string? PrivateKey,
        string? PrivateKeyPassphrase);
}

