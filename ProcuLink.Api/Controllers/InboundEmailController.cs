using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Public webhook endpoint for inbound email channels. Today: Postmark Inbound
/// (orders@{tenant}.proculink.app). Tomorrow: SendGrid Inbound Parse, or any
/// other provider that POSTs a JSON envelope with attachments.
///
/// This controller intentionally does NOT use Clerk authentication — it is a
/// public webhook. Authentication is the shared <c>X-Postmark-Server-Token</c>
/// header, compared against <c>Inbound:Postmark:WebhookToken</c> in config.
/// </summary>
[ApiController]
[Route("api/inbound-email")]
public sealed class InboundEmailController : ControllerBase
{
    private const string TokenHeader = "X-Postmark-Server-Token";

    private readonly IInboundEmailRouter _router;
    private readonly IConfiguration _config;
    private readonly ILogger<InboundEmailController> _logger;

    public InboundEmailController(
        IInboundEmailRouter router,
        IConfiguration config,
        ILogger<InboundEmailController> logger)
    {
        _router = router;
        _config = config;
        _logger = logger;
    }

    // ── POST /api/inbound-email/postmark ─────────────────────────────────────

    /// <summary>
    /// Postmark Inbound webhook. Postmark POSTs the parsed MIME of every inbound
    /// message to this URL. Verifies the shared token, maps the payload into
    /// the provider-neutral <see cref="InboundEmailPayload"/>, and delegates
    /// to <see cref="IInboundEmailRouter"/>.
    /// </summary>
    /// <remarks>
    /// Postmark contract: see https://postmarkapp.com/developer/user-guide/inbound/parse-an-email.
    /// The body is a single JSON object — multiple-recipient delivery is fanned
    /// out by Postmark into multiple POSTs, so we treat each request as one
    /// envelope with one <c>To</c>.
    /// </remarks>
    [HttpPost("postmark")]
    [EnableRateLimiting("upload")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Postmark([FromBody] PostmarkInboundPayload? body, CancellationToken ct)
    {
        // ── 1. Token check ───────────────────────────────────────────────────
        var expected = _config["Inbound:Postmark:WebhookToken"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Misconfiguration — refuse to accept inbound mail if the operator
            // has not set a token. Returning 401 instead of 500 keeps the
            // surface uniform from the webhook caller's perspective.
            _logger.LogError("Postmark inbound webhook called but Inbound:Postmark:WebhookToken is not configured.");
            return Unauthorized(new { error = "Inbound webhook is not configured." });
        }

        var presented = Request.Headers[TokenHeader].ToString();
        if (!CryptoEquals(expected, presented))
        {
            _logger.LogWarning("Postmark inbound webhook rejected: bad or missing {Header}.", TokenHeader);
            return Unauthorized(new { error = "Invalid webhook token." });
        }

        // ── 2. Payload validation ────────────────────────────────────────────
        if (body is null)
        {
            _logger.LogWarning("Postmark inbound webhook rejected: empty body.");
            return UnprocessableEntity(new { error = "Empty webhook body." });
        }

        var toAddress = ResolveRecipient(body);
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            _logger.LogWarning("Postmark inbound webhook rejected: no recipient on payload.");
            return UnprocessableEntity(new { error = "Missing recipient address." });
        }

        // ── 3. Map to provider-neutral shape ─────────────────────────────────
        var attachments = (body.Attachments ?? new List<PostmarkInboundAttachment>())
            .Select(a => new InboundAttachment(
                FileName: a.Name ?? string.Empty,
                ContentType: a.ContentType ?? "application/octet-stream",
                Content: DecodeBase64(a.Content)))
            .ToList();

        var payload = new InboundEmailPayload(
            FromEmail: body.From ?? string.Empty,
            ToEmail: toAddress!,
            Subject: body.Subject ?? string.Empty,
            Attachments: attachments);

        // ── 4. Delegate to router ────────────────────────────────────────────
        var result = await _router.RouteAsync(payload, ct);

        if (!result.Success)
        {
            // 422 keeps Postmark from retrying — the message is genuinely
            // unprocessable (bad tenant slug, blocked status, etc.). The
            // operator will see it in Postmark's inbound activity log.
            return UnprocessableEntity(new { error = result.Error, orgId = result.OrgId });
        }

        return Ok(new
        {
            orgId = result.OrgId,
            createdOrderIds = result.CreatedOrderIds,
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Prefer <c>OriginalRecipient</c> (preserves the alias the sender used),
    /// then <c>ToFull[0].Email</c>, then the comma-joined <c>To</c> string.
    /// </summary>
    private static string? ResolveRecipient(PostmarkInboundPayload body)
    {
        if (!string.IsNullOrWhiteSpace(body.OriginalRecipient))
            return body.OriginalRecipient;
        if (body.ToFull is { Count: > 0 } && !string.IsNullOrWhiteSpace(body.ToFull[0].Email))
            return body.ToFull[0].Email;
        if (!string.IsNullOrWhiteSpace(body.To))
        {
            var first = body.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return first;
        }
        return null;
    }

    private static byte[] DecodeBase64(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<byte>();
        try { return Convert.FromBase64String(content); }
        catch (FormatException) { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// Constant-time string compare. Prevents trivial timing-side-channel on
    /// the shared webhook token — overkill for a 200ms-budget webhook but
    /// effectively free.
    /// </summary>
    private static bool CryptoEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // ── Postmark contract DTOs ───────────────────────────────────────────────

    /// <summary>
    /// Subset of the Postmark Inbound JSON we actually consume. Postmark sends
    /// many more fields (Headers, MessageStream, Tag, etc.) — we ignore them.
    /// JsonNumberHandling allows the size field to be a number-or-string.
    /// </summary>
    public sealed class PostmarkInboundPayload
    {
        [JsonPropertyName("From")] public string? From { get; set; }
        [JsonPropertyName("FromName")] public string? FromName { get; set; }
        [JsonPropertyName("To")] public string? To { get; set; }
        [JsonPropertyName("ToFull")] public List<PostmarkAddress>? ToFull { get; set; }
        [JsonPropertyName("Cc")] public string? Cc { get; set; }
        [JsonPropertyName("Subject")] public string? Subject { get; set; }
        [JsonPropertyName("MessageID")] public string? MessageID { get; set; }
        [JsonPropertyName("OriginalRecipient")] public string? OriginalRecipient { get; set; }
        [JsonPropertyName("TextBody")] public string? TextBody { get; set; }
        [JsonPropertyName("HtmlBody")] public string? HtmlBody { get; set; }
        [JsonPropertyName("Attachments")] public List<PostmarkInboundAttachment>? Attachments { get; set; }
    }

    public sealed class PostmarkAddress
    {
        [JsonPropertyName("Email")] public string? Email { get; set; }
        [JsonPropertyName("Name")] public string? Name { get; set; }
    }

    public sealed class PostmarkInboundAttachment
    {
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Content")] public string? Content { get; set; }
        [JsonPropertyName("ContentType")] public string? ContentType { get; set; }
        [JsonPropertyName("ContentLength"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long ContentLength { get; set; }
        [JsonPropertyName("ContentID")] public string? ContentID { get; set; }
    }
}

/// <summary>
/// Hangfire adapter for <see cref="IParseJobEnqueuer"/>. Sits in
/// <c>ProcuLink.Api</c> because Hangfire and <c>ParseOrderJob</c> live here;
/// the router itself stays in <c>ProcuLink.Infrastructure</c> with no
/// Hangfire dependency.
/// </summary>
/// <remarks>
/// Register in <c>Program.cs</c>:
/// <code>
/// builder.Services.AddScoped&lt;IParseJobEnqueuer, HangfireParseJobEnqueuer&gt;();
/// builder.Services.AddScoped&lt;IInboundEmailRouter, InboundEmailRouter&gt;();
/// </code>
/// </remarks>
public sealed class HangfireParseJobEnqueuer : IParseJobEnqueuer
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireParseJobEnqueuer(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
    {
        ParseOrderJob.Enqueue(_jobs, orderId, orgId);
        return Task.CompletedTask;
    }
}
