using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Infrastructure.Services;

/// <summary>The <c>X-ProcuLink-Signature</c> value: <c>sha256=</c> and the lowercase hex HMAC-SHA256 of the body.</summary>
public static class WebhookSignature
{
    public static string Header(byte[] body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"sha256={Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant()}";
    }
}
