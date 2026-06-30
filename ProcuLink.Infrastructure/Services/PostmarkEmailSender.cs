using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// HTTP-email-API-backed <see cref="IEmailSender"/> for transactional mail (support contact form,
/// notifications). Delegates to <see cref="IEmailApiClient"/> (Postmark) so it works on hosts that
/// block outbound SMTP — replacing <see cref="MailKitEmailSender"/> as the default cloud sender.
/// Like <c>MailKitEmailSender</c>, failures log inside the client rather than throwing, so request
/// handlers (e.g. the anonymous support form) never 5xx on a transient email error.
/// </summary>
public sealed class PostmarkEmailSender : IEmailSender
{
    private readonly IEmailApiClient _client;

    public PostmarkEmailSender(IEmailApiClient client) => _client = client;

    /// <inheritdoc/>
    public bool CanDeliver => _client.IsConfigured;

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        // Result intentionally not surfaced — the IEmailSender contract is fire-and-log, identical to
        // MailKitEmailSender. The client logs failures internally.
        await _client.SendAsync(new EmailApiMessage(
            From: _client.DefaultFrom,
            To: new[] { to },
            Subject: SanitizeHeaderValue(subject),
            TextBody: body), ct);
    }

    /// <summary>
    /// Strips CR/LF from a value destined for the Subject. The anonymous support contact form's
    /// Category + Subject are user input, so this is defence-in-depth against header injection —
    /// mirrors <see cref="MailKitEmailSender.SanitizeHeaderValue"/>. Internal for unit testing via
    /// InternalsVisibleTo.
    /// </summary>
    internal static string SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.IndexOfAny(['\r', '\n']) < 0
            ? value
            : value.Replace('\r', ' ').Replace('\n', ' ');
    }
}
