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
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Infrastructure.Services.Email;

/// <summary>
/// Routes inbound emails (today: Postmark Inbound webhook, tomorrow: SendGrid
/// Inbound Parse) into the existing CreateStub + ParseOrderJob pipeline.
/// </summary>
/// <remarks>
/// A message whose organisation has no supplier is HELD, not rejected: its attachments are
/// imported via <c>IClaimedOrderCreator.CreateClaimedStubAsync</c> with a null supplier and the parse job parks them
/// <c>unrouted</c> for <c>POST /api/orders/{id}/assign-supplier</c> — the same hold the pull
/// channels use. The caller therefore answers 200; a false <c>InboundEmailResult.Success</c>
/// is reserved for messages this product cannot act on: an unparseable recipient, a recipient
/// that is not a live inbound address, a missing organisation, and an organisation whose account
/// status blocks ingest. Each of those also carries an <c>InboundEmailRejectionKind</c>, which is what
/// decides whether the mail provider re-delivers the message — see that enum.
///
/// Tenant resolution runs against <c>org_inbound_addresses</c>: the recipient address is a
/// per-organisation CREDENTIAL, and the organisation follows from the credential presented
/// rather than from anything the sender chose. It used to route on the organisation's public
/// <c>Slug</c>, which meant guessing a slug was enough to file purchase orders into a stranger's
/// inbox — see <c>InboundAddressService</c> for the full account. Two recipient schemes are
/// supported: the preferred <c>{token}@{InboundDomain}</c> (local-part; single
/// MX, set <c>Inbound:Postmark:InboundDomain</c>) and the legacy <c>orders@{token}.proculink.eu</c>
/// (subdomain; needs a wildcard MX). (Live receipt also needs the
/// inbound MX + Postmark domain configured — that is one-time infra, not per-org.)
///
/// Log levels: this runs once per inbound message an org receives, so the expected
/// cases — no attachments, an unsupported attachment (signature images ride along on
/// most mail), body extraction yielding no order — are <c>Debug</c>. API production
/// runs at <c>Default=Information</c>, so those stay out of the log unless someone
/// turns them on; the two attachment cases still write their audit row, so demoting
/// them loses no evidence. <c>Warning</c> is what an operator has to act on: every
/// message-level reject, and an attachment dropped for a content reason (undecodable,
/// empty, oversized, stub creation failed). An order actually created is <c>Information</c>
/// — one line per order, not per message.
///
/// Every attachment skip writes a durable audit row, and each names its own cause:
/// <c>attachment_skipped_unsupported</c>, <c>attachment_skipped_undecodable</c>,
/// <c>attachment_skipped_empty</c>, <c>attachment_skipped_too_large</c>. The rows carry
/// <c>Strip</c>ped attachments — file name, type and size, never bytes. A skip that writes
/// only a log line is invisible to the customer and to the operator both; that is the defect
/// the undecodable and empty branches were fixing.
/// </remarks>
public sealed class InboundEmailRouter : IInboundEmailRouter
{
    /// <summary>
    /// Extensions the router accepts. Pre-filter happens here; if a registered
    /// <c>IPurchaseOrderParser</c> later rejects the file (e.g. EDI parser not
    /// yet wired up), <c>IClaimedOrderCreator.CreateClaimedStubAsync</c> returns a Failure
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

    /// <summary>
    /// Namespace prefix stamped on the provider's message id before it is stored in
    /// <see cref="EmailImportRecord.ImapMessageId"/>. That ledger is shared with the IMAP PULL
    /// channel, whose values are RFC-822 <c>Message-Id</c> headers; a Postmark <c>MessageID</c> is
    /// the provider's own GUID from a different namespace entirely. Prefixing makes it impossible
    /// for the two to alias each other on the (OrgId, message id, content hash) unique index, and
    /// makes a row's channel readable at a glance. The column keeps its <c>imap_message_id</c> name
    /// — renaming it is a cosmetic migration, deliberately not bundled with a correctness fix.
    /// </summary>
    private const string PostmarkClaimPrefix = "postmark:";

