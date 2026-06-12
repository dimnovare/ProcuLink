namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Single-method FTP/FTPS fetch seam for the catalog pull channel (scope-review decision:
/// the catalog path needs exactly one capability — download one file, bounded). Kept
/// separate from <c>FtpsDeliveryDispatcher</c> so the hardened fetch configuration
/// (PASVEX, mandatory data-channel encryption, no invalid-cert escape hatch) cannot
/// regress the live delivery behaviour and vice versa.
/// </summary>
public interface IFtpFetchClientFactory
{
    /// <summary>
    /// Creates a session for the given endpoint. The TCP/TLS connect itself is deferred to
    /// <see cref="IFtpFetchSession.DownloadAsync"/> (the underlying client connects
    /// asynchronously under the caller's deadline token). Callers MUST run the SSRF guard
    /// against (host, port) before invoking this.
    /// </summary>
    IFtpFetchSession Connect(string host, int port, string username, string password, bool explicitTls);
}

/// <summary>An FTP/FTPS fetch session bound to one endpoint. Dispose to close the connection.</summary>
public interface IFtpFetchSession : IDisposable
{
    /// <summary>
    /// Connects (if not yet connected) and downloads <paramref name="remotePath"/> through
    /// a bounded copy: at most <paramref name="maxBytes"/> + 1 bytes are read, and
    /// <see cref="ProcuLink.Core.Services.Catalog.CatalogFileTooLargeException"/> is thrown
    /// on overflow (H2). No SIZE pre-probe — the server could lie about it.
    /// </summary>
    Task<MemoryStream> DownloadAsync(string remotePath, long maxBytes, CancellationToken ct);
}
