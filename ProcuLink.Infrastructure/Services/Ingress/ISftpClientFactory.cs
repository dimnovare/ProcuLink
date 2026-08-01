using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Abstraction over SFTP connectivity, introduced to allow unit-testing
/// <see cref="SftpIngressService"/> without a live SSH connection.
/// The production implementation uses <c>Renci.SshNet.SftpClient</c>.
/// </summary>
public interface ISftpClientFactory
{
    /// <summary>
    /// Create and connect an SFTP client using the supplied credentials.
    /// Returns a connected <see cref="ISftpSession"/> ready for file operations.
    /// Throws on connection or authentication failure — callers should wrap in try/catch.
    /// </summary>
    /// <param name="verifier">
    /// Decides whether this server's identity is acceptable, and carries the answer back out.
    /// REQUIRED, not optional: SSH.NET trusts any host key when nothing subscribes to
    /// <c>HostKeyReceived</c>, so a connect path that could be called without one is a connect path
    /// that can silently talk to a different server than the one it was configured for — which is
    /// exactly what both callers of this interface did until this parameter existed
    /// (<c>docs/ops/2026-08-01-wp38-delivery-channel-proof.md</c> §1).
    ///
    /// <para>
    /// Implementations MUST attach it before connecting, and on failure MUST call
    /// <see cref="SshHostKeyVerifier.ThrowIfRejected"/> before surfacing the library's own exception
    /// — otherwise the operator reads <c>"Key exchange negotiation failed."</c> and learns nothing.
    /// After a successful connect, <see cref="SshHostKeyVerifier.LearnedFingerprint"/> tells the
    /// caller whether there is a first-use fingerprint to record.
    /// </para>
    /// </param>
    /// <exception cref="ProcuLink.Core.Services.Security.SshHostKeyRejectedException">
    /// The server presented a host key that is not the pinned one.
    /// </exception>
    ISftpSession Connect(
        string host, int port, string username, string password, SshHostKeyVerifier verifier);
}

/// <summary>
/// Represents an active SFTP session. Dispose to close the connection.
/// </summary>
public interface ISftpSession : IDisposable
{
    /// <summary>
    /// List the names of all plain files in <paramref name="remoteDirectory"/>.
    /// Does not recurse into sub-directories.
    /// </summary>
    IEnumerable<string> ListFileNames(string remoteDirectory);

    /// <summary>
    /// Download the file at <paramref name="remotePath"/> into a new <see cref="MemoryStream"/>
    /// (position reset to 0).
    /// </summary>
    MemoryStream DownloadFile(string remotePath);

    /// <summary>
    /// Open the file at <paramref name="remotePath"/> as a forward-only read stream.
    /// Additive seam (catalog pull, finding H2): lets callers stream through a bounded
    /// copy instead of materializing the whole file like <see cref="DownloadFile"/> does.
    /// Caller disposes the returned stream before disposing the session.
    /// </summary>
    Stream OpenRead(string remotePath);
}
