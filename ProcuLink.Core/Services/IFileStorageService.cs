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

    /// <summary>
    /// Does a REAL round trip to the storage backend and reports what came back.
    ///
    /// <para>Exists because the readiness check used to report "Storage reachable." on the strength
    /// of <see cref="GetSignedDownloadUrlAsync"/> returning a non-empty string. Pre-signing is
    /// local — <c>AmazonS3Client.GetPreSignedURL</c> is synchronous and makes no network call — so
    /// a wrong <c>ServiceURL</c> signs perfectly and reports reachable while every upload 403s.
    /// This project has already been bitten by exactly that (an S3↔R2 <c>serviceUrl</c> mismatch),
    /// which is why reachability is now observed rather than inferred.</para>
    ///
    /// <para>Implementations MUST NOT throw: a probe that blows up is a probe result
    /// (<see cref="StorageProbeStatus.Unreachable"/>), not an exception for the caller to handle.
    /// The default implementation reports <see cref="StorageProbeStatus.NotProbed"/> so the ~40
    /// existing test doubles keep compiling and are not misreported as healthy.</para>
    /// </summary>
    Task<StorageProbe> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(StorageProbe.NotProbed(
            "This storage provider does not implement a reachability probe."));
}

/// <summary>What a <see cref="IFileStorageService.ProbeAsync"/> round trip established.</summary>
public enum StorageProbeStatus
{
    /// <summary>
    /// The backend answered. Credentials were accepted and the bucket resolved — an object-missing
    /// answer counts, because being told "no such key" is proof something on the far end read the
    /// request.
    /// </summary>
    Reachable,

    /// <summary>
    /// The round trip did not produce an answer that proves the backend is usable: DNS/connect
    /// failure, timeout, rejected credentials, or a missing bucket. Reported as Degraded, never as
    /// reachable.
    /// </summary>
    Unreachable,

    /// <summary>
    /// No round trip was attempted — storage is not configured (local dev without R2 keys), or the
    /// provider has no probe. Distinct from <see cref="Reachable"/> precisely so it cannot be
    /// rendered as one.
    /// </summary>
    NotProbed,
}

/// <summary>Outcome of a storage reachability probe, with a detail string safe to show an operator.</summary>
/// <param name="Status">What the round trip established.</param>
/// <param name="Detail">
/// Human-readable specifics. MUST NOT contain credentials, signed URLs or bucket secrets — it is
/// served on the anonymous <c>/health/ready</c> endpoint.
/// </param>
public sealed record StorageProbe(StorageProbeStatus Status, string Detail)
{
    public static StorageProbe Reachable(string detail)   => new(StorageProbeStatus.Reachable, detail);
    public static StorageProbe Unreachable(string detail) => new(StorageProbeStatus.Unreachable, detail);
    public static StorageProbe NotProbed(string detail)   => new(StorageProbeStatus.NotProbed, detail);
}