    /// <summary>
    /// Marks a claim whose content hash covers the email BODY rather than an attachment, so a
    /// body-NLP order and an attachment that happened to hash identically can never collide.
    /// </summary>
    private const string BodyClaimPrefix = "body:";

    /// <summary>File-name recorded on a body-NLP claim, which has no attachment to name.</summary>
    private const string BodyClaimFileName = "(email body)";

    private readonly ProcuLinkDbContext _db;
    private readonly IClaimedOrderCreator _orders;
    private readonly IParseJobEnqueuer _enqueuer;
    private readonly IEmailBodyOrderExtractor _bodyExtractor;
    private readonly IInboundAddressService _addresses;
    private readonly IConfiguration _config;
    private readonly ILogger<InboundEmailRouter> _logger;

    public InboundEmailRouter(
        ProcuLinkDbContext db,
        IClaimedOrderCreator orders,
        IParseJobEnqueuer enqueuer,
        IEmailBodyOrderExtractor bodyExtractor,
        IInboundAddressService addresses,
        IConfiguration config,
        ILogger<InboundEmailRouter> logger)
    {
        _db = db;
        _orders = orders;
        _enqueuer = enqueuer;
        _bodyExtractor = bodyExtractor;
        _addresses = addresses;
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

        var orgId = lookup.OrgId;

        // ── 2. Load the organisation + verify account-status gate ───────────
        var org = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == orgId.Value)
            .Select(o => new { o.Id, o.AccountStatus, o.EmailConfigJson, o.SelfHostedOcr })
            .FirstOrDefaultAsync(ct);

        if (org is null)
        {
            _logger.LogWarning(
                "Inbound email rejected: inbound address resolved to org {OrgId} but no such organisation exists.",
                orgId.Value);
            return new InboundEmailResult(false, OrgId: orgId, Array.Empty<Guid>(),
                $"Organisation '{orgId.Value}' not found.",
                // Our own inconsistency: an address row outliving its organisation. The foreign key
                // makes it unreachable in practice, but retries give the operator a window in which
                // repairing it lands the order untouched, so this is not the sender's problem to fix.
                InboundEmailRejectionKind.Transient);
        }

        if (BlockedAccountStatuses.Contains(org.AccountStatus))
        {
            _logger.LogWarning(
                "Inbound email for org {OrgId} ignored: account_status={Status} blocks ingest.",
                org.Id, org.AccountStatus);
            await WriteAuditAsync(org.Id, "inbound_email.rejected_read_only", payload, ct);
            return new InboundEmailResult(false, OrgId: orgId, Array.Empty<Guid>(),
                $"Organisation is in '{org.AccountStatus}' status and cannot ingest new orders.",
                // A billing state, not a bad message: lifting it is a founder action of
                // minutes, and Postmark keeps re-delivering for ~10.5 hours before filing
                // the mail under Failed, where it stays re-fireable by hand. Refusing the
                // retries here would turn a reversible freeze into a lost purchase order.
                InboundEmailRejectionKind.Transient);
        }

        // ── 3. Resolve the org's configured default supplier ─────────────────
        // The inbound webhook does not carry the supplier identity, so the ONLY
        // thing that can route it is the supplier the org configured in
        // Settings → Email intake (same JSONB column IMAP polling uses). With
        // none configured the message parks — the router never picks a supplier
        // on the org's behalf. See ResolveSupplierIdAsync for the measurement
        // that removed the old "oldest active supplier" fallback.
        //
        // No supplier at all is NOT a rejection. This used to answer 422 and the
        // message vanished from the product's view. Attachments are now imported
        // through the UNROUTED hold instead — the same path the pull channels use
        // (SftpIngressService / S3IngressService / EmailPollOrgJob) — so the order
        // lands in the inbox parked 'unrouted' and POST /api/orders/{id}/assign-supplier
        // routes and re-parses it. The webhook answers 200; a reject is reserved for
        // messages this product cannot act on (unknown tenant slug, blocked org).
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

