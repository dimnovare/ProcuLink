using Renci.SshNet;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production <see cref="ISftpClientFactory"/> backed by <c>Renci.SshNet</c>.
/// </summary>
public sealed class RenciSftpClientFactory : ISftpClientFactory
{
    /// <summary>
    /// H3 — connection and per-operation timeout. Without these SSH.NET waits on the socket
    /// indefinitely, so a stalling tenant-configured server could pin Worker threads.
    /// Applies to BOTH the order poller and the catalog pull (strict improvement for both).
    /// </summary>
    private static readonly TimeSpan ConnectAndOperationTimeout = TimeSpan.FromSeconds(30);

    public ISftpSession Connect(string host, int port, string username, string password)
    {
        var client = new SftpClient(host, port, username, password);
        client.ConnectionInfo.Timeout = ConnectAndOperationTimeout;
        client.OperationTimeout = ConnectAndOperationTimeout;
        client.Connect();
        return new RenciSftpSession(client);
    }

    // ── inner adapter ────────────────────────────────────────────────────────

    private sealed class RenciSftpSession : ISftpSession
    {
        private readonly SftpClient _client;

        public RenciSftpSession(SftpClient client)
        {
            _client = client;
        }

        public IEnumerable<string> ListFileNames(string remoteDirectory)
        {
            return _client
                .ListDirectory(remoteDirectory)
                .Where(f => f.IsRegularFile)
                .Select(f => f.FullName);
        }

        public MemoryStream DownloadFile(string remotePath)
        {
            var ms = new MemoryStream();
            _client.DownloadFile(remotePath, ms);
            ms.Position = 0;
            return ms;
        }

        public Stream OpenRead(string remotePath) => _client.OpenRead(remotePath);

        public void Dispose()
        {
            if (_client.IsConnected)
                _client.Disconnect();
            _client.Dispose();
        }
    }
}
