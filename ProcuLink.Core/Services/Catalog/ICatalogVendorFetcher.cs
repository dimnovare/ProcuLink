using System.Net.Http;
using System.Text.Json;

namespace ProcuLink.Core.Services.Catalog;

/// <summary>
/// A vendor-specific catalog fetcher (plan 2026-07-02 D4). Some distributor APIs use auth /
/// pagination too exotic for the generic <c>http</c> path + <c>HttpAuthApplier</c> (Logicom's
/// per-call 2FA AES signatures). Rather than a scripted-connector sandboxing liability, each such
/// vendor gets a dedicated fetcher, resolved by <see cref="Protocol"/>. A fetcher returns the SAME
/// <see cref="VendorFetchResult"/> byte shape the file/http channels produce, so the pull
/// pipeline's hash-skip, byte/row caps, parsing, honesty report, and error sanitisation all apply
/// unchanged. The fetcher itself MUST go through the SSRF-guarded <see cref="HttpClient"/> passed
/// in <see cref="VendorFetchContext"/> for every request.
/// </summary>
public interface ICatalogVendorFetcher
{
    /// <summary>The <c>SupplierCatalogSource.Protocol</c> value this fetcher handles (e.g. "logicom").</summary>
    string Protocol { get; }

    /// <summary>
    /// Fetches the whole catalog, returning it as one in-memory payload (typically a JSON array)
    /// plus a file name / content-type that drives the shared parser. Throws
    /// <see cref="CatalogSyncException"/> with an enumerated safe message on any failure.
    /// </summary>
    Task<VendorFetchResult> FetchAsync(VendorFetchContext ctx, CancellationToken ct);
}

/// <summary>
/// Inputs for a vendor fetch: the (optional, SSRF-pre-validated) source URL, the decrypted
/// vendor credential JSON (the <c>AuthConfigEncrypted</c> envelope), the SSRF-guarded HTTP client
/// to use for ALL requests, and the running byte cap the accumulated payload must not exceed.
/// </summary>
public sealed record VendorFetchContext(string? Url, JsonElement Creds, HttpClient Client, long MaxBytes);

/// <summary>The fetched catalog payload — same shape the file/http channels feed the parser.</summary>
public sealed record VendorFetchResult(MemoryStream Data, string? FileName, string? ContentType);