            // ── Two ways an attachment arrives with no bytes, and they are not the same event ──
            // Both used to land in one branch that wrote only a log line, while the skip branches
            // on either side of it wrote durable audit rows. The consequence was specific: a buyer
            // emails a purchase order, the webhook answers 200 so the provider never re-delivers,
            // no order is created, and the ONLY trace is a server log the customer cannot see and
            // the operator has no surface for. From every observable position the order simply
            // never existed. Each branch now writes its own row, named for its own cause.
            if (att.Decode == InboundAttachmentDecode.Undecodable)
            {
                // The sender attached SOMETHING; we could not turn the wire encoding into bytes.
                // Nothing here is recoverable by re-delivery — the provider replays the same
                // stored payload, so the same decode fails again — which is why this does not
                // reject the message. The audit row is what makes the loss visible instead.
                _logger.LogWarning(
                    "Inbound email attachment {FileName} for org {OrgId} could not be decoded from its wire encoding — skipping.",
                    att.FileName, org.Id);
                await WriteAuditAsync(
                    org.Id,
                    "inbound_email.attachment_skipped_undecodable",
                    payload with { Attachments = new[] { Strip(att) } },
                    ct);
                continue;
            }

            if (att.Content is null || att.Content.Length == 0)
            {
                // Decoded cleanly to nothing: the sender really did attach an empty file.
                _logger.LogWarning(
                    "Inbound email attachment {FileName} for org {OrgId} has empty content — skipping.",
                    att.FileName, org.Id);
                await WriteAuditAsync(
                    org.Id,
                    "inbound_email.attachment_skipped_empty",
                    payload with { Attachments = new[] { Strip(att) } },
                    ct);
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

            // ── CLAIM-FIRST dedupe ───────────────────────────────────────────
            // Same contract as the three PULL channels (see IngressDedupe): the ledger row is
            // committed BEFORE the order is created, and the unique index — not the existence
            // check — is the real guard. This is what stops a Postmark retry (any non-200 is
            // re-delivered ten times over ~10.5 hours) from turning one email into N orders and
            // N supplier deliveries.
            var claimKey = ClaimKeyFor(payload.ProviderMessageId);
            var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(att.Content));

            var claim = await ClaimAsync(org.Id, claimKey, contentHash, att.FileName, ct);
            if (claim is null)
            {
                // Either already imported, or a concurrent delivery holds the claim. Both mean
                // "nothing new to do" — never a second order.
                _logger.LogInformation(
                    "Inbound email attachment {FileName} for org {OrgId} already claimed (message {MessageId}); skipping duplicate.",
                    att.FileName, org.Id, claimKey);
                continue;
            }

            var orderId = claim.OrderId;

            // CreateClaimedStubAsync uploads to R2 and creates the stub under the claim's
            // pre-generated id — a find-or-create on that primary key, so a resume after a
            // transient failure can never produce a second order. With no supplier we pass null:
            // same upload, NULL supplier_id, and the parse job parks the order 'unrouted'.
            var stubResult = await _orders.CreateClaimedStubAsync(
                org.Id, supplierId, orderId, ms, att.FileName ?? "attachment", contentType,
                ExtractSenderDomain(payload.FromEmail), ct);

