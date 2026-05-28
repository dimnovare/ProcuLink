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
    ISftpSession Connect(string host, int port, string username, string password);
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
}
