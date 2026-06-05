using Hangfire;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Per-organisation child job: opens the IMAP connection, imports unseen attachments,
/// and enqueues parse jobs for one organisation. Scheduled by <see cref="EmailPollingJob"/>.
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
    private readonly ILogger<EmailPollOrgJob> _logger;

    public EmailPollOrgJob(
        ProcuLinkDbContext db,
        DeliveryEncryptionService encryption,
        IOrderService orders,
        IBackgroundJobClient jobs,
        IBillingService billing,
        IEmailSettingsService emailSettings,
        ILogger<EmailPollOrgJob> logger)
    {
        _db = db;
        _encryption = encryption;
        _orders = orders;
        _jobs = jobs;
        _billing = billing;
        _emailSettings = emailSettings;
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

        if (config.DefaultSupplierId is null
            || string.IsNullOrWhiteSpace(config.Host)
            || string.IsNullOrWhiteSpace(config.Username))
        {
            _logger.LogWarning("EmailPollOrgJob: incomplete config (host/username/supplierId missing) for org {OrgId}.", orgId);
            return;
        }

        var password = string.IsNullOrWhiteSpace(config.PasswordCiphertext)
            ? string.Empty
            : _encryption.Decrypt(config.PasswordCiphertext);

        if (password is null)
        {
            _logger.LogWarning("EmailPollOrgJob: IMAP password could not be decrypted for org {OrgId}.", orgId);
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
            var processed = await ProcessMessageAsync(orgId, config.DefaultSupplierId.Value, message, ct);
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

    private async Task<bool> ProcessMessageAsync(Guid orgId, Guid supplierId, MimeMessage message, CancellationToken ct)
    {
        var processedAny = false;

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

            stream.Position = 0;

            var contentType = string.IsNullOrWhiteSpace(part.ContentType.MimeType)
                ? "application/octet-stream"
                : part.ContentType.MimeType;

            var result = await _orders.CreateStubAsync(orgId, supplierId, stream, fileName, contentType, ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "EmailPollOrgJob: attachment {FileName} for org {OrgId} could not create order: {Error}",
                    fileName, orgId, result.Error);
                continue;
            }

            ParseOrderJob.Enqueue(_jobs, result.Value!.Id, orgId);
            processedAny = true;
        }

        return processedAny;
    }
}
