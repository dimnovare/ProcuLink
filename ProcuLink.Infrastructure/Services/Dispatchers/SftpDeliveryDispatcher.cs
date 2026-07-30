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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Sftp;

    // The deterministic PER-ORDER filename is overwritten on re-send, so a crash-recovery re-drive
    // cannot leave the supplier holding two copies.
    //
    // WP-20 note: this stays Safe only because DeliveryService now names the file
    // '{PO}-{orderId}{ext}'. Under the old bare-PO name, "overwrite" could also replace a DIFFERENT
    // order that happened to share the PO number. Overwriting is now scoped to this order's own
    // previous upload — and an operator may still switch it off per supplier
    // (overwriteExisting=false), which makes a re-send fail loudly instead of replacing anything.
    public ResendSafety ResendSafety => ResendSafety.Safe;

    public SftpDeliveryDispatcher(ILogger<SftpDeliveryDispatcher> logger, OutboundRequestGuard guard)
    {
        _logger = logger;
        _guard = guard;
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
        // A3 idempotency: SFTP is already idempotent by construction. The remote filename is the
        // deterministic per-order filename DeliveryService builds, and UploadFile below overwrites
        // by default, so a crash-recovery re-upload OVERWRITES the same path rather than creating a
        // second file — no supplier idempotency key is needed (idempotencyKey is intentionally
        // unused here).
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

            var overwrite = ResolveOverwriteExisting(config.ConfigJson);

            // SSH.NET is synchronous — wrap in Task.Run so we honour the CancellationToken.
            return await Task.Run(
                () => UploadSync(content, remotePath, connectionInfo, cfg.MakeDirectories, overwrite, ct), ct);
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

    private DeliveryResult UploadSync(
        byte[] content,
        string remotePath,
        ConnectionInfo connectionInfo,
        bool makeDirectories,
        bool overwriteExisting,
        CancellationToken ct)
    {
        using var client = new SftpClient(connectionInfo);
        try
        {
            client.Connect();
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

        ct.ThrowIfCancellationRequested();

        if (makeDirectories)
        {
            EnsureRemoteDirectoryExists(client, GetDirectoryPath(remotePath));
        }

        // Overwrite opt-out: when the supplier's drop directory must be append-only, a pre-existing
        // file at this path is a hard stop with a precise message, not a silent replacement.
        // (SSH.NET's canOverride:false surfaces only as a generic SshException, hence the pre-check.)
        if (!overwriteExisting)
        {
            try
            {
                if (client.Exists(remotePath))
                {
                    try { client.Disconnect(); } catch { /* swallow */ }
                    return new DeliveryResult(false,
                        $"SFTP upload skipped: '{remotePath}' already exists and overwriteExisting is false for this supplier.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SFTP existence pre-check failed; continuing without overwrite.");
            }
        }

        using var ms = new MemoryStream(content);
        try
        {
            client.UploadFile(ms, remotePath, canOverride: overwriteExisting);
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
            _logger.LogWarning(ex, "SFTP upload failed after connection.");
            return new DeliveryResult(false, "SFTP upload failed after a successful connection.");
        }
        finally
        {
            try { client.Disconnect(); } catch { /* swallow */ }
        }

        return new DeliveryResult(true, null);
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

    private static void EnsureRemoteDirectoryExists(SftpClient client, string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || dirPath == "/" || dirPath == ".") return;
        if (client.Exists(dirPath)) return;

        var parent = GetDirectoryPath(dirPath);
        if (!string.IsNullOrEmpty(parent) && parent != "/" && parent != dirPath)
            EnsureRemoteDirectoryExists(client, parent);

        try { client.CreateDirectory(dirPath); }
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

    /// <summary>
    /// Whether an upload may replace a file already sitting at the remote path.
    ///
    /// <para>
    /// Defaults to TRUE, which preserves the A3 crash-recovery contract: the remote filename is
    /// deterministic PER ORDER, so overwriting can only ever replace THIS order's own earlier
    /// upload, and a re-drive after an unknown outcome heals instead of failing. (Before WP-20 the
    /// filename was the bare PO number, so the same default could silently clobber a DIFFERENT
    /// order sharing that PO number — that is fixed by the filename, not by this flag.)
    /// </para>
    ///
    /// <para>
    /// Set <c>"overwriteExisting": false</c> in the supplier's delivery config JSON for a drop
    /// directory that must be append-only. Trade-off: a crash-recovery re-drive then FAILS with an
    /// explicit "already exists" message instead of re-uploading, and needs an operator decision.
    /// </para>
    /// </summary>
    internal static bool ResolveOverwriteExisting(string? configJson) =>
        DeliveryConfigFlags.ReadBool(configJson, DeliveryConfigFlags.OverwriteExisting, fallback: true);

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

    private sealed record SftpCredentials(
        string Username,
        string? Password,
        string? PrivateKey,
        string? PrivateKeyPassphrase);
}

