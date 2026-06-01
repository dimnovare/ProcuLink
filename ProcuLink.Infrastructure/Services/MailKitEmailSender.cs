using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// SMTP-backed <see cref="IEmailSender"/>. Reads SMTP host/port/username/password
/// from the <c>Smtp</c> config section. Failures are logged but do not throw,
/// so request handlers (e.g. support contact form) do not 5xx on transient SMTP errors.
/// </summary>
public sealed class MailKitEmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    private readonly ILogger<MailKitEmailSender> _log;

    public MailKitEmailSender(IConfiguration config, ILogger<MailKitEmailSender> log)
    {
        _config = config;
        _log    = log;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var host     = _config["Smtp:Host"];
        var portStr  = _config["Smtp:Port"];
        var username = _config["Smtp:Username"];
        var password = _config["Smtp:Password"];
        var from     = _config["Smtp:From"] ?? "support@proculink.eu";

        if (string.IsNullOrWhiteSpace(host))
        {
            _log.LogWarning("MailKitEmailSender invoked but Smtp:Host is empty — dropping email to {To}", to);
            return;
        }

        var port = int.TryParse(portStr, out var p) ? p : 587;

        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body    = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTlsWhenAvailable, ct);
            if (!string.IsNullOrWhiteSpace(username))
                await client.AuthenticateAsync(username, password ?? string.Empty, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);

            _log.LogInformation("MailKitEmailSender sent email to {To} via {Host}:{Port}", to, host, port);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MailKitEmailSender failed to send email to {To}", to);
        }
    }
}
