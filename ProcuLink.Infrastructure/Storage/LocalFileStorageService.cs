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
    /// Rejects path-traversal keys (e.g. <c>../../../windows/win.ini</c>) that
    /// would resolve outside <see cref="BasePath"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="key"/> is empty or escapes the storage root.
    /// </exception>
    public static string GetFullPath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Storage key must not be empty.", nameof(key));

        var fullPath = Path.GetFullPath(
            Path.Combine(BasePath, key.Replace('/', Path.DirectorySeparatorChar)));

        // Containment check: the resolved path must stay under BasePath.
        // Compare against BasePath with a trailing separator so that a sibling
        // directory whose name starts with BasePath (e.g. "proculink-dev-evil")
        // cannot satisfy the prefix test.
        //
        // Use Ordinal (case-SENSITIVE) so the check matches the real filesystem
        // semantics on the Linux (Debian, case-sensitive FS) deploy target. With
        // OrdinalIgnoreCase a case-variant sibling (e.g. ".../PROCULINK-DEV/...")
        // would pass the prefix test while actually resolving to a different
        // directory outside BasePath on a case-sensitive FS.
        var basePrefix = BasePath.TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(basePrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Storage key resolves outside the storage root: {key}", nameof(key));

        return fullPath;
    }
}
