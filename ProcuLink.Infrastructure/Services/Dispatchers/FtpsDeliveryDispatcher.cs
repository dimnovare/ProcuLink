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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Ftps;

    public FtpsDeliveryDispatcher(ILogger<FtpsDeliveryDispatcher> logger, OutboundRequestGuard guard)
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
        // A3 idempotency: FTPS is already idempotent by construction. The remote filename is the
        // deterministic sanitised PO filename and UploadStream below uses FtpRemoteExists.Overwrite,
        // so a crash-recovery re-upload OVERWRITES the same path rather than creating a second file —
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

                await client.Connect(token).ConfigureAwait(false);

                await using var ms = new MemoryStream(content);
                var status = await client.UploadStream(
                    ms,
                    remotePath,
                    FtpRemoteExists.Overwrite,
                    createRemoteDir: makeDirectories,
                    progress: null,
                    token: token).ConfigureAwait(false);

                return status == FtpStatus.Success
                    ? new DeliveryResult(true, null)
                    : new DeliveryResult(false, "FTPS upload did not complete successfully.");
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
}
