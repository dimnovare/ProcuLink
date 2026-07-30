using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services.Email;

/// <summary>
/// Postmark-backed <see cref="IEmailApiClient"/>. POSTs to <c>https://api.postmarkapp.com/email</c>
/// over HTTPS (port 443) — unaffected by the cloud host's outbound-SMTP block. The host is fixed and
/// trusted, so no SSRF guard is needed (unlike the tenant-URL HTTP delivery dispatcher).
///
/// <para>
/// No-op (<see cref="IsConfigured"/> = false) when <c>Email:Postmark:ServerToken</c> is absent — the
/// safe default deploy. Config keys:
/// <list type="bullet">
///   <item><c>Email:Postmark:ServerToken</c> — Postmark server API token (required to send).</item>
///   <item><c>Email:Postmark:From</c> — default verified sender (falls back to <c>Smtp:From</c>, then <c>orders@proculink.eu</c>).</item>
///   <item><c>Email:Postmark:MessageStream</c> — Postmark message stream (default <c>outbound</c>).</item>
/// </list>
/// </para>
/// </summary>
public sealed class PostmarkEmailApiClient : IEmailApiClient
{
    private const string SendUrl = "https://api.postmarkapp.com/email";
    private const string TokenHeader = "X-Postmark-Server-Token";
    /// <summary>Named-<see cref="System.Net.Http.IHttpClientFactory"/> key, shared with DI registration in both hosts.</summary>
    public const string HttpClientName = "email";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PostmarkEmailApiClient> _logger;
    private readonly string? _token;
    private readonly string _from;
    private readonly string _messageStream;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PostmarkEmailApiClient(
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        ILogger<PostmarkEmailApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _token = config["Email:Postmark:ServerToken"];
        _from = FirstNonBlank(config["Email:Postmark:From"], config["Smtp:From"], "orders@proculink.eu");
        _messageStream = FirstNonBlank(config["Email:Postmark:MessageStream"], "outbound");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token);

    public string DefaultFrom => _from;

    public async Task<EmailApiResult> SendAsync(EmailApiMessage message, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning(
                "PostmarkEmailApiClient invoked but Email:Postmark:ServerToken is not configured — dropping email.");
            return new EmailApiResult(false, "Email API is not configured (no provider token).");
        }

        if (message.To.Count == 0)
            return new EmailApiResult(false, "Email has no recipient addresses.");

        var payload = new PostmarkSendRequest
        {
            From = string.IsNullOrWhiteSpace(message.From) ? _from : message.From,
            To = string.Join(",", message.To),
            ReplyTo = string.IsNullOrWhiteSpace(message.ReplyTo) ? null : message.ReplyTo,
            Subject = message.Subject,
            TextBody = message.TextBody,
            MessageStream = _messageStream,
            Attachments = message.Attachments is { Count: > 0 }
                ? message.Attachments.Select(a => new PostmarkAttachment
                {
                    Name = a.FileName,
                    Content = Convert.ToBase64String(a.Content),
                    ContentType = string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType,
                }).ToList()
                : null,
            // A3 idempotency: custom headers (e.g. a deterministic Message-ID) passed through to Postmark.
            Headers = message.Headers is { Count: > 0 }
                ? message.Headers.Select(h => new PostmarkHeader { Name = h.Name, Value = h.Value }).ToList()
                : null,
        };

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, SendUrl);
            request.Headers.TryAddWithoutValidation(TokenHeader, _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = JsonContent.Create(payload, options: JsonOpts);

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var code = (int)response.StatusCode;

            // Postmark returns HTTP 200 + "ErrorCode":0 on success. A non-zero ErrorCode (even with
            // HTTP 200 — e.g. inactive recipient) or a non-2xx status is a failure.
            int? errorCode = null;
            string? providerMessage = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ErrorCode", out var ec) && ec.ValueKind == JsonValueKind.Number)
                    errorCode = ec.GetInt32();
                if (doc.RootElement.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String)
                    providerMessage = m.GetString();
            }
            catch (JsonException)
            {
                // Non-JSON body — fall back to the HTTP status code below.
            }

            if (response.IsSuccessStatusCode && errorCode is null or 0)
                return new EmailApiResult(true, null, code);

            var reason = string.IsNullOrWhiteSpace(providerMessage)
                ? $"email provider returned HTTP {code}."
                : providerMessage;
            _logger.LogWarning(
                "Postmark send failed: HTTP {Code} ErrorCode={ErrorCode} {Message}", code, errorCode, providerMessage);
            // The raw body is carried alongside the parsed reason. Email is the canonical outbound
            // channel, and SupplierResponseClassification's 400 split reads the BODY: dropping it
            // (as this did) made every provider refusal on the live path look unexplained, so the
            // order was re-sent instead of being put in front of a human.
            return new EmailApiResult(
                false, $"Email delivery failed: {reason}", code,
                ResponseBody: string.IsNullOrWhiteSpace(body) ? null : body,
                RetryAfter: RetryAfterHeader.Read(response.Headers, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EmailApiResult(false, "Email delivery timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Postmark send threw before a response was received.");
            return new EmailApiResult(false, "Email delivery failed before a response was received.");
        }
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        return string.Empty;
    }

    // ── Postmark send contract (subset we use) ─────────────────────────────────

    private sealed class PostmarkSendRequest
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string? ReplyTo { get; set; }
        public string Subject { get; set; } = "";
        public string TextBody { get; set; } = "";
        public string MessageStream { get; set; } = "outbound";
        public List<PostmarkAttachment>? Attachments { get; set; }
        public List<PostmarkHeader>? Headers { get; set; }
    }

    private sealed class PostmarkAttachment
    {
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
        public string ContentType { get; set; } = "";
    }

    private sealed class PostmarkHeader
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
