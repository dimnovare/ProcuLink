using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Core.Security;

/// <summary>
/// Defensive SHA-256 helpers for artifact / delivery provenance (launch batch 3).
/// Provenance capture is best-effort observability metadata: these helpers NEVER throw —
/// any failure returns null so the parent transform/delivery operation is unaffected.
/// Digests are lowercase hex (64 chars).
/// </summary>
public static class ProvenanceHash
{
    /// <summary>SHA-256 of raw bytes as lowercase hex, or null on any failure (including null input).</summary>
    public static string? TrySha256Hex(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>SHA-256 of the UTF-8 encoding of <paramref name="text"/> as lowercase hex, or null on any failure.</summary>
    public static string? TrySha256HexUtf8(string? text)
    {
        if (text is null) return null;
        try
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
