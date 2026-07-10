using System.Security.Cryptography;
using Hangfire;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Per-organisation child job: opens the IMAP connection, imports unseen attachments,
/// and enqueues parse jobs for one organisation. Scheduled by <see cref="EmailPollingJob"/>.
/// Attachments are routed to the configured default supplier via
/// <see cref="IOrderService.CreateStubAsync"/>, or imported UNROUTED via
/// <see cref="IOrderService.CreateUnroutedStubAsync"/> when no valid default supplier is
/// configured (the parse job parks them 'unrouted' for later supplier assignment).
/// </summary>
public sealed class EmailPollOrgJob
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".xlsx",
        ".pdf"
    };

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IOrderService _orders;
    private readonly IBackgroundJobClient _jobs;
    private readonly IBillingService _billing;
    private readonly IEmailSettingsService _emailSettings;
    private readonly OutboundRequestGuard _guard;
    private readonly ILogger<EmailPollOrgJob> _logger;

    public EmailPollOrgJob(
        ProcuLinkDbContext db,
        DeliveryEncryptionService encryption,
        IOrderService orders,
        IBackgroundJobClient jobs,
        IBillingService billing,
        IEmailSettingsService emailSettings,
        OutboundRequestGuard guard,
        ILogger<EmailPollOrgJob> logger)
    {
        _db = db;
        _encryption = encryption;
        _orders = orders;
        _jobs = jobs;
        _billing = billing;
        _emailSettings = emailSettings;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>
    /// Performs the IMAP poll for a single organisation. Idempotent: relies on
    /// the SEEN flag set on each processed message; re-running after a crash
    /// will skip messages already flagged.
    /// </summary>
    [Queue("polling")]
    [AutomaticRetry(Attempts = 2)]
    public async Task ExecuteAsync(Guid orgId, CancellationToken ct)
    {
        // Re-read config inside the child job so the child is self-contained.
        var org = await _db.Organisations
            .AsNoTracking()
            .Where(x => x.Id == orgId)
            .Select(x => new { x.Id, x.EmailConfigJson })
            .FirstOrDefaultAsync(ct);

        if (org is null)
        {
            _logger.LogWarning("EmailPollOrgJob: org {OrgId} not found.", orgId);
            return;
        }

        var config = EmailPollingConfig.FromJson(org.EmailConfigJson);

        if (!config.Enabled)
        {
            _logger.LogInformation("EmailPollOrgJob: polling disabled for org {OrgId}.", orgId);
            return;
        }

        if (!await _billing.HasFeatureAsync(orgId, BillingFeature.EmailIngestion, ct))
        {
            _logger.LogInformation("EmailPollOrgJob: plan does not include email ingestion for org {OrgId}.", orgId);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Host)
            || string.IsNullOrWhiteSpace(config.Username))
        {
            _logger.LogWarning("EmailPollOrgJob: incomplete config (host/username missing) for org {OrgId}.", orgId);
            return;
        }

        // Phase 1b routing: a missing (or stale/soft-deleted) default supplier no longer
        // blocks the poll. Attachments are imported UNROUTED (SupplierId = null) via
        // CreateUnroutedStubAsync — the parse job parks them 'unrouted' and
        // POST /api/orders/{id}/assign-supplier routes + re-parses them later.
        var supplierId = await ResolveDefaultSupplierIdAsync(orgId, config.DefaultSupplierId, ct);
        if (supplierId is null)
        {
            if (config.DefaultSupplierId is null)
            {
                _logger.LogInformation(
                    "EmailPollOrgJob: org {OrgId} has no default supplier configured — new attachments will be imported unrouted.",
                    orgId);
            }
            else
            {
                _logger.LogWarning(
                    "EmailPollOrgJob: org {OrgId} default supplier {SupplierId} no longer exists — new attachments will be imported unrouted.",
                    orgId, config.DefaultSupplierId);
            }
        }

        var password = string.IsNullOrWhiteSpace(config.PasswordCiphertext)
            ? string.Empty
            : _encryption.Decrypt(config.PasswordCiphertext);

        if (password is null)
        {
            _logger.LogWarning("EmailPollOrgJob: IMAP password could not be decrypted for org {OrgId}.", orgId);
            return;
        }

        // ── SSRF guard IMMEDIATELY before connect (SEC-1) ────────────────────
        // The IMAP host is tenant-configured; without this an org could point it at an
        // internal/metadata host. Same shared guard the delivery dispatchers + the SFTP/S3
        // pollers use. Residual L1 (DNS-rebind TOCTOU between this resolve and MailKit's own
        // resolve at connect) is the documented accepted risk shared with the other pull paths.
        var guardResult = await _guard.ValidateHostAsync(config.Host, config.Port, ct);
        if (!guardResult.Allowed)
        {
            _logger.LogWarning(
                "EmailPollOrgJob: org {OrgId} — IMAP host blocked by SSRF guard, skipping poll. {Reason}",
                orgId, guardResult.Reason);
            return;
        }

        _logger.LogInformation(
            "EmailPollOrgJob: polling IMAP for org {OrgId}: {User}@{Host}:{Port} folder={Folder}",
            orgId, config.Username, config.Host, config.Port, config.Folder);

        using var client = new ImapClient();
        await client.ConnectAsync(
            config.Host,
            config.Port,
            config.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
            ct);
        await client.AuthenticateAsync(config.Username, password, ct);

        var folder = await client.GetFolderAsync(config.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);

        var unseen = await folder.SearchAsync(SearchQuery.NotSeen, ct);
        _logger.LogInformation("EmailPollOrgJob org {OrgId}: {UnseenCount} unseen message(s) in {Folder}.", orgId, unseen.Count, config.Folder);

        var queued = 0;
        foreach (var uid in unseen)
        {
            var message = await folder.GetMessageAsync(uid, ct);
            var processed = await ProcessMessageAsync(orgId, supplierId, message, ct);
            // Flag SEEN whenever the message was fully handled — including the case where every
            // attachment was a duplicate already imported (processed == true via the dedupe path).
            // Only an empty/unsupported message (processed == false) is left unseen for retry.
            if (processed)
            {
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct);
                queued++;
            }
        }

        if (queued > 0)
            _logger.LogInformation("EmailPollOrgJob org {OrgId}: queued {Queued} parse job(s) from email attachments.", orgId, queued);

        await client.DisconnectAsync(quit: true, ct);
        await _emailSettings.MarkPolledAsync(orgId, DateTime.UtcNow, ct);

        _logger.LogInformation("EmailPollOrgJob: IMAP poll complete for org {OrgId}. Unseen={Unseen}, ParseJobsQueued={Queued}.", orgId, unseen.Count, queued);
    }

    /// <summary>
    /// Imports each supported, non-duplicate attachment of one message into an order stub and
    /// enqueues a parse job. <paramref name="supplierId"/> is the RESOLVED default supplier
    /// (validated org-scoped + not-deleted): non-null routes the stub to it, null imports it
    /// UNROUTED for later assignment. Returns true when the message was fully handled (imported
    /// at least one attachment OR found every attachment already imported) so the caller may flag
    /// it SEEN. <c>public</c> for direct unit testing of the (OrgId, Message-Id, AttachmentHash)
    /// dedupe without a live IMAP server (exposing it via InternalsVisibleTo would surface the
    /// Worker's top-level <c>Program</c> and clash with the API's in WebApplicationFactory tests).
    /// </summary>
    public async Task<bool> ProcessMessageAsync(Guid orgId, Guid? supplierId, MimeMessage message, CancellationToken ct)
    {
        var processedAny = false;

        // The IMAP Message-Id header; "" when the server omits it (the content hash still dedupes).
        var messageId = message.MessageId ?? string.Empty;

        foreach (var attachment in message.Attachments)
        {
            if (attachment is not MimePart part)
                continue;

            var fileName = string.IsNullOrWhiteSpace(part.FileName)
                ? $"email-attachment-{Guid.NewGuid():N}.dat"
                : part.FileName;
            var extension = Path.GetExtension(fileName);

            if (!SupportedExtensions.Contains(extension))
                continue;

            if (part.Content is null)
                continue;

            await using var stream = new MemoryStream();
            await part.Content.DecodeToAsync(stream, ct);

            // Size cap — skip oversized attachments before the parse pipeline.
            if (stream.Length > IngressLimits.MaxFileBytes)
            {
                _logger.LogWarning(
                    "EmailPollOrgJob: skipping attachment {FileName} for org {OrgId} ({Bytes} bytes > {Max} byte cap).",
                    fileName, orgId, stream.Length, IngressLimits.MaxFileBytes);
                continue;
            }

            // ── Idempotency: dedupe on (OrgId, ImapMessageId, AttachmentHash) ─────────────────
            // The message is flagged SEEN only after the whole loop succeeds, so a crash mid-poll
            // re-presents this unseen message next time. Without this guard the same attachment is
            // re-imported as a NEW order. Hash the decoded bytes and skip if we already imported it.
            var attachmentHash = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));

            var alreadyImported = await _db.EmailImportRecords.AsNoTracking().AnyAsync(
                r => r.OrgId == orgId
                  && r.ImapMessageId == messageId
                  && r.AttachmentHash == attachmentHash, ct);
            if (alreadyImported)
            {
                _logger.LogInformation(
                    "EmailPollOrgJob: attachment {FileName} for org {OrgId} already imported (message {MessageId}); skipping duplicate.",
                    fileName, orgId, messageId);
                // Count as processed so the message can still be flagged SEEN — there is nothing new
                // to import, but leaving it unseen would re-trigger this dedupe check every poll.
                processedAny = true;
                continue;
            }

            stream.Position = 0;

            var contentType = string.IsNullOrWhiteSpace(part.ContentType.MimeType)
                ? "application/octet-stream"
                : part.ContentType.MimeType;

            // Routed when a valid default supplier exists; UNROUTED hold otherwise —
            // same creation path browser upload and inbound email use for supplier-less files.
            var result = supplierId is { } routedSupplierId
                ? await _orders.CreateStubAsync(orgId, routedSupplierId, stream, fileName, contentType, ct)
                : await _orders.CreateUnroutedStubAsync(orgId, stream, fileName, contentType, ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "EmailPollOrgJob: {Mode} order stub creation failed for attachment {FileName} (org {OrgId}): {Error}",
                    supplierId is null ? "unrouted" : "routed", fileName, orgId, result.Error);
                continue;
            }

            // Record the import BEFORE enqueuing the parse job. The unique index on
            // (OrgId, ImapMessageId, AttachmentHash) wins the race between two concurrent polls of
            // the same mailbox: the loser's insert throws and we treat the attachment as already
            // imported (its order stub from the winner is the canonical one).
            var record = new EmailImportRecord
            {
                Id             = Guid.NewGuid(),
                OrgId          = orgId,
                ImapMessageId  = messageId,
                AttachmentHash = attachmentHash,
                OrderId        = result.Value!.Id,
                FileName       = fileName,
                ImportedAt     = DateTime.UtcNow,
            };
            _db.EmailImportRecords.Add(record);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent poll already imported this exact attachment (unique-index violation).
                // The other poll owns the canonical order stub; do not enqueue a second parse job.
                _logger.LogInformation(
                    "EmailPollOrgJob: attachment {FileName} for org {OrgId} imported concurrently (message {MessageId}); skipping duplicate parse.",
                    fileName, orgId, messageId);
                _db.Entry(record).State = EntityState.Detached;
                processedAny = true;
                continue;
            }

            ParseOrderJob.Enqueue(_jobs, result.Value!.Id, orgId);
            processedAny = true;
        }

        return processedAny;
    }

    /// <summary>
    /// Resolves the configured default supplier to a routable supplier id, or null when the
    /// attachments must be imported unrouted. Org-scoped + not-deleted — a wrong-org or
    /// soft-deleted id resolves to unrouted, never cross-tenant (same contract as the SFTP/S3
    /// pull-ingress resolvers). <c>public</c> for direct unit testing without a live IMAP server,
    /// same rationale as <see cref="ProcessMessageAsync"/>.
    /// </summary>
    public async Task<Guid?> ResolveDefaultSupplierIdAsync(
        Guid orgId,
        Guid? defaultSupplierId,
        CancellationToken ct)
    {
        if (defaultSupplierId is null || defaultSupplierId == Guid.Empty)
        {
            return null;
        }

        var exists = await _db.Suppliers
            .AsNoTracking()
            .AnyAsync(
                s => s.OrgId == orgId
                  && s.Id == defaultSupplierId
                  && s.DeletedAt == null,
                ct);

        return exists ? defaultSupplierId.Value : null;
    }
}
