using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Webhooks;

namespace ProcuLink.Infrastructure.Services.Webhooks;

/// <summary>
/// HMAC-SHA256 webhook verifier with replay protection (timestamp window + nonce cache).
///
/// Algorithm (matches outbound FireIntegrationTriggerJob convention):
///   signature = lower-hex(HMAC-SHA256(secret, $"{timestamp}.{nonce}.{body}"))
///
/// Security properties:
///   * Generic error on every failure path — no information leakage.
///   * Constant-time signature comparison via CryptographicOperations.FixedTimeEquals.
///   * ±300 second clock skew tolerance, 600 second nonce reuse window.
///   * Secret is decrypted per call (cheap; key is in-memory) and not retained.
/// </summary>
public sealed class HmacWebhookVerifier : IHmacWebhookVerifier
{
    private const string GenericError = "Signature verification failed";
    private const int    ClockSkewSeconds = 300;
    private const int    NonceCacheSeconds = 600;

    private readonly ProcuLinkDbContext              _db;
    private readonly DeliveryEncryptionService       _crypto;
    private readonly IMemoryCache                    _cache;
    private readonly ILogger<HmacWebhookVerifier>    _logger;

    public HmacWebhookVerifier(
        ProcuLinkDbContext              db,
        DeliveryEncryptionService       crypto,
        IMemoryCache                    cache,
        ILogger<HmacWebhookVerifier>    logger)
    {
        _db     = db;
        _crypto = crypto;
        _cache  = cache;
        _logger = logger;
    }

    public async Task<HmacVerificationResult> VerifyAsync(
        string slug,
        string timestampHeader,
        string nonceHeader,
        string signatureHeader,
        string rawBody,
        CancellationToken ct)
    {
        // 1. All headers must be present and non-empty
        if (string.IsNullOrWhiteSpace(slug)
            || string.IsNullOrWhiteSpace(timestampHeader)
            || string.IsNullOrWhiteSpace(nonceHeader)
            || string.IsNullOrWhiteSpace(signatureHeader))
        {
            _logger.LogWarning("HMAC verify: missing slug/timestamp/nonce/signature header.");
            return Fail();
        }

        // 2. Parse + validate timestamp window (±300s)
        if (!DateTimeOffset.TryParse(
                timestampHeader,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var ts))
        {
            _logger.LogWarning("HMAC verify: timestamp not ISO-8601 parseable.");
            return Fail();
        }

        var now    = DateTimeOffset.UtcNow;
        var skew   = (now - ts).Duration();
        if (skew > TimeSpan.FromSeconds(ClockSkewSeconds))
        {
            _logger.LogWarning(
                "HMAC verify: timestamp outside ±{Skew}s window (drift={Drift}s).",
                ClockSkewSeconds, (int)skew.TotalSeconds);
            return Fail();
        }

        // 3. Look up organisation by slug
        var org = await _db.Organisations
                           .Where(o => o.Slug == slug)
                           .Select(o => new { o.Id, o.WebhookSecretEncrypted })
                           .FirstOrDefaultAsync(ct);
        if (org is null)
        {
            _logger.LogWarning("HMAC verify: unknown slug.");
            return Fail();
        }

        // 4. Secret must be configured
        if (string.IsNullOrEmpty(org.WebhookSecretEncrypted))
        {
            _logger.LogWarning("HMAC verify: org {OrgId} has no webhook secret configured.", org.Id);
            return Fail();
        }

        // 5. Decrypt
        var secret = _crypto.Decrypt(org.WebhookSecretEncrypted);
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogWarning("HMAC verify: failed to decrypt webhook secret for org {OrgId}.", org.Id);
            return Fail();
        }

        // 6. Compute expected signature and compare (constant time)
        var expectedHex = ComputeHmacHex(secret, timestampHeader, nonceHeader, rawBody);
        if (!TryHexToBytes(expectedHex, out var expectedBytes)
            || !TryHexToBytes(signatureHeader, out var providedBytes))
        {
            _logger.LogWarning("HMAC verify: signature header is not hex.");
            return Fail();
        }
        if (expectedBytes.Length != providedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            _logger.LogWarning("HMAC verify: signature mismatch.");
            return Fail();
        }

        // 7. Replay-protection nonce check (after signature passes to avoid polluting cache on bad inputs)
        var nonceKey = $"webhook_nonce:{org.Id}:{nonceHeader}";
        if (_cache.TryGetValue(nonceKey, out _))
        {
            _logger.LogWarning("HMAC verify: replayed nonce for org {OrgId}.", org.Id);
            return Fail();
        }
        _cache.Set(nonceKey, true, TimeSpan.FromSeconds(NonceCacheSeconds));

        return new HmacVerificationResult(Valid: true, ErrorMessage: null, OrganisationId: org.Id);
    }

    private static string ComputeHmacHex(string secret, string timestamp, string nonce, string body)
    {
        var keyBytes  = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes($"{timestamp}.{nonce}.{body}");
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(dataBytes)).ToLowerInvariant();
    }

    private static bool TryHexToBytes(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return false;
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HmacVerificationResult Fail()
        => new(Valid: false, ErrorMessage: GenericError, OrganisationId: null);
}
