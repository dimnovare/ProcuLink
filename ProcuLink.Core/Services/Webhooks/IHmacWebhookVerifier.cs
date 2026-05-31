namespace ProcuLink.Core.Services.Webhooks;

/// <summary>
/// Result of verifying an inbound HMAC-signed webhook request.
/// On any failure, ErrorMessage is the same generic string and OrganisationId is null —
/// the caller MUST NOT differentiate failure reasons in the response.
/// </summary>
public sealed record HmacVerificationResult(bool Valid, string? ErrorMessage, Guid? OrganisationId);

/// <summary>
/// Verifies HMAC-SHA256 signed webhook callbacks from supplier ERPs / EDI gateways.
///
/// Signature is computed over the concatenation: $"{timestamp}.{nonce}.{body}"
/// using the org's stored AES-GCM-encrypted webhook secret as the HMAC key.
///
/// Replay protection:
///   - Timestamp must be within +/- 300 seconds of UtcNow.
///   - Nonce must not have been seen within the last 600 seconds (via IDistributedCache).
///
/// Error policy:
///   - ANY failure (bad timestamp, unknown slug, missing secret, signature mismatch,
///     replayed nonce) returns the same generic "Signature verification failed"
///     message and null OrganisationId. No information leakage.
/// </summary>
public interface IHmacWebhookVerifier
{
    Task<HmacVerificationResult> VerifyAsync(
        string slug,
        string timestampHeader,
        string nonceHeader,
        string signatureHeader,
        string rawBody,
        CancellationToken ct);
}
