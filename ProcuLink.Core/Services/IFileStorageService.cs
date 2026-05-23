namespace ProcuLink.Core.Services;

public interface IFileStorageService
{
    /// <summary>Uploads a stream to R2 under the given key and returns the key.</summary>
    Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct);

    /// <summary>Returns a pre-signed download URL valid for the specified duration.</summary>
    Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct);

    /// <summary>Deletes the object at the given key.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}
