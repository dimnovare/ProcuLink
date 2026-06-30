using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
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
///
/// <para>
/// <b>Postmark HTTPS transport (Railway).</b> Railway (and many other PaaS hosts)
/// block outbound SMTP ports (25/465/587), so raw-SMTP delivery times out there.
/// When <c>Delivery:Smtp:PostmarkServerToken</c> is configured, this dispatcher
/// instead POSTs the message to Postmark's REST API over HTTPS (port 443, which
/// Railway allows). The Postmark path reuses the same per-supplier From/recipients,
/// subject, body, and attachment that the MailKit path builds. When the token is
/// absent the dispatcher falls back to the unchanged raw-SMTP path, so behaviour is
/// identical without configuration.
/// </para>
/// </summary>
public class SmtpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly ILogger<SmtpDeliveryDispatcher> _logger;
    private readonly OutboundRequestGuard _guard;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string? _postmarkServerToken;

    /// <summary>Postmark transactional email send endpoint (HTTPS, port 443).</summary>
    private const string PostmarkSendUrl = "https://api.postmarkapp.com/email";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Smtp;

    /// <summary>
    /// Primary DI constructor. <paramref name="httpClientFactory"/> and
    /// <paramref name="configuration"/> are optional so the existing two-arg call sites
    /// (and the raw-SMTP unit tests) keep working; the Postmark path is only taken when
    /// both an <see cref="IHttpClientFactory"/> and a non-empty
    /// <c>Delivery:Smtp:PostmarkServerToken</c> are available.
    /// </summary>
    public SmtpDeliveryDispatcher(
        ILogger<SmtpDeliveryDispatcher> logger,
        OutboundRequestGuard guard,
        IHttpClientFactory? httpClientFactory = null,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _guard = guard;
        _httpClientFactory = httpClientFactory;
        _postmarkServerToken = configuration?["Delivery:Smtp:PostmarkServerToken"];
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

        // ── Postmark HTTPS transport (Railway blocks raw SMTP ports) ───────────
        // When a Postmark server token is configured, send over HTTPS (port 443)
        // instead of raw SMTP. Reuses the same From/recipients/subject/body/attachment.
        if (!string.IsNullOrWhiteSpace(_postmarkServerToken) && _httpClientFactory is not null)
        {
            return await SendViaPostmarkAsync(
                content, contentType, cfg.FromAddress!, recipients, subject, body, attachmentName, timeoutSeconds, ct);
        }

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

        // Dispatch with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeoutCts.Token;

        try
        {
            // ── SSRF guard — re-validated IMMEDIATELY before ConnectAsync to shrink the
            // DNS-rebinding TOCTOU window. MailKit reconnects by hostname (re-resolving), so
            // we cannot pin the IP without breaking TLS certificate/hostname validation; the
            // tightest available mitigation is to re-resolve+validate right before connect.
            var guardResult = await _guard.ValidateHostAsync(cfg.Host, port, token);
            if (!guardResult.Allowed)
                return new DeliveryResult(false, $"SMTP delivery blocked: {guardResult.Reason}");

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

    // ── Postmark HTTPS transport ────────────────────────────────────────────

    /// <summary>
    /// Sends the message via Postmark's REST API over HTTPS. Used in place of raw SMTP
    /// when <c>Delivery:Smtp:PostmarkServerToken</c> is configured, because Railway blocks
    /// outbound SMTP ports (25/465/587). The outbound POST goes through the SSRF-guarded
    /// named <c>delivery</c> <see cref="HttpClient"/> (connect-time-revalidating handler);
    /// <c>api.postmarkapp.com</c> is a public host so the guard passes.
    /// </summary>
    private async Task<DeliveryResult> SendViaPostmarkAsync(
        byte[] content,
        string contentType,
        string fromAddress,
        IReadOnlyList<string> recipients,
        string subject,
        string body,
        string attachmentName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeoutCts.Token;

        try
        {
            var payload = new PostmarkEmailRequest
            {
                From = fromAddress,
                To = string.Join(",", recipients),
                Subject = subject,
                TextBody = body,
                MessageStream = "outbound",
                Attachments = new[]
                {
                    new PostmarkAttachment
                    {
                        Name = attachmentName,
                        Content = Convert.ToBase64String(content),
                        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                    },
                },
            };

            // Reuse the SSRF-guarded "delivery" named client (same one HttpDeliveryDispatcher
            // mirrors) so a redirect/DNS-rebind to a private/metadata IP is still rejected at
            // TCP connect. api.postmarkapp.com is public, so the guard passes.
            var client = CreateSendClient();

            using var request = new HttpRequestMessage(HttpMethod.Post, PostmarkSendUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, PostmarkJsonOpts),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-Postmark-Server-Token", _postmarkServerToken);

            var response = await client.SendAsync(request, token);
            var responseBody = await response.Content.ReadAsStringAsync(token);

            PostmarkEmailResponse? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<PostmarkEmailResponse>(responseBody, JsonOpts);
            }
            catch (JsonException)
            {
                // Non-JSON body — handled below by the status-code / null-parse checks.
            }

            // Success = HTTP 200 AND Postmark ErrorCode 0.
            if (response.IsSuccessStatusCode && parsed is { ErrorCode: 0 })
                return new DeliveryResult(true, null, (int)response.StatusCode);

            var message = !string.IsNullOrWhiteSpace(parsed?.Message)
                ? parsed!.Message
                : $"Postmark returned HTTP {(int)response.StatusCode}.";

            _logger.LogWarning(
                "Postmark email delivery failed: HTTP {Status}, ErrorCode {ErrorCode}, Message {Message}",
                (int)response.StatusCode, parsed?.ErrorCode, message);

            return new DeliveryResult(false, $"Email delivery via Postmark failed: {message}", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryResult(false, "Email delivery via Postmark timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Postmark email delivery failed before a response was received.");
            return new DeliveryResult(false, "Email delivery via Postmark failed before a response was received.");
        }
    }

    // Built lazily once from the guard's connect-time-revalidating handler and reused
    // for the lifetime of this (scoped) dispatcher. SocketsHttpHandler pools connections.
    private HttpClient? _guardedClient;

    /// <summary>
    /// Resolves the <see cref="HttpClient"/> used for the outbound Postmark POST. Mirrors
    /// <c>HttpDeliveryDispatcher.CreateSendClient</c>: it copies the named <c>delivery</c>
    /// client's timeout but routes the socket through the guard's connect-time
    /// re-validating <see cref="System.Net.Http.SocketsHttpHandler"/>, so a DNS-rebind to a
    /// private/metadata IP after the validation is still rejected at TCP connect. Tests
    /// override this to inject a fake transport.
    /// </summary>
    internal virtual HttpClient CreateSendClient()
    {
        if (_guardedClient is not null) return _guardedClient;

        var timeout = _httpClientFactory!.CreateClient("delivery").Timeout;
        _guardedClient = new HttpClient(_guard.CreateGuardedHttpHandler(), disposeHandler: true)
        {
            Timeout = timeout,
        };
        return _guardedClient;
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

    // ── Postmark API DTOs ─────────────────────────────────────────────────────

    // Postmark expects PascalCase JSON property names ("From", "To", "Attachments", …),
    // which differs from the camelCase JsonOpts used for ProcuLink config — hence a
    // dedicated serializer that emits property names verbatim and omits nulls.
    private static readonly JsonSerializerOptions PostmarkJsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class PostmarkEmailRequest
    {
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public string Subject { get; init; } = "";
        public string TextBody { get; init; } = "";
        public string MessageStream { get; init; } = "outbound";
        public PostmarkAttachment[] Attachments { get; init; } = Array.Empty<PostmarkAttachment>();
    }

    private sealed class PostmarkAttachment
    {
        public string Name { get; init; } = "";
        public string Content { get; init; } = "";
        public string ContentType { get; init; } = "";
    }

    private sealed class PostmarkEmailResponse
    {
        public int ErrorCode { get; init; }
        public string? Message { get; init; }
    }
}
