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

    public Task<long?> TryGetSizeAsync(string key, CancellationToken ct)
    {
        // Best-effort by contract: any failure (bad key, missing file) → null, never throw.
        try
        {
            var fullPath = GetFullPath(key);
            return Task.FromResult(File.Exists(fullPath)
                ? (long?)new FileInfo(fullPath).Length
                : null);
        }
        catch
        {
            return Task.FromResult<long?>(null);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The local provider's "backend" is a directory, so the round trip is creating it and writing
    /// a byte. That is a genuine check of the only thing that can be wrong here (the temp root not
    /// being writable) rather than an inferred one, which keeps this provider honest for the same
    /// reason the R2 probe is.
    /// </remarks>
    public async Task<StorageProbe> ProbeAsync(CancellationToken ct)
    {
        var probeKey = $"__healthcheck__/probe-{Guid.NewGuid():N}";
        string? fullPath = null;
        try
        {
            fullPath = GetFullPath(probeKey);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, new byte[] { 0x1 }, ct);

            return StorageProbe.Reachable(
                "Local dev storage root is present and writable.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StorageProbe.Unreachable(
                $"Local dev storage root is not writable: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (fullPath is not null && File.Exists(fullPath)) File.Delete(fullPath);
            }
            catch
            {
                // Probe cleanup is best-effort; a leftover 1-byte temp file is not a health signal.
            }
        }
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

        // Reject path traversal OS-INDEPENDENTLY. The containment check below relies on
        // Path.GetFullPath, whose separator handling is platform-specific: on Linux '\'
        // is a literal filename char, so a key like "..\..\..\windows\win.ini" would NOT
        // be collapsed and would slip past the prefix test (it does on Windows). Normalise
        // BOTH separators to '/', then reject any "." or ".." segment up front — this is
        // identical on every platform and is the primary traversal guard.
        var normalizedKey = key.Replace('\\', '/');
        if (normalizedKey.Split('/').Any(seg => seg == ".."))
            throw new ArgumentException(
                $"Storage key must not contain path traversal: {key}", nameof(key));

        var fullPath = Path.GetFullPath(
            Path.Combine(BasePath, normalizedKey.Replace('/', Path.DirectorySeparatorChar)));

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
