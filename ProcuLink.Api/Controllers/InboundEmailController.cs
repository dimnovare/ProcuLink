using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure.Tenancy;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Public webhook endpoint for inbound email channels. Today: Postmark Inbound
/// (orders@{tenant}.proculink.eu). Tomorrow: SendGrid Inbound Parse, or any
/// other provider that POSTs a JSON envelope with attachments.
///
/// This controller intentionally does NOT use Clerk authentication — it is a
/// public webhook. Authentication happens in two independent layers, and it is worth being precise
/// about which one does what:
///
/// <list type="number">
/// <item><b>Transport</b> — the shared token in <c>Inbound:Postmark:WebhookToken</c> (plus the
/// optional edge <c>ProxySecret</c>) says the POST came from our relay rather than from a stranger
/// with the URL. It is one value for the whole deployment and it says NOTHING about which tenant
/// the message belongs to.</item>
/// <item><b>Tenant</b> — the recipient address, resolved by <c>IInboundAddressService</c> against
/// <c>org_inbound_addresses</c>. This is the per-tenant credential, and it is the only thing that
/// can name an organisation.</item>
/// </list>
///
/// The distinction is the whole point: the transport token guards the HTTP door, but the ordinary
/// way to reach this endpoint is to send an email, and the relay accepts mail from anybody. So the
/// transport layer cannot be what protects a tenant — only the address can.
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
    ///
    /// The status code is a retry instruction, not a verdict on the mail: 200 ends
    /// Postmark's interest in the message, anything else schedules ten more attempts
    /// over ~10.5 hours. So the endpoint answers 200 both when a message is ingested
    /// and when nothing could ever ingest it, and reserves non-200 for the cases where
    /// re-delivery is the point — our own outages (5xx), load shed by the rate limiter
    /// (429), a token the operator still has to fix (401), and a tenant that is only
    /// temporarily unable to accept orders (422).
    /// </remarks>
    [HttpPost("postmark")]
    [CrossOrganisationRead(
        "Postmark inbound webhook: the organisation is derived from the RECIPIENT address, which is " +
        "a per-tenant CREDENTIAL looked up in org_inbound_addresses by IInboundAddressService — the " +
        "query that DISCOVERS the tenant, so it necessarily runs before any tenant is known and " +
        "cannot be scoped to one. The endpoint authenticates with a shared token rather than an " +
        "ASP.NET auth scheme, so no tenant resolves from the caller and the router's reads are " +
        "unfiltered. A request that did arrive with a valid bearer token would arm the context, and " +
        "inbound mail for every other tenant would stop routing while the endpoint kept answering " +
        "200 — which is exactly how Postmark is told the message was handled. Declared so the " +
        "safety is deliberate rather than incidental.")]
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

        // Postmark INBOUND webhooks cannot send a custom request header — inbound
        // deliveries are authenticated by HTTP Basic-Auth or a token embedded in the
        // configured webhook URL. Accept the token from (in priority order): the
        // X-Postmark-Server-Token header (back-compat), a ?token= query param, or the
        // password half of HTTP Basic-Auth. (X-Postmark-Server-Token is an outbound-API
        // header and is never present on inbound POSTs.)
        var presented = ResolvePresentedToken(Request);
        if (!CryptoEquals(expected, presented))
        {
            _logger.LogWarning("Postmark inbound webhook rejected: bad or missing token (header/query/basic-auth).");
            return Unauthorized(new { error = "Invalid webhook token." });
        }

        // ── 1b. Edge-proxy secret (opt-in) ───────────────────────────────────
        // When Inbound:Postmark:ProxySecret is set, requests must also carry the
        // X-Inbound-Proxy-Secret header injected by the Cloudflare inbound-verify
        // Worker (docs/infra/postmark-inbound-verify-worker) — closing the direct
        // door to this endpoint so only Worker-vetted traffic (Postmark source IPs)
        // gets through. Unset = inert, so the code ships dark until the operator
        // deploys the Worker and sets the env var. Same 401 shape as a token
        // failure to keep the reject surface uniform.
        var proxySecret = _config["Inbound:Postmark:ProxySecret"];
        if (!string.IsNullOrWhiteSpace(proxySecret)
            && !CryptoEquals(proxySecret, Request.Headers["X-Inbound-Proxy-Secret"].FirstOrDefault() ?? string.Empty))
        {
            _logger.LogWarning("Postmark inbound webhook rejected: bad or missing X-Inbound-Proxy-Secret (edge proxy gate).");
            return Unauthorized(new { error = "Invalid webhook token." });
        }

        // ── 2. Payload validation ────────────────────────────────────────────
        // Both branches answer 200 (see Ignored): a re-delivery carries the same
        // bytes, so it would reach the same verdict ten more times.
        if (body is null)
        {
            _logger.LogWarning("Postmark inbound webhook rejected: empty body.");
            return Ignored("Empty webhook body.", orgId: null);
        }

        var toAddress = ResolveRecipient(body);
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            _logger.LogWarning("Postmark inbound webhook rejected: no recipient on payload.");
            return Ignored("Missing recipient address.", orgId: null);
        }

        // ── 3. Map to provider-neutral shape ─────────────────────────────────
        var attachments = (body.Attachments ?? new List<PostmarkInboundAttachment>())
            .Select(a =>
            {
                var decoded = DecodeBase64(a.Content);
                return new InboundAttachment(
                    FileName: a.Name ?? string.Empty,
                    ContentType: a.ContentType ?? "application/octet-stream",
                    Content: decoded.Content,
                    Decode: decoded.Decode);
            })
            .ToList();

        var payload = new InboundEmailPayload(
            FromEmail: body.From ?? string.Empty,
            ToEmail: toAddress!,
            Subject: body.Subject ?? string.Empty,
            Attachments: attachments,
            Body: ResolveBody(body),
            // Postmark's own MessageID is stable across its retry schedule (ten attempts over
            // ~10.5 hours), which is exactly what makes it the dedupe key for this channel: a
            // re-delivery of the message that already produced an order carries the same value.
            ProviderMessageId: body.MessageID);

        // ── 4. Delegate to router ────────────────────────────────────────────
        var result = await _router.RouteAsync(payload, ct);

        if (!result.Success)
        {
            return result.RejectionKind == InboundEmailRejectionKind.Permanent
                ? Ignored(result.Error, result.OrgId)
                // Transient — and an unlabelled rejection lands here too, because
                // duplicate webhook calls are the cheaper mistake. Any non-200 puts
                // the message back in Postmark's retry schedule.
                : UnprocessableEntity(new { error = result.Error, orgId = result.OrgId });
        }

        return Ok(new
        {
            orgId = result.OrgId,
            createdOrderIds = result.CreatedOrderIds,
        });
    }

    /// <summary>
    /// Accepts a message the product will never turn into an order, and tells the
    /// provider to stop re-delivering it.
    /// </summary>
    /// <remarks>
    /// Postmark retries on any status other than 200, ten times over roughly 10.5
    /// hours, then files the message as <c>Failed</c>. (An earlier comment here
    /// claimed 422 suppressed retries; production measurement on 2026-07-24 disproved
    /// it — one 422'd message produced three webhook attempts in six minutes and
    /// stayed <c>Scheduled</c>.) For a message whose rejection no re-delivery can
    /// change — an address that is not ours, a tenant slug that does not exist — those
    /// retries are pure noise, so we answer 200 and the message settles as
    /// <c>Processed</c>.
    ///
    /// The evidence does not disappear with the retries: every reject branch logs at
    /// Warning, the router writes an audit row wherever an organisation exists to own
    /// one, and this response body carries the reason into Postmark's inbound activity
    /// log. The <c>ignored</c> marker is what separates it from an ingested message,
    /// since both are 200s.
    ///
    /// Deliberately not 403. It would suppress the retries too, but only by leaving the
    /// message out of <c>Failed</c> as well, so an operator could never re-fire one that
    /// was refused by mistake — 200 keeps that door open. The inbound Cloudflare Worker
    /// (docs/infra/postmark-inbound-verify-worker) spends no 403 either, for the mirrored
    /// reason: its refusals are all operator-fixable, so they answer 503 and keep the
    /// retry window rather than dropping real orders on a stale IP allowlist.
    /// </remarks>
    private OkObjectResult Ignored(string? reason, Guid? orgId) =>
        Ok(new { status = "ignored", reason, orgId });

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

    /// <summary>
    /// Decodes one Postmark attachment body, keeping "we could not decode what they sent" and
    /// "they sent nothing" apart.
    /// </summary>
    /// <remarks>
    /// This method used to return a bare <c>byte[]</c> and answered <c>Array.Empty&lt;byte&gt;()</c>
    /// on both paths, which collapsed the two facts at the only point in the system that could
    /// still tell them apart — the router then had a single sentence for both and wrote no audit
    /// row for either, so a purchase order lost to a corrupt attachment was invisible from every
    /// position a human can occupy. The bytes and the observation now travel together; see
    /// <see cref="InboundAttachmentDecode"/>.
    ///
    /// A malformed body yields NO bytes rather than a partial recovery, and the offending base64
    /// is not returned, logged, or audited anywhere: it is a customer's file content.
    /// </remarks>
    private static (byte[] Content, InboundAttachmentDecode Decode) DecodeBase64(string? content)
    {
        // Absent or blank is a real, decodable answer: an empty attachment. (Postmark encodes a
        // zero-byte file as an empty string, and Convert.FromBase64String ignores whitespace, so
        // this short-circuit agrees with what the full decode would have returned anyway.)
        if (string.IsNullOrWhiteSpace(content))
            return (Array.Empty<byte>(), InboundAttachmentDecode.Decoded);

        try
        {
            return (Convert.FromBase64String(content), InboundAttachmentDecode.Decoded);
        }
        catch (FormatException)
        {
            return (Array.Empty<byte>(), InboundAttachmentDecode.Undecodable);
        }
    }

    /// <summary>
    /// Prefer Postmark's <c>TextBody</c>; fall back to stripped <c>HtmlBody</c>
    /// when only HTML is sent. The fallback is intentionally minimal — we only
    /// need a plain-text body good enough for the NLP extractor, not a perfect
    /// HTML-to-text renderer.
    /// </summary>
    private static string? ResolveBody(PostmarkInboundPayload body)
    {
        if (!string.IsNullOrWhiteSpace(body.TextBody))
            return body.TextBody;
        if (!string.IsNullOrWhiteSpace(body.HtmlBody))
            return StripHtml(body.HtmlBody!);
        return null;
    }

    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    // ── POST /api/inbound-email/postmark-bounce ──────────────────────────────

    /// <summary>
    /// Postmark Bounce / SpamComplaint webhook. This is the OUTBOUND direction: Postmark POSTs here
    /// when a message ProcuLink sent to a supplier bounces or is reported as spam.
    /// </summary>
    /// <remarks>
    /// Until this existed, "delivered" on the email channels meant only that the first hop accepted
    /// the handoff — a Postmark 2xx. A mistyped supplier address is accepted at that moment and
    /// bounces seconds later, and the order read <c>delivered</c> permanently. Nothing anywhere
    /// consumed a bounce.
    ///
    /// <para>Authentication is the SAME shared token the inbound endpoint uses, because it is the
    /// same relay and the same trust question: did this POST come from our provider. Postmark's
    /// outbound webhooks CAN send a custom header, but the token is resolved through the same
    /// header/query/basic-auth ladder so one configured value serves both URLs.</para>
    ///
    /// <para>The status code is a retry instruction. 200 ends Postmark's interest; a bounce that
    /// cannot be attributed is still answered 200, because ten re-deliveries of the same payload
    /// reach the same verdict — the operator learns about it from the Warning the handler logs, not
    /// from Postmark trying again. Non-200 is reserved for a token the operator must fix (401) and
    /// for our own failure to persist (500), where re-delivery is the point.</para>
    /// </remarks>
    [HttpPost("postmark-bounce")]
    [CrossOrganisationRead(
        "Postmark outbound bounce webhook: the organisation is derived from the DELIVERY ATTEMPT the " +
        "bounce metadata resolves to, never from the caller. Authenticated by the same shared token " +
        "as the inbound endpoint rather than an ASP.NET auth scheme, so no tenant resolves from the " +
        "request and the attempt lookup necessarily runs unfiltered — it is the query that DISCOVERS " +
        "the tenant. Scoping it would make every bounce unattributable while the endpoint kept " +
        "answering 200, which is exactly how Postmark is told the bounce was handled. Declared so " +
        "the safety is deliberate rather than incidental.")]
    [EnableRateLimiting("upload")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> PostmarkBounce(
        [FromBody] PostmarkBouncePayload? body,
        [FromServices] IDeliveryBounceHandler handler,
        CancellationToken ct)
    {
        var expected = _config["Inbound:Postmark:WebhookToken"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogError("Postmark bounce webhook called but Inbound:Postmark:WebhookToken is not configured.");
            return Unauthorized(new { error = "Inbound webhook is not configured." });
        }

        if (!CryptoEquals(expected, ResolvePresentedToken(Request)))
        {
            _logger.LogWarning("Postmark bounce webhook rejected: bad or missing token.");
            return Unauthorized(new { error = "Invalid webhook token." });
        }

        var proxySecret = _config["Inbound:Postmark:ProxySecret"];
        if (!string.IsNullOrWhiteSpace(proxySecret)
            && !CryptoEquals(proxySecret, Request.Headers["X-Inbound-Proxy-Secret"].FirstOrDefault() ?? string.Empty))
        {
            _logger.LogWarning("Postmark bounce webhook rejected: bad or missing X-Inbound-Proxy-Secret.");
            return Unauthorized(new { error = "Invalid webhook token." });
        }

        if (body is null)
        {
            _logger.LogWarning("Postmark bounce webhook rejected: empty body.");
            return Ok(new { handled = false, reason = "empty_body" });
        }

        var kind = ClassifyBounce(body);
        if (kind is null)
        {
            // A SOFT bounce, a transient block, or a subscription-management event. Postmark retries
            // soft bounces on its own, and moving an order off 'delivered' for one would manufacture
            // a failure the supplier never saw. Debug, not Warning: these are the common case.
            _logger.LogDebug(
                "Postmark bounce webhook ignored: RecordType={RecordType} Type={Type} TypeCode={TypeCode} — not terminal.",
                body.RecordType, body.Type, body.TypeCode);
            return Ok(new { handled = false, reason = "not_terminal" });
        }

        var result = await handler.HandleAsync(
            new DeliveryBounceNotification(
                IdempotencyKey:    ReadDeliveryKey(body.Metadata),
                Kind:              kind.Value,
                Recipient:         body.Email,
                Description:       string.IsNullOrWhiteSpace(body.Description) ? body.Details : body.Description,
                ProviderMessageId: body.MessageID),
            ct);

        return Ok(new { handled = true, outcome = result.Outcome.ToString(), orderId = result.OrderId });
    }

    /// <summary>
    /// Maps a Postmark webhook payload to the terminal kinds ProcuLink acts on, or null for
    /// everything else.
    ///
    /// <para>Postmark sends bounce records for a long list of types. Only two are terminal statements
    /// about the supplier's address: a HARD bounce (the address does not exist) and a spam complaint.
    /// <c>SpamNotification</c> is the bounce-typed form of a complaint; <c>SpamComplaint</c> is the
    /// separate RecordType. A soft bounce, a transient DNS failure or a throttle is Postmark's
    /// problem to retry, and <c>Inactive</c> alone is not a cause — it is the consequence Postmark
    /// applies AFTER a hard bounce, and it is also set for a suppressed recipient, so treating it as
    /// terminal on its own would fail orders that were never sent.</para>
    /// </summary>
    private static DeliveryBounceKind? ClassifyBounce(PostmarkBouncePayload body)
    {
        if (string.Equals(body.RecordType, "SpamComplaint", StringComparison.OrdinalIgnoreCase))
            return DeliveryBounceKind.SpamComplaint;

        if (string.Equals(body.Type, "SpamNotification", StringComparison.OrdinalIgnoreCase))
            return DeliveryBounceKind.SpamComplaint;

        if (string.Equals(body.Type, "HardBounce", StringComparison.OrdinalIgnoreCase))
            return DeliveryBounceKind.Hard;

        return null;
    }

    /// <summary>
    /// Reads the delivery key out of the metadata bag Postmark echoes back. Postmark lower-cases
    /// metadata keys in webhook payloads, so the lookup is case-insensitive — a case-sensitive read
    /// would find nothing on every real bounce while every unit test that spells the key exactly
    /// passed.
    /// </summary>
    private static string? ReadDeliveryKey(Dictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;

        foreach (var (key, value) in metadata)
        {
            if (string.Equals(key, DeliveryBounceMetadata.IdempotencyKeyField, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string StripHtml(string html)
    {
        // Drop <script> and <style> blocks (content, not just tags) so their
        // text doesn't bleed into the extracted body.
        var noScripts = Regex.Replace(html, "<script[^>]*>.*?</script>",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var noStyles = Regex.Replace(noScripts, "<style[^>]*>.*?</style>",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var stripped = HtmlTagRegex.Replace(noStyles, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    /// <summary>
    /// Resolves the presented webhook token from (in priority order): the
    /// <c>X-Postmark-Server-Token</c> header (back-compat), the <c>token</c>
    /// query-string parameter, or the password half of HTTP Basic-Auth. Postmark
    /// inbound webhooks cannot attach custom headers, so the URL-embedded token
    /// (<c>?token=</c>) and Basic-Auth (<c>https://user:token@host/...</c>) paths
    /// are what actually authenticate real inbound deliveries.
    /// </summary>
    private static string ResolvePresentedToken(HttpRequest request)
    {
        var header = request.Headers[TokenHeader].ToString();
        if (!string.IsNullOrEmpty(header)) return header;

        var query = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(query)) return query;

        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(auth["Basic ".Length..].Trim()));
                var sep = decoded.IndexOf(':');
                return sep >= 0 ? decoded[(sep + 1)..] : decoded;
            }
            catch (FormatException) { /* malformed Basic-Auth — treated as no token */ }
        }
        return string.Empty;
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
    /// Subset of the Postmark Bounce / SpamComplaint webhook JSON we consume.
    ///
    /// <para><c>Metadata</c> is the load-bearing field: it is what ProcuLink stamped on the outbound
    /// send and is the only thing here that can name an order. Postmark echoes it on both record
    /// types. <c>MessageID</c> is Postmark's own id and is kept for the audit row only — it is not a
    /// correlation key, because nothing persists it on our side.</para>
    /// </summary>
    public sealed class PostmarkBouncePayload
    {
        /// <summary>"Bounce" or "SpamComplaint". Absent on older payload shapes.</summary>
        [JsonPropertyName("RecordType")] public string? RecordType { get; set; }

        /// <summary>Bounce type name — "HardBounce", "SoftBounce", "SpamNotification", …</summary>
        [JsonPropertyName("Type")] public string? Type { get; set; }

        [JsonPropertyName("TypeCode")] public int? TypeCode { get; set; }
        [JsonPropertyName("MessageID")] public string? MessageID { get; set; }
        [JsonPropertyName("Email")] public string? Email { get; set; }
        [JsonPropertyName("Description")] public string? Description { get; set; }
        [JsonPropertyName("Details")] public string? Details { get; set; }
        [JsonPropertyName("Inactive")] public bool? Inactive { get; set; }
        [JsonPropertyName("Metadata")] public Dictionary<string, string>? Metadata { get; set; }
    }

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
