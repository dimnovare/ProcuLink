using Renci.SshNet;
using ProcuLink.Infrastructure.Services.Security;

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

    public ISftpSession Connect(
        string host, int port, string username, string password, SshHostKeyVerifier verifier)
    {
        var client = new SftpClient(host, port, username, password);
        client.ConnectionInfo.Timeout = ConnectAndOperationTimeout;
        client.OperationTimeout = ConnectAndOperationTimeout;

        // BEFORE Connect, without exception. SSH.NET raises HostKeyReceived during key exchange and
        // trusts anything when nothing is subscribed, so a subscription added after this line
        // verifies nothing while looking exactly like one that does.
        verifier.Attach(client);

        try
        {
            client.Connect();
        }
        catch
        {
            client.Dispose();

            // Our sentence, not the library's. A refused host key surfaces from SSH.NET as
            // "Key exchange negotiation failed." — indistinguishable from an algorithm mismatch, and
            // useless to the operator who has to decide whether their supplier rebuilt a server or
            // someone is in the path. Checked before rethrowing so it wins over whatever type the
            // library chose, and a library upgrade that renames its exception cannot quietly
            // reinstate the useless message.
            verifier.ThrowIfRejected();
            throw;
        }

        // Belt and braces: a connection that SUCCEEDED despite our refusal must still not be used.
        // Today SSH.NET aborts on CanTrust=false — proven live on the pinned 2024.2.0 — but that is
        // a property of the library, and this is the one place where trusting it silently would hand
        // a supplier's credentials to an unverified server. Cheap to not depend on.
        if (verifier.Rejection is not null)
        {
            client.Dispose();
            throw verifier.Rejection;
        }

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
