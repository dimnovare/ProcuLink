namespace ProcuLink.Core.Services.Email;

/// <summary>
/// Provider-neutral HTTP email-API client. Sends mail over HTTPS (port 443) through a managed
/// provider (Postmark today; Mailgun-EU / Amazon SES later behind the same interface), so it works
/// on hosts that block outbound SMTP ports (e.g. Railway blocks 25/465/587). The provider relays
/// from its own deliverability-managed IPs (SPF/DKIM/DMARC).
///
/// <para>
/// Backs BOTH the transactional <see cref="IEmailSender"/> (support contact form, notifications)
/// via <c>PostmarkEmailSender</c>, and the supplier <c>email</c> delivery channel via
/// <c>EmailApiDeliveryDispatcher</c>. Implementations MUST NOT throw — return an
/// <see cref="EmailApiResult"/> with <c>Success=false</c> on any failure.
/// </para>
/// </summary>
public interface IEmailApiClient
{
    /// <summary>
    /// <c>true</c> when a provider token is configured and mail can actually be transmitted.
    /// <c>false</c> for the safe default deploy (no token) — callers should give honest feedback
    /// rather than implying the email was sent.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The default verified sender address (a provider-verified domain, e.g.
    /// <c>orders@proculink.eu</c>), used when a message does not specify its own From.
    /// </summary>
    string DefaultFrom { get; }

    /// <summary>Sends one email. Never throws — failures come back as <see cref="EmailApiResult"/>.</summary>
    Task<EmailApiResult> SendAsync(EmailApiMessage message, CancellationToken ct = default);
}

/// <summary>One outbound email. <paramref name="Attachments"/> may be null/empty.</summary>
public sealed record EmailApiMessage(
    string From,
    IReadOnlyList<string> To,
    string Subject,
    string TextBody,
    IReadOnlyList<EmailApiAttachment>? Attachments = null,
    string? ReplyTo = null,
    IReadOnlyList<EmailApiHeader>? Headers = null);

/// <summary>A single binary attachment (the generated artifact for delivery, etc.).</summary>
public sealed record EmailApiAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// A custom message header passed through to the provider (e.g. a deterministic <c>Message-ID</c>
/// for A3 delivery idempotency). Provider support is best-effort.
/// </summary>
public sealed record EmailApiHeader(string Name, string Value);

/// <summary>
/// Result of a single send attempt. <paramref name="StatusCode"/> is the provider's HTTP status
/// (null when the request never completed).
/// </summary>
/// <param name="ResponseBody">
/// The provider's own response payload, VERBATIM. The client already parses this to pull out
/// <c>Message</c>/<c>ErrorCode</c> for <paramref name="Error"/>; carrying the original too is what
/// lets <c>SupplierResponseClassification</c> tell a 400 that names a fault in the message from a
/// 400 that says nothing. Email is the canonical outbound path, so without it every provider
/// refusal on the live channel looked unexplained and was re-sent.
/// </param>
/// <param name="RetryAfter">
/// The wait the provider asked for (<c>Retry-After</c>) — Postmark sends it on a rate limit — or
/// null when they sent none.
/// </param>
public sealed record EmailApiResult(
    bool Success,
    string? Error,
    int? StatusCode = null,
    string? ResponseBody = null,
    TimeSpan? RetryAfter = null);
