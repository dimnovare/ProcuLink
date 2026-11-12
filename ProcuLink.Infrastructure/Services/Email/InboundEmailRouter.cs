using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Infrastructure.Services.Email;

/// <summary>
/// Routes inbound emails (Postmark Inbound webhook) to the flow whose own address received them.
/// </summary>
/// <remarks>
/// Tenant resolution runs against <c>org_inbound_addresses</c>: the recipient address is a
/// per-organisation CREDENTIAL, and the organisation follows from the credential presented
/// rather than from anything the sender chose — see <c>InboundAddressService</c>. Two recipient
/// schemes are supported: the preferred <c>{token}@{InboundDomain}</c> (local-part; set
/// <c>Inbound:Postmark:InboundDomain</c>) and the legacy <c>orders@{token}.proculink.eu</c>
/// (subdomain). Only a flow's own address is acted on; an organisation-level address no longer
/// receives anything and is answered Transient, so the provider keeps the mail re-fireable.
/// </remarks>
public sealed class InboundEmailRouter : IInboundEmailRouter
{
    /// <summary>
    /// Default host suffix used when parsing the recipient address. The full
    /// recipient is <c>orders@{slug}.proculink.eu</c>; if the founder hosts
    /// the inbound MX on a different domain (e.g. <c>inbound.proculink.eu</c>),
    /// override via config <c>Inbound:Postmark:HostSuffix</c>.
    /// </summary>
    private const string DefaultHostSuffix = ".proculink.eu";

    private readonly ProcuLinkDbContext _db;
    private readonly IInboundAddressService _addresses;
    private readonly IBillingService _billing;
    private readonly IConfiguration _config;
    private readonly IFlowDocumentSubmitter? _flows;
    private readonly ILogger<InboundEmailRouter> _logger;

    public InboundEmailRouter(
        ProcuLinkDbContext db,
        IInboundAddressService addresses,
        // Required, never optional: a default would let a test — or a second host's DI — construct
        // a router whose plan and volume gates silently do not run, which is the state this class
        // shipped in for months.
        IBillingService billing,
        IConfiguration config,
        ILogger<InboundEmailRouter> logger,
        // Registered in both hosts that run the router. Optional only so non-flow callers need not
        // supply it; a flow address with none defers (Transient) rather than dropping the mail.
        IFlowDocumentSubmitter? flows = null)
    {
        _flows = flows;
        _db = db;
        _addresses = addresses;
        _billing = billing;
        _config = config;
        _logger = logger;
    }

