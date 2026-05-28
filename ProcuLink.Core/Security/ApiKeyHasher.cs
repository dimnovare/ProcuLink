using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Core.Security;

/// <summary>
/// HMAC-SHA256 key hashing shared between ApiKeyAuthHandler (Api)
/// and ApiKeyService (Infrastructure).
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>
    /// HMAC-SHA256 using the first 32 chars of the raw key as the secret (self-HMAC).
    /// Returns lowercase hex string.
    /// </summary>
    public static string ComputeHash(string rawKey)
    {
        var secret = Encoding.UTF8.GetBytes(rawKey[..Math.Min(32, rawKey.Length)]);
        var data   = Encoding.UTF8.GetBytes(rawKey);
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }
}
