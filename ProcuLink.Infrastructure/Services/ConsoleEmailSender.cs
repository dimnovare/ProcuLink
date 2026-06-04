using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Fallback IEmailSender for dev/test/CI when no SMTP host is configured.
/// Logs the email at Information level rather than sending. Production deployments
/// must register <see cref="MailKitEmailSender"/> instead.
/// </summary>
public sealed class ConsoleEmailSender : IEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _log;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> log) => _log = log;

    /// <inheritdoc/>
    public bool CanDeliver => false;

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        _log.LogInformation(
            "ConsoleEmailSender (no SMTP configured) — would have sent: to={To} subject={Subject}{NewLine}{Body}",
            to, subject, Environment.NewLine, body);
        return Task.CompletedTask;
    }
}
