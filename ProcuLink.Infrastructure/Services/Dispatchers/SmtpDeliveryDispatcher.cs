using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// SMTP delivery dispatcher — sends the generated artifact as an email attachment
/// to the supplier using per-supplier host/port/credentials configured in
/// <see cref="SupplierDeliveryConfig.ConfigJson"/>.
///
/// A standalone <see cref="SmtpClient"/> is used instead of the existing
/// <c>IEmailSender</c> / <c>MailKitEmailSender</c> because that sender is wired
/// to a single global support-email account (From, credentials, host) and has no
/// facility for per-supplier relay hosts, per-supplier From addresses, or binary
/// attachments. Each SMTP delivery must use the supplier's own relay settings and
/// authentication, so we build and dispose a dedicated SmtpClient per dispatch.
/// </summary>
public sealed class SmtpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly ILogger<SmtpDeliveryDispatcher> _logger;
    private readonly OutboundRequestGuard _guard;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Smtp;

    public SmtpDeliveryDispatcher(ILogger<SmtpDeliveryDispatcher> logger, OutboundRequestGuard guard)
    {
        _logger = logger;
        _guard = guard;
    }

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct)
    {
        SmtpConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<SmtpConfig>(config.ConfigJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SMTP delivery config JSON malformed.");
            return new DeliveryResult(false, "SMTP delivery configuration could not be parsed.");
        }

        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Host))
            return new DeliveryResult(false, "SMTP delivery configuration is invalid — host is required.");

        if (string.IsNullOrWhiteSpace(cfg.FromAddress))
            return new DeliveryResult(false, "SMTP delivery configuration is invalid — fromAddress is required.");

        var recipients = ParseRecipients(cfg.ToAddresses);
        if (recipients.Count == 0)
            return new DeliveryResult(false, "SMTP delivery has no valid recipient addresses.");

        SmtpCredentials? creds;
        try
        {
            creds = string.IsNullOrEmpty(decryptedCredentials)
                ? null
                : JsonSerializer.Deserialize<SmtpCredentials>(decryptedCredentials, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SMTP delivery credentials JSON malformed.");
            return new DeliveryResult(false, "SMTP delivery configuration could not be parsed.");
        }

        if (creds is null || string.IsNullOrWhiteSpace(creds.Username))
            return new DeliveryResult(false, "SMTP delivery credentials are missing — username is required.");

        var port = cfg.Port > 0 ? cfg.Port : 587;
        var timeoutSeconds = cfg.TimeoutSeconds is > 0 ? cfg.TimeoutSeconds!.Value : 30;
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var attachmentName = string.IsNullOrWhiteSpace(cfg.AttachmentFileName) ? fileName : cfg.AttachmentFileName;

        var subject = BuildFromTemplate(
            string.IsNullOrWhiteSpace(cfg.SubjectTemplate)
                ? "Purchase Order " + fileNameWithoutExt
                : cfg.SubjectTemplate,
            fileNameWithoutExt,
            attachmentName);

        var body = BuildFromTemplate(
            string.IsNullOrWhiteSpace(cfg.BodyTemplate)
                ? $"Please find the attached purchase order ({attachmentName})."
                : cfg.BodyTemplate,
            fileNameWithoutExt,
            attachmentName);

        var secureOptions = cfg.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        // Build message
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(cfg.FromAddress));
        foreach (var addr in recipients)
            message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = subject;

        var builder = new BodyBuilder { TextBody = body };
        builder.Attachments.Add(attachmentName, content, ContentType.Parse(contentType));
        message.Body = builder.ToMessageBody();

        // ── SSRF guard ────────────────────────────────────────────────────────
        var guardResult = await _guard.ValidateHostAsync(cfg.Host, port, ct);
        if (!guardResult.Allowed)
            return new DeliveryResult(false, $"SMTP delivery blocked: {guardResult.Reason}");

        // Dispatch with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeoutCts.Token;

        try
        {
            using var client = new SmtpClient();
            client.Timeout = (int)TimeSpan.FromSeconds(timeoutSeconds).TotalMilliseconds;

            await client.ConnectAsync(cfg.Host, port, secureOptions, token);
            await client.AuthenticateAsync(creds.Username, creds.Password ?? "", token);
            await client.SendAsync(message, token);
            await client.DisconnectAsync(true, token);

            return new DeliveryResult(true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryResult(false, "SMTP delivery timed out.");
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return new DeliveryResult(false, "SMTP authentication failed — check the username and password.");
        }
        catch (SmtpCommandException ex) when (IsRecipientError(ex))
        {
            return new DeliveryResult(false, "SMTP delivery rejected — invalid recipient address.");
        }
        catch (SmtpCommandException ex)
        {
            _logger.LogWarning(ex, "SMTP command failure during delivery.");
            return new DeliveryResult(false, $"SMTP delivery failed — server returned an error: {ex.Message}");
        }
        catch (SmtpProtocolException ex)
        {
            return new DeliveryResult(false, $"SMTP host unreachable: {ex.Message}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new DeliveryResult(false, $"SMTP host unreachable: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SMTP delivery config or credentials JSON malformed.");
            return new DeliveryResult(false, "SMTP delivery configuration could not be parsed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMTP delivery failed unexpectedly.");
            return new DeliveryResult(false, "SMTP delivery failed before the message could be sent.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <c>toAddresses</c> which may be a JSON array of strings, a JSON
    /// string containing comma-separated addresses, or <c>null</c>.
    /// </summary>
    private static List<string> ParseRecipients(JsonElement toAddresses)
    {
        var result = new List<string>();

        if (toAddresses.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in toAddresses.EnumerateArray())
            {
                var s = el.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s))
                    result.Add(s);
            }
        }
        else if (toAddresses.ValueKind == JsonValueKind.String)
        {
            var csv = toAddresses.GetString() ?? "";
            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var s = part.Trim();
                if (!string.IsNullOrEmpty(s))
                    result.Add(s);
            }
        }

        return result;
    }

    internal static string BuildFromTemplate(string template, string poNumber, string attachmentName)
        => template
            .Replace("{poNumber}", poNumber)
            .Replace("{fileName}", attachmentName);

    private static bool IsRecipientError(SmtpCommandException ex)
        // SmtpErrorCode has no MailboxUnavailable member — the mailbox-unavailable
        // condition is expressed via the SMTP StatusCode instead.
        => ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted
        || ex.StatusCode is SmtpStatusCode.MailboxUnavailable
                         or SmtpStatusCode.MailboxNameNotAllowed
                         or SmtpStatusCode.UserNotLocalTryAlternatePath;

    // ── Config + credentials POCOs ────────────────────────────────────────────

    private sealed class SmtpConfig
    {
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public bool UseSsl { get; init; }
        public string? FromAddress { get; init; }
        // Flexible: may be JSON array OR comma-separated string — deserialized as raw JsonElement.
        public JsonElement ToAddresses { get; init; }
        public string? SubjectTemplate { get; init; }
        public string? BodyTemplate { get; init; }
        public string? AttachmentFileName { get; init; }
        public int? TimeoutSeconds { get; init; }
    }

    private sealed record SmtpCredentials(
        string Username,
        string? Password);
}
