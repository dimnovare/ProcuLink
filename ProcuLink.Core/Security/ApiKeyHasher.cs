using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Core.Security;

/// <summary>
/// HMAC-SHA256 key hashing shared between ApiKeyAuthHandler (Api)
/// and ApiKeyService (Infrastructure).
///
/// <para>
/// The HMAC key is a <strong>server-side secret</strong> from configuration
/// (<c>Security:ApiKeyHashSecret</c>), <em>not</em> derived from the raw key
/// itself. This ensures an attacker with read-only database access cannot
/// recompute stored hashes offline — knowledge of the server secret is required.
/// </para>
///
/// <para>
/// <strong>Breaking change:</strong> any <c>plk_</c> API keys hashed with the
/// previous self-HMAC scheme (where the HMAC key was derived from the raw key
/// itself) will no longer verify. All existing API keys must be revoked and
/// regenerated after deploying this version.
/// </para>
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>
    /// HMAC-SHA256 of <paramref name="rawKey"/> keyed with
    /// <paramref name="serverSecret"/>. Returns a lowercase hex string.
    /// </summary>
    /// <param name="rawKey">The raw <c>plk_*</c> API key presented by the caller.</param>
    /// <param name="serverSecret">
    /// A server-side secret from <c>Security:ApiKeyHashSecret</c> configuration.
    /// Must not be empty in production. The hasher accepts any non-null string; callers
    /// are responsible for enforcing minimum length via startup validation.
    /// </param>
    public static string ComputeHash(string rawKey, string serverSecret)
    {
        ArgumentNullException.ThrowIfNull(rawKey);
        ArgumentNullException.ThrowIfNull(serverSecret);

        var key  = Encoding.UTF8.GetBytes(serverSecret);
        var data = Encoding.UTF8.GetBytes(rawKey);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }
}
