using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services.Email;

/// <summary>
/// Routes inbound emails (today: Postmark Inbound webhook, tomorrow: SendGrid
/// Inbound Parse) into the existing CreateStub + ParseOrderJob pipeline.
/// </summary>
/// <remarks>
/// Today's tenant resolution is config-driven: <c>Inbound:Postmark:TenantMapping:{slug}</c>
/// → org-id GUID. This is a deliberate workaround until the founder adds an
/// <c>OrgSlug</c> column to the <c>organisations</c> table. Once that column
/// exists, the resolver can replace the config lookup with a DB query without
/// touching the controller or the interface contract.
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
        ".edi",
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
    /// recipient is <c>orders@{slug}.proculink.app</c>; if the founder hosts
    /// the inbound MX on a different domain (e.g. <c>inbound.proculink.app</c>),
    /// override via config <c>Inbound:Postmark:HostSuffix</c>.
    /// </summary>
    private const string DefaultHostSuffix = ".proculink.app";

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

        var orgId = ResolveOrgIdFromSlug(slug);
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
            .Select(o => new { o.Id, o.AccountStatus, o.EmailConfigJson })
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
            _logger.LogInformation(
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
        var supplierId = await ResolveSupplierIdAsync(org.Id, org.EmailConfigJson, ct);
        if (supplierId is null)
        {
            _logger.LogWarning(
                "Inbound email for org {OrgId} rejected: organisation has no supplier configured for inbound email.",
                org.Id);
            await WriteAuditAsync(org.Id, "inbound_email.rejected_no_supplier", payload, ct);
            return new InboundEmailResult(false, OrgId: orgId, Array.Empty<Guid>(),
                "Organisation has no supplier configured for inbound email ingestion.");
        }

        // ── 4. Filter attachments ────────────────────────────────────────────
        // Note: an empty attachment list is no longer an early-return — the
        // body-NLP fallback below may still produce an order from prose text.
        if (payload.Attachments.Count == 0)
        {
            _logger.LogInformation(
                "Inbound email for org {OrgId} carried no attachments; will try email-body NLP fallback if a body is present.",
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
                _logger.LogInformation(
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

            await using var ms = new MemoryStream(att.Content, writable: false);
            var contentType = string.IsNullOrWhiteSpace(att.ContentType)
                ? "application/octet-stream"
                : att.ContentType;

            // IOrderService.CreateStubAsync uploads to R2 and creates the stub.
            // It is the same call browser-upload and IMAP-poll use today.
            var stubResult = await _orders.CreateStubAsync(
                org.Id, supplierId.Value, ms, att.FileName ?? "attachment", contentType, ct);

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
                "Inbound email created order {OrderId} for org {OrgId} (attachment={FileName}).",
                orderId, org.Id, att.FileName);
        }

        // ── 6. Email-body NLP fallback ───────────────────────────────────────
        // When no attachment produced an order — either nothing was attached or
        // every attachment was unsupported / empty / rejected — fall back to
        // extracting a purchase order from the email body itself. The extractor
        // is a no-op without an OpenAI key, so this is safe in unit tests and
        // local dev where the body field is set but the AI provider is absent.
        if (created.Count == 0 && !string.IsNullOrWhiteSpace(payload.Body))
        {
            try
            {
                var extraction = await _bodyExtractor.ExtractAsync(payload.Body!, ct);
                if (extraction.Success && extraction.Order is not null)
                {
                    var stubResult = await _orders.CreateStubFromParsedOrderAsync(
                        org.Id,
                        supplierId.Value,
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
                    _logger.LogInformation(
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

        await WriteAuditAsync(org.Id, "inbound_email.processed",
            payload with { Attachments = payload.Attachments.Select(Strip).ToList() }, ct,
            extra: new { createdOrderIds = created });

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
        if (at < 0 || at == trimmed.Length - 1)
            return null;

        var host = trimmed[(at + 1)..].ToLowerInvariant();
        var suffix = GetHostSuffix().ToLowerInvariant();

        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var slug = host[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(slug) || slug.Contains('.'))
            return null;

        return slug;
    }

    private string GetHostSuffix()
    {
        var configured = _config["Inbound:Postmark:HostSuffix"];
        return string.IsNullOrWhiteSpace(configured) ? DefaultHostSuffix : configured;
    }

    private Guid? ResolveOrgIdFromSlug(string slug)
    {
        // Until OrgSlug is added to the organisations table, we rely on a
        // configurable mapping: Inbound:Postmark:TenantMapping:{slug} = "<guid>".
        var raw = _config[$"Inbound:Postmark:TenantMapping:{slug}"];
        if (string.IsNullOrWhiteSpace(raw))
            return null;
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

    private async Task WriteAuditAsync(
        Guid orgId,
        string action,
        InboundEmailPayload payload,
        CancellationToken ct,
        object? extra = null)
    {
        try
        {
            var summary = new
            {
                from = payload.FromEmail,
                to = payload.ToEmail,
                subject = payload.Subject,
                attachmentCount = payload.Attachments.Count,
                attachments = payload.Attachments.Select(a => new
                {
                    fileName = a.FileName,
                    contentType = a.ContentType,
                    size = a.Content?.Length ?? 0,
                }),
                extra,
            };

            var json = JsonSerializer.Serialize(summary);
            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                UserId = null,
                EntityType = "InboundEmail",
                EntityId = Guid.Empty,
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

    private static InboundAttachment Strip(InboundAttachment a) =>
        // Drop the byte content from audit payloads — only metadata.
        new(a.FileName, a.ContentType, Array.Empty<byte>());
}
