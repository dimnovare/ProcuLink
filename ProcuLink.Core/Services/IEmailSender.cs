namespace ProcuLink.Core.Services;

/// <summary>
/// Minimal outbound email contract. Used by SupportContactService and any other
/// service that needs to send a one-shot notification email. Implementations
/// must be safe to call from anonymous request handlers — failure modes log,
/// they do not throw, unless the caller explicitly wants to know.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