            if (!stubResult.IsSuccess)
            {
                // Result.Fail is a PERMANENT content error (empty/unsupported): mark the claim
                // terminal so a genuinely-bad attachment is bounded rather than retried on every
                // re-delivery. A TRANSIENT infra failure THROWS instead and propagates, leaving
                // the claim holding its real OrderId with no order — which the next delivery
                // RESUMES. That asymmetry is the whole no-lost-order guarantee.
                _logger.LogWarning(
                    "Inbound email attachment {FileName} for org {OrgId} could not create order stub; marking claim terminal: {Error}",
                    att.FileName, org.Id, stubResult.Error);
                claim.OrderId = IngressDedupe.TerminalOrderId;
                await _db.SaveChangesAsync(ct);
                continue;
            }

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
        // A supplier is NOT required. With none resolvable the extracted order takes the unrouted
        // sibling, exactly as the attachment path above passes a null supplier: the order is
        // parked for a human to route rather than dropped. Do not fabricate a supplier id here —
        // the null is the signal that routing is still owed.
        if (created.Count == 0
            && !string.IsNullOrWhiteSpace(payload.Body)
            && !org.SelfHostedOcr)
        {
            try
            {
                var extraction = await _bodyExtractor.ExtractAsync(payload.Body!, ct);
                if (extraction.Success && extraction.Order is not null)
                {
                    // The body path creates orders too, so it needs the same claim — otherwise a
                    // replayed body-only email produces one order per re-delivery. The hash covers
                    // the BODY text (prefixed, so it cannot alias an attachment hash), which also
                    // deduplicates a provider that supplies no message id at all.
                    var bodyClaimKey = ClaimKeyFor(payload.ProviderMessageId);
                    var bodyHash = BodyClaimPrefix + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(payload.Body!)));

                    var bodyClaim = await ClaimAsync(org.Id, bodyClaimKey, bodyHash, BodyClaimFileName, ct);
                    if (bodyClaim is null)
                    {
                        // Already imported, or a concurrent delivery holds the claim. Fall through
                        // to the processed-audit row below — the message IS handled, there is just
                        // nothing new to create.
                        _logger.LogInformation(
                            "Inbound email body for org {OrgId} already claimed (message {MessageId}); skipping duplicate.",
                            org.Id, bodyClaimKey);
                    }
                    else
                    {
                        var stubResult = await _orders.CreateClaimedFromParsedOrderAsync(
                            org.Id, supplierId, bodyClaim.OrderId, extraction.Order, EmailBodyNlpSourceTag,
                            ExtractSenderDomain(payload.FromEmail), ct);

                        if (stubResult.IsSuccess)
                        {
                            var orderId = stubResult.Value!.Id;
                            created.Add(orderId);
                            _logger.LogInformation(
                                "Inbound email created {Mode} order {OrderId} for org {OrgId} from email body NLP (confidence={Confidence:F2}).",
                                supplierId is null ? "unrouted" : "routed", orderId, org.Id, extraction.Confidence);
                        }
                        else
                        {
                            // Permanent content failure — bound the claim so every later
                            // re-delivery skips it instead of re-running the extractor and the
                            // same failure.
                            _logger.LogWarning(
                                "Inbound email body extraction for org {OrgId} succeeded but stub creation failed; marking claim terminal: {Error}",
                                org.Id, stubResult.Error);
                            bodyClaim.OrderId = IngressDedupe.TerminalOrderId;
                            await _db.SaveChangesAsync(ct);
                        }
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

    // ── Claim-first dedupe ───────────────────────────────────────────────────

    /// <summary>
    /// The ledger key for this delivery: the provider's message id, namespaced so it cannot alias an
    /// IMAP <c>Message-Id</c> in the shared table. A provider that supplies no id yields just the
    /// prefix, and the content hash then carries the dedupe on its own (the same fallback the IMAP
    /// poller uses for a server that omits the header).
    /// </summary>
    private static string ClaimKeyFor(string? providerMessageId) =>
        PostmarkClaimPrefix + (providerMessageId?.Trim() ?? string.Empty);

    /// <summary>
    /// Claims (OrgId, <paramref name="claimKey"/>, <paramref name="contentHash"/>) for import,
    /// committing the ledger row BEFORE any order exists. Returns the claim to create under, or
    /// <c>null</c> when this content must NOT be imported again.
    ///
    /// <para>Three outcomes, matching <see cref="IngressDedupe"/>'s contract exactly:</para>
    /// <list type="bullet">
    /// <item><description>no row → insert a claim carrying a fresh pre-generated order id and
    /// return it (CREATE). A concurrent delivery that also got here loses the unique-index race,
    /// catches 23505 and returns null (SKIP) — it must not resume, the winner is mid-flight;</description></item>
    /// <item><description>a row whose claim is SATISFIED (terminal sentinel, or its order already
    /// exists) → null (SKIP): a true duplicate, including the "order committed but a later step
    /// crashed" case;</description></item>
    /// <item><description>a row whose order does NOT exist → return it (RESUME): a transient failure
    /// abandoned it, so recreate under the same id. Without this branch an "any existing claim means
    /// skip" policy would mark the message seen forever and the purchase order would be silently
    /// LOST — worse than the duplicate the claim prevents.</description></item>
    /// </list>
    /// The returned entity is TRACKED by <c>_db</c>, so the caller can mark it terminal on a
    /// permanent failure and have <c>SaveChangesAsync</c> persist it.
    /// </summary>
    private async Task<EmailImportRecord?> ClaimAsync(
        Guid orgId, string claimKey, string contentHash, string? fileName, CancellationToken ct)
    {
        var existing = await _db.EmailImportRecords.FirstOrDefaultAsync(
            r => r.OrgId == orgId && r.ImapMessageId == claimKey && r.AttachmentHash == contentHash, ct);

        if (existing is not null)
        {
            return await IngressDedupe.ClaimSatisfiedAsync(_db, orgId, existing.OrderId, ct)
                ? null      // SKIP — already imported, or deliberately bounded.
                : existing; // RESUME — the order was never created.
        }

        var record = new EmailImportRecord
        {
            Id             = Guid.NewGuid(),
            OrgId          = orgId,
            ImapMessageId  = claimKey,
            AttachmentHash = contentHash,
            // Pre-generated and stored ATOMICALLY with the claim insert — there is no separate
            // "backfill the id" step whose failure could open a window where the claim exists but
            // points nowhere.
            OrderId        = Guid.NewGuid(),
            FileName       = fileName,
            ImportedAt     = DateTime.UtcNow,
        };
        _db.EmailImportRecords.Add(record);

        try
        {
            await _db.SaveChangesAsync(ct);
            return record;
        }
        catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
        {
            // A concurrent delivery of the same message won the claim. Detach so this context is
            // usable again, and SKIP — the winner owns the order.
            _db.Entry(record).State = EntityState.Detached;
            return null;
        }
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

    // ── Supplier resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves the org's configured Email-intake default supplier (the <c>defaultSupplierId</c>
    /// in the same <c>email_config</c> JSONB column IMAP polling uses), or null when the message
    /// must be imported unrouted. Org-scoped and not-deleted, so a wrong-org or soft-deleted id
    /// resolves to unrouted rather than cross-tenant — the same contract as the three
    /// pull-ingress resolvers (<c>SftpIngressService</c>, <c>S3IngressService</c>,
    /// <c>EmailPollOrgJob</c>), each of which returns null on exactly these conditions.
    /// <para>
    /// There is deliberately NO fallback to "the org's only, or oldest, supplier". An inbound
    /// message carries no supplier identity of its own, so any such pick is a guess, and a
    /// guessed routing is indistinguishable in the product from one an operator chose.
    /// Measured on production 2026-07-26 (finding F1 of
    /// <c>docs/qa/2026-07-fable5-push/2026-07-25-routing-matrix-live-proof.md</c>): with the
    /// default cleared, an emailed purchase order was attributed to the oldest active supplier
    /// — a counterparty nobody had chosen — and reached <c>pending_review</c> as a normal,
    /// actionable order with no audit row recording that it had been guessed. Returning null
    /// parks it <c>unrouted</c> instead, where assign-supplier resolves it.
    /// </para>
    /// </summary>
    private async Task<Guid?> ResolveSupplierIdAsync(Guid orgId, string emailConfigJson, CancellationToken ct)
    {
        var config = EmailPollingConfig.FromJson(emailConfigJson);
        if (config.DefaultSupplierId is not { } configured || configured == Guid.Empty)
        {
            return null;
        }

        var exists = await _db.Suppliers
            .AsNoTracking()
            .AnyAsync(s => s.OrgId == orgId && s.Id == configured && s.DeletedAt == null, ct);

        return exists ? configured : null;
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

    /// <summary>
    /// Drops the byte content from an attachment so an audit payload carries only metadata.
    /// </summary>
    /// <remarks>
    /// A <c>with</c> expression rather than a positional rebuild: the positional form named three
    /// members explicitly and would have SILENTLY dropped every member added to the record after
    /// it was written — which is exactly what would have happened to <c>Decode</c>. Copy
    /// everything, then blank the one field that must not travel.
    /// </remarks>
    private static InboundAttachment Strip(InboundAttachment a) =>
        a with { Content = Array.Empty<byte>() };
}
