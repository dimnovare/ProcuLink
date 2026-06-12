using FluentFTP;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production <see cref="IFtpFetchClientFactory"/> backed by FluentFTP 54.x, configured by
/// the pure <see cref="BuildFtpConfig"/> helper (unit-tested directly — the H1/M6/H3
/// security regression gate):
///
///  • <b>H1 (PASV SSRF):</b> <see cref="FtpDataConnectionType.PASVEX"/> — passive mode that
///    IGNORES the server-advertised PASV IP and reuses the already-SSRF-validated
///    control-connection host for the data connection. A malicious server can therefore
///    not bounce the data connection to a private/metadata address.
///  • <b>M6:</b> for FTPS, data-channel encryption is mandatory and certificate validation
///    is the OS default (<c>ValidateAnyCertificate = false</c>); there is deliberately NO
///    invalid-certificate escape hatch on this path.
///  • <b>H3:</b> 30 s connect/read timeouts on both control and data connections.
/// </summary>
public sealed class FluentFtpFetchClientFactory : IFtpFetchClientFactory
{
    internal const int TimeoutMs = 30_000;

    public IFtpFetchSession Connect(string host, int port, string username, string password, bool explicitTls)
    {
        var client = new AsyncFtpClient(host, username, password, port)
        {
            Config = BuildFtpConfig(explicitTls),
        };
        return new FluentFtpFetchSession(client);
    }

    /// <summary>Pure, unit-testable hardened configuration (see class doc).</summary>
    internal static FtpConfig BuildFtpConfig(bool explicitTls)
    {
        var config = new FtpConfig
        {
            // H1: passive, but pin the data connection to the validated control host.
            DataConnectionType = FtpDataConnectionType.PASVEX,

            // H3: bound every socket wait.
            ConnectTimeout               = TimeoutMs,
            ReadTimeout                  = TimeoutMs,
            DataConnectionConnectTimeout = TimeoutMs,
            DataConnectionReadTimeout    = TimeoutMs,

            // M6: secure-by-default certificate validation; no opt-out exists on this path.
            ValidateAnyCertificate = false,
        };

        if (explicitTls)
        {
            config.EncryptionMode = FtpEncryptionMode.Explicit; // AUTH TLS on the control port
            config.DataConnectionEncryption = true;             // M6: file content never travels cleartext
        }

        return config;
    }

    // ── inner adapter ────────────────────────────────────────────────────────

    private sealed class FluentFtpFetchSession : IFtpFetchSession
    {
        private readonly AsyncFtpClient _client;

        public FluentFtpFetchSession(AsyncFtpClient client)
        {
            _client = client;
        }

        public async Task<MemoryStream> DownloadAsync(string remotePath, long maxBytes, CancellationToken ct)
        {
            if (!_client.IsConnected)
                await _client.Connect(ct).ConfigureAwait(false);

            await using var remote = await _client
                .OpenRead(remotePath, FtpDataType.Binary, restart: 0, checkIfFileExists: false, token: ct)
                .ConfigureAwait(false);

            return await BoundedRead.CopyAsync(remote, maxBytes, ct).ConfigureAwait(false);
        }

        public void Dispose() => _client.Dispose();
    }
}