    public async Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct)
    {
        // ── 1. Resolve the tenant from the recipient address ─────────────────
        // The recipient address IS the credential — see InboundAddressService for why the mail
        // channel leaves no other place to put one. The organisation therefore follows from what
        // was presented; nothing the sender writes anywhere else in the message can name a tenant.
        var addressToken = ExtractAddressToken(payload.ToEmail);
        if (addressToken is null)
        {
            _logger.LogWarning(
                "Inbound email rejected: recipient address {To} is not shaped like an inbound ProcuLink address.",
                payload.ToEmail);
            return new InboundEmailResult(false, OrgId: null, Array.Empty<Guid>(),
                $"Recipient '{payload.ToEmail}' does not look like an inbound ProcuLink address.",
                InboundEmailRejectionKind.Permanent);
        }

        var lookup = await _addresses.ResolveAsync(addressToken, ct);
        switch (lookup.Status)
        {
            // The org id is destructured in the guard, so a "Resolved" that carries no organisation
            // — an impossible construction today, and exactly the kind of thing a later edit
            // introduces — falls through to the refusing branch instead of dereferencing null.
            case InboundAddressLookupStatus.Resolved when lookup.OrgId is { } resolved && resolved != Guid.Empty:
                break;

            case InboundAddressLookupStatus.Unavailable:
                // We cannot recognise ANY address right now, so this says nothing about this
                // message. Transient keeps the provider re-delivering while the misconfiguration is
                // fixed; calling it Permanent here would settle real purchase orders as handled and
                // lose them.
                _logger.LogError(
                    "Inbound email deferred: the inbound-address lookup is unavailable, so no tenant " +
                    "can be resolved. The message is being retried, not dropped.");
                return new InboundEmailResult(false, OrgId: null, Array.Empty<Guid>(),
                    "Inbound address lookup is temporarily unavailable.",
                    InboundEmailRejectionKind.Transient);

            default:
                // NotFound — and anything a future edit adds without thinking, because an
                // unrecognised status must refuse rather than fall through to the accepting branch.
                // The address is never echoed: it is a credential, the log is not a place to leak
                // one, and this repository is public.
                _logger.LogWarning(
                    "Inbound email rejected: recipient address is not a live inbound address for any " +
                    "organisation (unissued, revoked, or expired).");
                return new InboundEmailResult(false, OrgId: null, Array.Empty<Guid>(),
                    "Recipient is not a recognised inbound address.",
                    InboundEmailRejectionKind.Permanent);
        }

        if (lookup.FlowId is { } flowId)
            return await RouteToFlowAsync(payload, lookup.OrgId!.Value, flowId, ct);

        // An organisation-level address no longer receives anything: documents arrive at a flow's own
        // address. Transient, never Permanent: a Permanent answer settles the mail as handled, which
        // here would be silent loss — Transient keeps it in Postmark's re-delivery window and then its
        // Failed list, re-fireable by hand.
        var orgId = lookup.OrgId!.Value;
        _logger.LogInformation("Inbound email for org {OrgId} refused: the address is not a flow's address.", orgId);
        await WriteAuditAsync(orgId, "inbound_email.rejected_orders_retired", payload, ct);
        return new InboundEmailResult(false, OrgId: orgId, Array.Empty<Guid>(),
            "Organisation now receives documents through flows.",
            InboundEmailRejectionKind.Transient);
    }

    /// <summary>Mail sent to one flow's own address: each attachment the flow accepts becomes a document in that flow.</summary>
    private async Task<InboundEmailResult> RouteToFlowAsync(
        InboundEmailPayload payload, Guid orgId, Guid flowId, CancellationToken ct)
    {
        InboundEmailResult Ignored(string why) =>
            new(false, orgId, Array.Empty<Guid>(), why, InboundEmailRejectionKind.Permanent);

        if (_flows is null)
            return new InboundEmailResult(false, orgId, Array.Empty<Guid>(),
                "Flow intake is not available on this host.", InboundEmailRejectionKind.Transient);

        var intake = await _db.FlowIntakes
            .Where(i => i.OrgId == orgId && i.FlowId == flowId && i.Kind == IntakeKind.Email)
            .FirstOrDefaultAsync(ct);
        if (intake is not { IsEnabled: true })
            return Ignored("This flow's email intake is off.");

        async Task<InboundEmailResult> PausedAsync(LimitCheckResult limit)
        {
            intake.LastStatus = "paused";
            intake.LastMessage = limit.FlowPausedMessage;
            intake.LastRunAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ignored(intake.LastMessage);
        }

        var limit = await _billing.CheckOrderLimitAsync(orgId, ct);
        if (!limit.Allowed) return await PausedAsync(limit);

        var usable = 0;
        var accepted = _flows.AcceptedFormats(orgId);
        var messageKey = $"postmark:{payload.ProviderMessageId ?? Guid.NewGuid().ToString("N")}";
        var files = payload.Attachments.Where(a =>
                accepted.Contains(Path.GetExtension(a.FileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            .Select(a => (a.FileName, a.ContentType, a.Content,
                Key: $"{messageKey}#{Convert.ToHexString(SHA256.HashData(a.Content)).ToLowerInvariant()}"))
            .ToList();
        // Nothing was submitted, so the body is the document, when the org reads text files.
        if (files.Count == 0 && accepted.Contains("txt") && !string.IsNullOrWhiteSpace(payload.Body))
            files.Add(("email.txt", "text/plain", Encoding.UTF8.GetBytes(payload.Body), $"{messageKey}#body"));
        foreach (var att in files)
        {
            usable++;
            var key = att.Key;
            var claim = await IntakeLedger.ClaimAsync(_db, orgId, intake.Id, key, ct);
            if (claim is null)
            {
                // Only a settled row (a document or a refusal) is done. Anything else is in flight, was released a
                // moment ago, or was left by a crash: defer, so Postmark re-delivers until it settles or is retaken.
                var itemKey = IntakeLedger.KeyFor(key);
                if (!await _db.FlowIntakeItems.AnyAsync(i => i.OrgId == orgId && i.ScopeId == intake.Id
                                                             && i.ItemKey == itemKey && i.DocumentId != null, ct))
                    return new InboundEmailResult(false, orgId, Array.Empty<Guid>(),
                        "An earlier delivery of this message is still being taken in.", InboundEmailRejectionKind.Transient);
                continue;
            }

            FlowSubmitResult result;
            try
            {
                result = await _flows.SubmitAsync(
                    orgId, flowId, new MemoryStream(att.Content), att.FileName, att.ContentType, IntakeKind.Email,
                    payload.FromEmail, ct);
            }
            catch
            {
                // SubmitAsync throws only before the document commits, so the claim goes back for the re-delivery.
                await IntakeLedger.ReleaseAsync(_db, orgId, claim.Id, CancellationToken.None);
                throw;
            }
            if (result.Error is "document_engine_not_enabled" or "flow_not_found")
                return Ignored(result.Error);
            if (result.Limit is { Allowed: false } refused) return await PausedAsync(refused);

            var documentId = result.Document?.Id ?? IntakeLedger.Refused;
            await _db.FlowIntakeItems
                .Where(i => i.OrgId == orgId && i.Id == claim.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DocumentId, documentId), CancellationToken.None);
        }

        intake.LastRunAt = DateTime.UtcNow;
        intake.LastStatus = "ok";
        intake.LastMessage = usable == 0
            ? $"An email from {payload.FromEmail} had no file we can read attached."
            : null;
        await _db.SaveChangesAsync(ct);
        return new InboundEmailResult(true, orgId, Array.Empty<Guid>(), null);
    }

    // ── Tenant resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the address TOKEN out of a recipient address — the part that identifies the tenant,
    /// under either addressing scheme. Purely syntactic: it decides what was presented, never
    /// whether it is valid. <c>IInboundAddressService.ResolveAsync</c> owns that, and is the only
    /// thing that can turn a token into an organisation.
    /// </summary>
    private string? ExtractAddressToken(string? toEmail)
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

        // ── Scheme A: local-part addressing — {token}@{InboundDomain} ────────
        // The token is the mailbox name and the host is one fixed inbound domain
        // (e.g. orders.proculink.eu). This needs only a SINGLE MX record on that
        // domain → Postmark, and avoids a wildcard MX on the marketing apex
        // (*.proculink.eu would otherwise swallow all subdomain mail). Preferred
        // scheme; enabled when Inbound:Postmark:InboundDomain is configured.
        var inboundDomain = GetInboundDomain();
        if (!string.IsNullOrWhiteSpace(inboundDomain) &&
            host.Equals(inboundDomain, StringComparison.OrdinalIgnoreCase))
        {
            return NormaliseAddressToken(local);
        }

        // ── Scheme B: subdomain addressing — orders@{token}{HostSuffix} ──────
        // The token is a subdomain label. Requires a wildcard MX (*.proculink.eu).
        // Kept for back-compat with the original addressing scheme.
        var suffix = GetHostSuffix().ToLowerInvariant();
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = host[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(token) || token.Contains('.'))
            return null;

        return token;
    }

    /// <summary>
    /// Normalises a local-part token: lower-cases it and strips a <c>+tag</c>
    /// (plus-addressing) suffix, so <c>{token}+po@orders.proculink.eu</c> still
    /// reaches the same organisation. Returns null for an empty result.
    /// </summary>
    /// <remarks>
    /// Lower-casing is why minted tokens use a case-insensitive alphabet — see
    /// <c>InboundAddressService.NewToken</c>. A case-sensitive token would lose entropy here.
    /// </remarks>
    private static string? NormaliseAddressToken(string local)
    {
        var token = local.Trim().ToLowerInvariant();
        var plus = token.IndexOf('+');
        if (plus >= 0)
            token = token[..plus];
        // Reject structurally invalid tokens. Minted tokens are hex and legacy slugs are kebab-case,
        // neither of which contains a dot; this mirrors the subdomain scheme's dot-rejection so both
        // schemes behave consistently and a "user.name@domain" address can't resolve at all.
        if (string.IsNullOrWhiteSpace(token) || token.Contains('.'))
            return null;
        return token;
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

    // NOTE: there is deliberately no ResolveOrgIdFromSlugAsync here any more, and no
    // Inbound:Postmark:TenantMapping:{slug} configuration fallback.
    //
    // Both were ways for a caller-supplied string to name an organisation directly: the first via
    // the organisation's public Slug column, the second via a config key whose NAME was the
    // caller's own string. Tenant selection now has exactly one door — a hashed lookup against
    // org_inbound_addresses — so there is nowhere left for an unrecognised address to fall through
    // to a real organisation. If a new resolution path is ever added, it belongs behind
    // IInboundAddressService, not beside it.

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
    /// The DOMAIN part of an inbound sender address — "orders@acme.example" ⇒ "acme.example". Returns null
    /// for anything that is not clearly a domain (no "@", nothing after it, no dot).
    ///
    /// <para>This method IS the privacy boundary for founder ruling D2. The local part — the half
    /// that identifies a PERSON — is dropped here and never reaches the order row; the full address
    /// keeps its existing treatment, a one-way SHA-256 in the audit payload and nothing else. What
    /// is persisted is the counterparty organisation the mail came from, which is the routing
    /// evidence a supplier-less order is missing, and it is scrubbed after 12 months by the
    /// data-retention sweep. <c>internal</c> for testing.</para>
    /// </summary>
    internal static string? ExtractSenderDomain(string? fromEmail)
    {
        if (string.IsNullOrWhiteSpace(fromEmail)) return null;

        var at = fromEmail.LastIndexOf('@');
        if (at < 0) return null;

        // A From header may arrive as a display-name form — `"Acme Orders" <orders@acme.example>` —
        // so keep only the leading run of characters that can legally appear in a host name and
        // drop the closing bracket and anything after it.
        var tail = fromEmail[(at + 1)..].Trim();
        var end = 0;
        while (end < tail.Length && (char.IsLetterOrDigit(tail[end]) || tail[end] is '-' or '.')) end++;

        var domain = ProcuLink.Core.Services.Detection.SupplierSuggestionScoring
            .NormalizeDomain(tail[..end]);

        // A domain with no dot is a local/host name, not something two organisations could share
        // a match on — better to store nothing than a value that can only ever be noise.
        return domain is not null && domain.Contains('.') ? domain : null;
    }

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
}
