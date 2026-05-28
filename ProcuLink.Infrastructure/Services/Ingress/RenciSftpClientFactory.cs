using Renci.SshNet;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production <see cref="ISftpClientFactory"/> backed by <c>Renci.SshNet</c>.
/// </summary>
public sealed class RenciSftpClientFactory : ISftpClientFactory
{
    public ISftpSession Connect(string host, int port, string username, string password)
    {
        var client = new SftpClient(host, port, username, password);
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

        public void Dispose()
        {
            if (_client.IsConnected)
                _client.Disconnect();
            _client.Dispose();
        }
    }
}
