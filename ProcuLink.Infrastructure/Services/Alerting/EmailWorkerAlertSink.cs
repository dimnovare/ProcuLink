using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services.Alerting;

/// <summary>
/// <see cref="IWorkerAlertSink"/> that emails the operator through the existing provider-neutral
/// <see cref="IEmailApiClient"/> (Postmark today). This is the destination WP-37 routes to: the
/// Worker already registers that client, so no new transport, package or credential is introduced —
/// only the recipient address is new (<c>Alerting:Email:To</c>).
/// <para>
/// DOUBLE no-op guard. Nothing is sent when no recipient is configured, and nothing is sent when the
/// email provider itself has no token. Either way this is silent and never throws: an alerting
/// component that can crash the Worker is worse than no alerting, and a throw here would abort the
/// sweep before the remaining conditions were evaluated.
/// </para>
/// <para>
/// But silent is not the same as delivered. Every no-op path — no recipient, no provider token, a
/// refusal from the provider, a transport throw — returns <c>false</c>, so the caller can tell the
/// difference between "emailed" and "did nothing without complaining". The
/// no-token case is the one this distinction was written for: <c>Alerting:Email:To</c> alone made
/// the configuration LOOK complete while every alert died here behind one unread warning.
/// </para>
/// </summary>
public sealed class EmailWorkerAlertSink : IWorkerAlertSink
{
    private readonly IEmailApiClient _client;
    private readonly AlertingEmailOptions _options;
    private readonly ILogger<EmailWorkerAlertSink> _logger;

    public EmailWorkerAlertSink(
        IEmailApiClient client,
        AlertingEmailOptions options,
        ILogger<EmailWorkerAlertSink> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct = default)
    {
        var recipients = _options.Recipients;
        if (recipients.Count == 0)
        {
            // Not an error: an unconfigured destination is the safe default deploy.
            _logger.LogDebug(
                "EmailWorkerAlertSink: no Alerting:Email:To configured — alert {AlertKey} not emailed.",
                alertKey);
            return false;
        }

        if (!_client.IsConfigured)
        {
            _logger.LogWarning(
                "EmailWorkerAlertSink: a recipient is configured but the email provider is not — "
              + "alert {AlertKey} could NOT be emailed.", alertKey);
            return false;
        }

        try
        {
            var result = await _client.SendAsync(new EmailApiMessage(
                From:     _client.DefaultFrom,
                To:       recipients,
                Subject:  BuildSubject(_options.EffectiveSubjectPrefix, alertKey),
                TextBody: BuildBody(alertKey, message)), ct);

            if (!result.Success)
            {
                _logger.LogError(
                    "EmailWorkerAlertSink: provider refused alert {AlertKey} — {Status} {Error}.",
                    alertKey, result.StatusCode, result.Error);
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            // IEmailApiClient's contract says it must not throw, but this sink is the last line of
            // defence: swallow anything so one bad transport cannot suppress the rest of the sweep.
            _logger.LogError(ex, "EmailWorkerAlertSink: failed to email alert {AlertKey}.", alertKey);
            return false;
        }
    }

    /// <summary>
    /// Subject line. The prefix makes alerts filterable and the key makes the condition visible
    /// without opening the mail — both are load-bearing for one person triaging on a phone.
    /// CR/LF is stripped defensively so a key can never inject a header.
    /// </summary>
    private static string BuildSubject(string prefix, string alertKey) =>
        $"{prefix} {alertKey}".Replace('\r', ' ').Replace('\n', ' ');

    private static string BuildBody(string alertKey, string message) =>
        $"""
         ProcuLink operational alert

         Condition: {alertKey}
         Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}

         {message}

         Triage: docs/deployment/monitoring-runbook.md
         """;
}
