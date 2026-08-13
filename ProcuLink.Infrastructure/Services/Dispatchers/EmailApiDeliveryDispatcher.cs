using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// Email delivery dispatcher (Protocol = <c>email</c>). Sends the generated artifact as an email
/// attachment to the supplier via the HTTP email API (<see cref="IEmailApiClient"/>, Postmark) over
/// HTTPS — so it is unaffected by the cloud host's outbound-SMTP block (Railway). This is the offered
/// replacement for the legacy <see cref="SmtpDeliveryDispatcher"/>.
///
/// <para>
/// Unlike raw SMTP, the supplier provides ONLY recipient addresses (no host/port/SSL/credentials).
/// Mail is sent FROM ProcuLink's provider-verified sender (<see cref="IEmailApiClient.DefaultFrom"/>),
/// with the buyer contact in <c>Reply-To</c>. The config shape mirrors what the frontend
/// <c>DeliveryConfigEditor</c> writes for the <c>email</c> protocol.
/// </para>
/// </summary>
public sealed class EmailApiDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly IEmailApiClient _email;
    private readonly ILogger<EmailApiDeliveryDispatcher> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Email;

    // Only a deterministic Message-ID is set. Receiving-MTA dedup on a caller-supplied
    // Message-ID is best-effort and rarely applied, so a re-send after an unknown outcome
    // most likely lands a duplicate email.
    public ResendSafety ResendSafety => ResendSafety.Unsafe;

    // The provider's own response payload is carried verbatim on every failure, so a blank
    // ResponseBody here means the provider really did say nothing. Before WP-19 this dispatcher
    // returned only (false, result.Error, result.StatusCode) — so on the CANONICAL, live outbound
    // channel every 400 read as unexplained and went back into the automatic re-send path.
    public bool CapturesSupplierResponseBody => true;

    public EmailApiDeliveryDispatcher(IEmailApiClient email, ILogger<EmailApiDeliveryDispatcher> logger)
    {
        _email = email;
        _logger = logger;
    }

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct,
        string? idempotencyKey = null,
        bool isTestFire = false)
    {
        if (!_email.IsConfigured)
            return new DeliveryResult(false,
                "Email delivery is not configured on this deployment (no email-API provider token).");

        EmailDeliveryConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<EmailDeliveryConfig>(config.ConfigJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Email delivery config JSON malformed.");
            return new DeliveryResult(false, "Email delivery configuration could not be parsed.");
        }

        if (cfg is null)
            return new DeliveryResult(false, "Email delivery configuration is invalid.");

        var recipients = ParseRecipients(cfg.ToAddresses);
        if (recipients.Count == 0)
            return new DeliveryResult(false, "Email delivery has no valid recipient addresses.");

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        // Subject/body/attachment naming live in EmailMessageComposer, shared with the SMTP
        // dispatcher — a test fire says it is a test on BOTH email channels or on neither.
        var attachmentName = EmailMessageComposer.AttachmentName(
            isTestFire, cfg.AttachmentFileName, fileName);

        var subject = EmailMessageComposer.Subject(
            isTestFire, cfg.SubjectTemplate, fileNameWithoutExt, attachmentName);

        var bodyText = EmailMessageComposer.Body(
            isTestFire, cfg.BodyTemplate, fileNameWithoutExt, attachmentName);

        // From defaults to the provider-verified platform sender. A custom fromAddress is honoured
        // only if supplied (operator must have verified that domain with the provider, else the
        // provider rejects the send — surfaced as a delivery_failed with the provider's reason).
        var from = string.IsNullOrWhiteSpace(cfg.FromAddress) ? _email.DefaultFrom : cfg.FromAddress!;

        // A3 idempotency: a deterministic Message-ID (stable across a re-send of the same artifact)
        // lets a receiving MTA de-duplicate. Best-effort only — MTA dedup on a caller-supplied
        // Message-ID is rarely applied, which is why this dispatcher declares ResendSafety.Unsafe
        // and DeliveryService parks an unknown-outcome re-drive rather than risking a duplicate email.
        var headers = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : new[] { new EmailApiHeader("Message-ID", $"<{idempotencyKey}@proculink.eu>") };

        var message = new EmailApiMessage(
            From: from,
            To: recipients,
            Subject: subject,
            TextBody: bodyText,
            Attachments: new[] { new EmailApiAttachment(attachmentName, contentType, content) },
            ReplyTo: string.IsNullOrWhiteSpace(cfg.ReplyTo) ? null : cfg.ReplyTo,
            Headers: headers);

        var result = await _email.SendAsync(message, ct);
        return result.Success
            ? new DeliveryResult(true, null, result.StatusCode)
            : new DeliveryResult(
                false,
                result.Error ?? "Email delivery failed.",
                result.StatusCode,
                ResponseBody: result.ResponseBody,
                SupplierReasonObservable: CapturesSupplierResponseBody,
                RetryAfter: result.RetryAfter);
    }

    /// <summary>
    /// Parses <c>toAddresses</c> which may be a JSON array of strings, a JSON string of
    /// comma-separated addresses, or absent. Mirrors the legacy SMTP dispatcher's parser.
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
        => EmailMessageComposer.Render(template, poNumber, attachmentName);

    /// <summary>Config POCO — mirrors the JSON the frontend writes for the <c>email</c> protocol.</summary>
    private sealed class EmailDeliveryConfig
    {
        // Flexible: JSON array OR comma-separated string — deserialized as raw JsonElement.
        public JsonElement ToAddresses { get; init; }
        public string? FromAddress { get; init; }
        public string? ReplyTo { get; init; }
        public string? SubjectTemplate { get; init; }
        public string? BodyTemplate { get; init; }
        public string? AttachmentFileName { get; init; }
    }
}
