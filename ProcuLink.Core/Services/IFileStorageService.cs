namespace ProcuLink.Core.Services;

public interface IFileStorageService
{
    /// <summary>Uploads a stream to R2 under the given key and returns the key.</summary>
    Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct);

    /// <summary>Returns a pre-signed download URL valid for the specified duration.</summary>
    Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct);

    /// <summary>Downloads the object at the given key as a stream.</summary>
    Task<Stream> DownloadAsync(string key, CancellationToken ct);

    /// <summary>Deletes the object at the given key.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>
    /// Best-effort size (bytes) of the object at the given key, or null when unknown
    /// (missing object, transient storage error, or an implementation that does not
    /// support metadata). Used by the blob-retention sweep to estimate reclaimed bytes —
    /// MUST never throw. Default implementation returns null so existing test doubles
    /// keep compiling unchanged.
    /// </summary>
    Task<long?> TryGetSizeAsync(string key, CancellationToken ct) => Task.FromResult<long?>(null);
}
