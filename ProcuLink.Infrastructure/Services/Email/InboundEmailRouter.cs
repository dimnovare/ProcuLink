using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;

namespace ProcuLink.Infrastructure.Services.Email;

/// <summary>
/// Routes inbound emails (today: Postmark Inbound webhook, tomorrow: SendGrid
/// Inbound Parse) into the existing CreateStub + ParseOrderJob pipeline.
/// </summary>
/// <remarks>
/// A message whose organisation has no supplier is HELD, not rejected: its attachments are
/// imported via <c>IOrderService.CreateUnroutedStubAsync</c> and the parse job parks them
/// <c>unrouted</c> for <c>POST /api/orders/{id}/assign-supplier</c> — the same hold the pull
/// channels use. The caller therefore answers 200; a false <c>InboundEmailResult.Success</c>
/// (and the webhook's 422) is reserved for genuinely unprocessable messages: an unparseable
/// recipient, an unknown tenant slug, and an organisation whose account status blocks ingest.
///
/// Tenant resolution routes on the organisation's own unique <c>Slug</c> (auto-generated
/// at org creation), so any org can receive orders with no per-org setup. Two recipient
/// schemes are supported: the preferred <c>{slug}@{InboundDomain}</c> (local-part; single
/// MX, set <c>Inbound:Postmark:InboundDomain</c>) and the legacy <c>orders@{slug}.proculink.eu</c>
/// (subdomain; needs a wildcard MX). An explicit <c>Inbound:Postmark:TenantMapping:{slug}</c>
/// config entry is still honoured as an override/fallback. (Live receipt also needs the
/// inbound MX + Postmark domain configured — that is one-time infra, not per-org.)
///
/// Log levels: this runs once per inbound message an org receives, so the expected
/// cases — no attachments, an unsupported attachment (signature images ride along on
/// most mail), body extraction yielding no order — are <c>Debug</c>. API production
/// runs at <c>Default=Information</c>, so those stay out of the log unless someone
/// turns them on; the two attachment cases still write their audit row, so demoting
/// them loses no evidence. <c>Warning</c> is what an operator has to act on: every
/// message-level reject, and an attachment dropped for a content reason (empty,
/// oversized, stub creation failed). An order actually created is <c>Information</c>
/// — one line per order, not per message.
/// </remarks>
public sealed class InboundEmailRouter : IInboundEmailRouter
{
    /// <summary>
    /// Extensions the router accepts. Pre-filter happens here; if a registered
    /// <c>IPurchaseOrderParser</c> later rejects the file (e.g. EDI parser not
    /// yet wired up), <c>IOrderService.CreateStubAsync</c> returns a Failure
    /// result and the attachment is skipped with a warning log.
    /// </summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".xlsx",
        ".pdf",
        ".xml",
        ".cxml",  // cXML is a first-class input format (CxmlOrderParser) — accept it from email too
        ".edi",
        ".x12",   // ANSI X12 850 is a first-class input format (X12OrderParser) — accept from email
        ".txt",
    };

    /// <summary>
    /// Account statuses that block ingest. Mirrors the read-only gate used by
    /// <c>OrdersController.Upload</c> via <c>IBillingService.CheckOrderLimitAsync</c>.
    /// We can't reuse that path verbatim because billing also enforces monthly
    /// volume limits; for the inbound webhook we only gate on account status —
    /// monthly limits are enforced downstream by <c>ParseOrderJob</c>.
    /// </summary>
    private static readonly HashSet<string> BlockedAccountStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        AccountStatusConstants.ReadOnly,
        AccountStatusConstants.TrialExpired,
    };

    /// <summary>
    /// Default host suffix used when parsing the recipient address. The full
    /// recipient is <c>orders@{slug}.proculink.eu</c>; if the founder hosts
    /// the inbound MX on a different domain (e.g. <c>inbound.proculink.eu</c>),
    /// override via config <c>Inbound:Postmark:HostSuffix</c>.
    /// </summary>
    private const string DefaultHostSuffix = ".proculink.eu";

    /// <summary>
    /// Provenance tag stamped on orders the router creates from the email body
    /// (after the NLP extractor). The review UI reads this from
    /// <c>PurchaseOrderEntity.CanonicalJson.source</c> to show how the order
    /// was created (vs. attachment upload, IMAP poll, etc.).
    /// </summary>
    private const string EmailBodyNlpSourceTag = "email_body_nlp";

    private readonly ProcuLinkDbContext _db;
    private readonly IOrderService _orders;
    private readonly IParseJobEnqueuer _enqueuer;
    private readonly IEmailBodyOrderExtractor _bodyExtractor;
    private readonly IConfiguration _config;
    private readonly ILogger<InboundEmailRouter> _logger;

    public InboundEmailRouter(
        ProcuLinkDbContext db,
        IOrderService orders,
        IParseJobEnqueuer enqueuer,
        IEmailBodyOrderExtractor bodyExtractor,
        IConfiguration config,
        ILogger<InboundEmailRouter> logger)
    {
        _db = db;
        _orders = orders;
        _enqueuer = enqueuer;
        _bodyExtractor = bodyExtractor;
        _config = config;
        _logger = logger;
    }

    public async Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct)
    {
        // ── 1. Resolve the tenant slug from the recipient address ────────────
        var slug = ExtractTenantSlug(payload.ToEmail);
        if (slug is null)
        {
            _logger.LogWarning(
                "Inbound email rejected: recipient address {To} does not match orders@{{slug}}{HostSuffix}.",
                payload.ToEmail, GetHostSuffix());
            return new InboundEmailResult(false, OrgId: null, Array.Empty<Guid>(),
                $"Recipient '{payload.ToEmail}' does not look like an inbound ProcuLink address.");
        }

        var orgId = await ResolveOrgIdFromSlugAsync(slug, ct);
        if (orgId is null)
        {
            _logger.LogWarning("Inbound email rejected: no tenant mapping for slug {Slug}.", slug);
            return new InboundEmailResult(false, OrgId: null, Array.Empty<Guid>(),
                $"Unknown tenant slug '{slug}'.");
        }

        // ── 2. Load the organisation + verify account-status gate ───────────
        var org = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == orgId.Value)
            .Select(o => new { o.Id, o.AccountStatus, o.EmailConfigJson, o.SelfHostedOcr })
            .FirstOrDefaultAsync(ct);

        if (org is null)
        {
            _logger.LogWarning(
                "Inbound email rejected: slug {Slug} mapped to org {OrgId} but no such organisation exists.",
                slug, orgId.Value);
            return new InboundEmailResult(false, OrgId: orgId, Array.Empty<Guid>(),
                $"Organisation '{orgId.Value}' not found.");
        }

        if (BlockedAccountStatuses.Contains(org.AccountStatus))
        {
            _logger.LogWarning(
                "Inbound email for org {OrgId} ignored: account_status={Status} blocks ingest.",
                org.Id, org.AccountStatus);
            await WriteAuditAsync(org.Id, "inbound_email.rejected_read_only", payload, ct);
            return new InboundEmailResult(false, OrgId: orgId, Array.Empty<Guid>(),
                $"Organisation is in '{org.AccountStatus}' status and cannot ingest new orders.");
        }

        // ── 3. Resolve a default supplier for the org ────────────────────────
        // The inbound webhook does not carry the supplier identity directly.
        // Prefer the supplier configured for IMAP polling (same JSONB column);
        // otherwise fall back to the org's first non-deleted supplier.
        //
        // No supplier at all is NOT a rejection. This used to answer 422 and the
        // message vanished from the product's view. Attachments are now imported
        // through the UNROUTED hold instead — the same path the pull channels use
        // (SftpIngressService / S3IngressService / EmailPollOrgJob) — so the order
        // lands in the inbox parked 'unrouted' and POST /api/orders/{id}/assign-supplier
        // routes and re-parses it. The webhook answers 200; 422 is reserved for
        // genuinely unprocessable messages (unknown tenant slug, blocked org).
        var supplierId = await ResolveSupplierIdAsync(org.Id, org.EmailConfigJson, ct);
        if (supplierId is null)
        {
            _logger.LogInformation(
                "Inbound email for org {OrgId} has no supplier configured — importing attachments unrouted for operator assignment.",
                org.Id);
            await WriteAuditAsync(org.Id, "inbound_email.unrouted_no_supplier", payload, ct);
        }

        // ── 4. Filter attachments ────────────────────────────────────────────
        // Note: an empty attachment list is no longer an early-return — the
        // body-NLP fallback below may still produce an order from prose text.
        if (payload.Attachments.Count == 0)
        {
            _logger.LogDebug(
                "Inbound email for org {OrgId} carried no attachments; the email-body NLP fallback runs if a body is present and a supplier is known.",
                org.Id);
            await WriteAuditAsync(org.Id, "inbound_email.no_attachments", payload, ct);
        }

        // ── 5. Create one stub per supported attachment ──────────────────────
        var created = new List<Guid>(payload.Attachments.Count);
        foreach (var att in payload.Attachments)
        {
            var extension = Path.GetExtension(att.FileName ?? string.Empty).ToLowerInvariant();
            if (!SupportedExtensions.Contains(extension))
            {
                _logger.LogDebug(
                    "Inbound email attachment {FileName} ({Ext}) for org {OrgId} skipped: unsupported type.",
                    att.FileName, extension, org.Id);
                await WriteAuditAsync(
                    org.Id,
                    "inbound_email.attachment_skipped_unsupported",
                    payload with { Attachments = new[] { Strip(att) } },
                    ct);
                continue;
            }

            if (att.Content is null || att.Content.Length == 0)
            {
                _logger.LogWarning(
                    "Inbound email attachment {FileName} for org {OrgId} has empty content — skipping.",
                    att.FileName, org.Id);
                continue;
            }

            // Size cap — skip oversized attachments before the parse pipeline.
            if (att.Content.Length > IngressLimits.MaxFileBytes)
            {
                _logger.LogWarning(
                    "Inbound email attachment {FileName} for org {OrgId} is {Bytes} bytes (> {Max} byte cap) — skipping.",
                    att.FileName, org.Id, att.Content.Length, IngressLimits.MaxFileBytes);
                await WriteAuditAsync(
                    org.Id,
                    "inbound_email.attachment_skipped_too_large",
                    payload with { Attachments = new[] { Strip(att) } },
                    ct);
                continue;
            }

            await using var ms = new MemoryStream(att.Content, writable: false);
            var contentType = string.IsNullOrWhiteSpace(att.ContentType)
                ? "application/octet-stream"
                : att.ContentType;

            // IOrderService.CreateStubAsync uploads to R2 and creates the stub.
            // It is the same call browser-upload and IMAP-poll use today. With no
            // supplier we take the unrouted sibling instead: same upload, NULL
            // supplier_id, and the parse job parks the order 'unrouted'.
            var stubResult = supplierId is { } routedSupplierId
                ? await _orders.CreateStubAsync(
                    org.Id, routedSupplierId, ms, att.FileName ?? "attachment", contentType, ct)
                : await _orders.CreateUnroutedStubAsync(
                    org.Id, ms, att.FileName ?? "attachment", contentType, ct);

            if (!stubResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Inbound email attachment {FileName} for org {OrgId} could not create order stub: {Error}",
                    att.FileName, org.Id, stubResult.Error);
                continue;
            }

            var orderId = stubResult.Value!.Id;
            await _enqueuer.EnqueueAsync(orderId, org.Id, ct);
            created.Add(orderId);

            _logger.LogInformation(
                "Inbound email created {Mode} order {OrderId} for org {OrgId} (attachment={FileName}).",
                supplierId is null ? "unrouted" : "routed", orderId, org.Id, att.FileName);
        }

        // ── 6. Email-body NLP fallback ───────────────────────────────────────
        // When no attachment produced an order — either nothing was attached or
        // every attachment was unsupported / empty / rejected — fall back to
        // extracting a purchase order from the email body itself. The extractor
        // is a no-op without an OpenAI key, so this is safe in unit tests and
        // local dev where the body field is set but the AI provider is absent.
        // No-egress orgs are excluded: the body extractor sends the prose to OpenAI,
        // which would violate the no-data-leaves guarantee.
        //
        // KNOWN GAP: this path is skipped when the org has no supplier. The persist call
        // (CreateStubFromParsedOrderAsync) takes a non-nullable supplier id and there is no
        // unrouted sibling for it, so a prose-only email to a supplier-less org still yields
        // no order — it is accepted (200) and audited rather than 422'd, and any ATTACHMENT on
        // the same message is parked unrouted above. Closing it needs an unrouted overload of
        // CreateStubFromParsedOrderAsync; do not fabricate a supplier id here.
        if (created.Count == 0
            && supplierId is { } bodySupplierId
            && !string.IsNullOrWhiteSpace(payload.Body)
            && !org.SelfHostedOcr)
        {
            try
            {
                var extraction = await _bodyExtractor.ExtractAsync(payload.Body!, ct);
                if (extraction.Success && extraction.Order is not null)
                {
                    var stubResult = await _orders.CreateStubFromParsedOrderAsync(
                        org.Id,
                        bodySupplierId,
                        extraction.Order,
                        EmailBodyNlpSourceTag,
                        ct);

                    if (stubResult.IsSuccess)
                    {
                        var orderId = stubResult.Value!.Id;
                        created.Add(orderId);
                        _logger.LogInformation(
                            "Inbound email created order {OrderId} for org {OrgId} from email body NLP (confidence={Confidence:F2}).",
                            orderId, org.Id, extraction.Confidence);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Inbound email body extraction for org {OrgId} succeeded but stub creation failed: {Error}",
                            org.Id, stubResult.Error);
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Inbound email body extraction for org {OrgId} did not yield an order (confidence={Confidence:F2}, reason={Reason}).",
                        org.Id, extraction.Confidence, extraction.FailureReason ?? "n/a");
                }
            }
            catch (Exception ex)
            {
                // NLP fallback must never break the webhook — log and continue.
                _logger.LogWarning(ex,
                    "Inbound email body extraction for org {OrgId} threw; treating message as having no orders.",
                    org.Id);
            }
        }
        else if (created.Count == 0 && supplierId is null && !string.IsNullOrWhiteSpace(payload.Body))
        {
            _logger.LogInformation(
                "Inbound email for org {OrgId} produced no order: nothing importable was attached and the "
                + "body-NLP fallback needs a supplier, which this organisation has not configured.",
                org.Id);
        }

        // Key the processed-audit row to the created order when exactly one was created
        // (the dominant single-attachment case) so the per-order GDPR erase removes it.
        // For 0 or multiple orders the row stays message-level (EntityId=Guid.Empty), but
        // it carries no raw sender/subject PII either way (see BuildAuditSummary).
        await WriteAuditAsync(org.Id, "inbound_email.processed",
            payload with { Attachments = payload.Attachments.Select(Strip).ToList() }, ct,
            extra: new { createdOrderIds = created },
            entityId: created.Count == 1 ? created[0] : (Guid?)null);

        return new InboundEmailResult(true, OrgId: orgId, CreatedOrderIds: created, Error: null);
    }

    // ── Tenant resolution ────────────────────────────────────────────────────

    private string? ExtractTenantSlug(string? toEmail)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
            return null;

        var trimmed = toEmail.Trim();

        // Strip a "Name <local@host>" wrapper if present.
        var lt = trimmed.LastIndexOf('<');
        var gt = trimmed.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
            trimmed = trimmed.Substring(lt + 1, gt - lt - 1).Trim();

        var at = trimmed.IndexOf('@');
        // Need a non-empty local part AND something after the '@'.
        if (at <= 0 || at == trimmed.Length - 1)
            return null;

        var local = trimmed[..at];
        var host = trimmed[(at + 1)..].ToLowerInvariant();

        // ── Scheme A: local-part addressing — {slug}@{InboundDomain} ─────────
        // The slug is the mailbox name and the host is one fixed inbound domain
        // (e.g. orders.proculink.eu). This needs only a SINGLE MX record on that
        // domain → Postmark, and avoids a wildcard MX on the marketing apex
        // (*.proculink.eu would otherwise swallow all subdomain mail). Preferred
        // scheme; enabled when Inbound:Postmark:InboundDomain is configured.
        var inboundDomain = GetInboundDomain();
        if (!string.IsNullOrWhiteSpace(inboundDomain) &&
            host.Equals(inboundDomain, StringComparison.OrdinalIgnoreCase))
        {
            return NormaliseLocalPartSlug(local);
        }

        // ── Scheme B: subdomain addressing — orders@{slug}{HostSuffix} ───────
        // The slug is a subdomain label. Requires a wildcard MX (*.proculink.eu).
        // Kept for back-compat with the original addressing scheme.
        var suffix = GetHostSuffix().ToLowerInvariant();
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var slug = host[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(slug) || slug.Contains('.'))
            return null;

        return slug;
    }

    /// <summary>
    /// Normalises a local-part slug: lower-cases it and strips a <c>+tag</c>
    /// (plus-addressing) suffix, so <c>acme+po@orders.proculink.eu</c> still
    /// routes to org <c>acme</c>. Returns null for an empty result.
    /// </summary>
    private static string? NormaliseLocalPartSlug(string local)
    {
        var slug = local.Trim().ToLowerInvariant();
        var plus = slug.IndexOf('+');
        if (plus >= 0)
            slug = slug[..plus];
        // Reject structurally invalid slugs. Org slugs are kebab-case (no dots);
        // this mirrors the subdomain scheme's dot-rejection so both schemes behave
        // consistently and a "user.name@domain" address can't resolve a bogus slug.
        if (string.IsNullOrWhiteSpace(slug) || slug.Contains('.'))
            return null;
        return slug;
    }

    private string GetHostSuffix()
    {
        var configured = _config["Inbound:Postmark:HostSuffix"];
        return string.IsNullOrWhiteSpace(configured) ? DefaultHostSuffix : configured;
    }

    /// <summary>
    /// The single fixed inbound domain for local-part addressing
    /// (<c>{slug}@{InboundDomain}</c>). Empty disables Scheme A and falls back to
    /// the subdomain scheme. Config key <c>Inbound:Postmark:InboundDomain</c>.
    /// </summary>
    private string GetInboundDomain() => _config["Inbound:Postmark:InboundDomain"] ?? string.Empty;

    private async Task<Guid?> ResolveOrgIdFromSlugAsync(string slug, CancellationToken ct)
    {
        // Primary: the org's own unique Slug (auto-generated at creation) — no per-org config.
        var orgId = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Slug == slug)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(ct);
        if (orgId is not null)
            return orgId;

        // Fallback/override: explicit config mapping Inbound:Postmark:TenantMapping:{slug}.
        var raw = _config[$"Inbound:Postmark:TenantMapping:{slug}"];
        return Guid.TryParse(raw, out var id) ? id : (Guid?)null;
    }

    // ── Supplier resolution ──────────────────────────────────────────────────

    private async Task<Guid?> ResolveSupplierIdAsync(Guid orgId, string emailConfigJson, CancellationToken ct)
    {
        // Prefer the IMAP-polling default supplier (same JSONB column) when set.
        var config = EmailPollingConfig.FromJson(emailConfigJson);
        if (config.DefaultSupplierId is { } configured && configured != Guid.Empty)
        {
            var exists = await _db.Suppliers
                .AsNoTracking()
                .AnyAsync(s => s.OrgId == orgId && s.Id == configured && s.DeletedAt == null, ct);
            if (exists)
                return configured;
        }

        // Fall back to the oldest active supplier for the org.
        var fallback = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && s.DeletedAt == null)
            .OrderBy(s => s.CreatedAt)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        return fallback;
    }

    // ── Audit logging ────────────────────────────────────────────────────────

    /// <param name="entityId">
    /// When the audit row concerns a single created order, pass its id so the row is
    /// keyed to the order (<c>EntityId == orderId</c>) and the per-order GDPR erase
    /// path removes it. Message-level rows that map to no single order leave this null
    /// (<c>Guid.Empty</c>) — but the payload still carries no raw sender/subject PII.
    /// </param>
    private async Task WriteAuditAsync(
        Guid orgId,
        string action,
        InboundEmailPayload payload,
        CancellationToken ct,
        object? extra = null,
        Guid? entityId = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(BuildAuditSummary(payload, extra));
            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                UserId = null,
                EntityType = "InboundEmail",
                EntityId = entityId ?? Guid.Empty,
                Action = action,
                Payload = JsonDocument.Parse(json),
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Audit failures must not break ingestion — the order itself is
            // already persisted by the time this fires. Log and move on.
            _logger.LogWarning(ex,
                "Failed to write inbound-email audit event {Action} for org {OrgId}.", action, orgId);
        }
    }

    /// <summary>
    /// Builds the PII-safe audit payload for an inbound email. GDPR: the raw sender
    /// address is stored only as a one-way SHA-256 hash (for correlation/dedup
    /// diagnostics) and the free-text subject line is NOT persisted at all — both are
    /// third-party PII. The recipient (the org's own inbound address), attachment
    /// metadata (file name, type, size) and the caller-supplied <paramref name="extra"/>
    /// are retained. <c>internal</c> so it is unit-testable via InternalsVisibleTo.
    /// </summary>
    internal static object BuildAuditSummary(InboundEmailPayload payload, object? extra) => new
    {
        fromSha256 = Sha256Hex(payload.FromEmail),
        to = payload.ToEmail,
        attachmentCount = payload.Attachments.Count,
        attachments = payload.Attachments.Select(a => new
        {
            fileName = a.FileName,
            contentType = a.ContentType,
            size = a.Content?.Length ?? 0,
        }),
        extra,
    };

    /// <summary>
    /// Lower-case hex SHA-256 of <paramref name="value"/>; null/blank ⇒ null. Used to
    /// pseudonymise the sender address in audit payloads. <c>internal</c> for testing.
    /// </summary>
    internal static string? Sha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static InboundAttachment Strip(InboundAttachment a) =>
        // Drop the byte content from audit payloads — only metadata.
        new(a.FileName, a.ContentType, Array.Empty<byte>());
}
