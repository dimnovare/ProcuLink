namespace ProcuLink.Core.Services;

/// <summary>
/// Minimal outbound email contract. Used by SupportContactService and any other
/// service that needs to send a one-shot notification email. Implementations
/// must be safe to call from anonymous request handlers — failure modes log,
/// they do not throw, unless the caller explicitly wants to know.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// <c>true</c> when this implementation can actually transmit email (SMTP configured).
    /// <c>false</c> for fallback/dev senders that only log — callers can use this to give
    /// honest feedback to users rather than implying the email was sent.
    /// </summary>
    bool CanDeliver { get; }

    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
