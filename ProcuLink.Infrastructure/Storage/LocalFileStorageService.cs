using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Storage;

/// <summary>
/// Dev-only file storage that writes blobs to the local temp directory.
/// Activated when <c>Storage:R2AccessKeyId</c> is absent or empty.
/// Key convention mirrors R2: {orgId}/{orderId}/... and {orgId}/{orderId}/artifacts/{artifactId}.{ext}
/// </summary>
public sealed class LocalFileStorageService : IFileStorageService
{
    private static readonly string BasePath =
        Path.Combine(Path.GetTempPath(), "proculink-dev");

    // ── IFileStorageService ────────────────────────────────────────────────

    public async Task<string> UploadAsync(
        Stream content, string key, string contentType, CancellationToken ct)
    {
        var fullPath = GetFullPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var fs = File.Create(fullPath);
        await content.CopyToAsync(fs, ct);

        return key;
    }

    public Task<string> GetSignedDownloadUrlAsync(
        string key, TimeSpan expiry, CancellationToken ct)
    {
        // Return a URL to the dev-only passthrough endpoint; no actual signing needed.
        var url = $"http://localhost:5096/api/dev/files/{key}";
        return Task.FromResult(url);
    }

    public Task<Stream> DownloadAsync(string key, CancellationToken ct)
    {
        var fullPath = GetFullPath(key);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found in local storage: {key}");

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var fullPath = GetFullPath(key);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }

    // ── Internal helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Converts a storage key (forward-slash delimited) to an absolute local path.
    /// Public so <see cref="DevFilesController"/> can resolve the same path.
    /// </summary>
    public static string GetFullPath(string key) =>
        Path.GetFullPath(
            Path.Combine(BasePath, key.Replace('/', Path.DirectorySeparatorChar)));
}
