using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// Computes the canonical column-layout hash used for schema fingerprinting.
///
/// The hash is deliberately:
/// <list type="bullet">
///   <item><b>order-independent</b> — headers are sorted, so the same columns in a different
///         order hash identically;</item>
///   <item><b>case-insensitive</b> — headers are lowercased;</item>
///   <item><b>whitespace-tolerant</b> — headers are trimmed and blank columns dropped.</item>
/// </list>
///
/// Both the parse-time upsert (<c>ParseOrderJob</c>) and the detect-time lookup
/// (<c>FormatDetectionController</c>) call this with headers extracted by the same
/// <see cref="IFormatDetector"/>, so a given physical layout always produces the same hash.
/// </summary>
public static class SchemaFingerprintHasher
{
    /// <summary>
    /// Returns the lowercase-hex SHA-256 of the normalised, sorted column headers, or
    /// <c>null</c> when there are no usable headers (e.g. a format without a header row).
    /// </summary>
    public static string? ComputeColumnNameHash(IEnumerable<string>? headers)
    {
        if (headers is null) return null;

        var normalised = headers
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToLowerInvariant())
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();

        if (normalised.Count == 0) return null;

        // Newline join (a character that cannot appear inside a single CSV header cell)
        // keeps "a","bc" distinct from "ab","c".
        var joined = string.Join('\n', normalised);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
