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
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Worker.Jobs;

public sealed class EmailPollingJob
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
    private readonly ILogger<EmailPollingJob> _logger;

    public EmailPollingJob(
        ProcuLinkDbContext db,
        DeliveryEncryptionService encryption,
        IOrderService orders,
        IBackgroundJobClient jobs,
        IBillingService billing,
        IEmailSettingsService emailSettings,
        ILogger<EmailPollingJob> logger)
    {
        _db = db;
        _encryption = encryption;
        _orders = orders;
        _jobs = jobs;
        _billing = billing;
        _emailSettings = emailSettings;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var orgConfigs = await _db.Organisations
            .AsNoTracking()
            .Where(x => x.EmailConfigJson != "{}")
            .Select(x => new { x.Id, x.EmailConfigJson })
            .ToListAsync(ct);

        foreach (var org in orgConfigs)
        {
            var config = EmailPollingConfig.FromJson(org.EmailConfigJson);
            if (!config.Enabled)
                continue;

            if (!await _billing.HasFeatureAsync(org.Id, BillingFeature.EmailIngestion, ct))
            {
                _logger.LogInformation("Skipping email polling for org {OrgId}: plan does not include email ingestion.", org.Id);
                continue;
            }

            try
            {
                await PollOrganisationAsync(org.Id, config, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email polling failed for org {OrgId}.", org.Id);
            }
        }
    }

    private async Task PollOrganisationAsync(Guid orgId, EmailPollingConfig config, CancellationToken ct)
    {
        if (config.DefaultSupplierId is null || string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.Username))
        {
            _logger.LogWarning("Skipping email polling for org {OrgId}: email config is incomplete.", orgId);
            return;
        }

        var password = string.IsNullOrWhiteSpace(config.PasswordCiphertext)
            ? string.Empty
            : _encryption.Decrypt(config.PasswordCiphertext);

        if (password is null)
        {
            _logger.LogWarning("Skipping email polling for org {OrgId}: IMAP password could not be decrypted.", orgId);
            return;
        }

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
        foreach (var uid in unseen)
        {
            var message = await folder.GetMessageAsync(uid, ct);
            var processed = await ProcessMessageAsync(orgId, config.DefaultSupplierId.Value, message, ct);
            if (processed)
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct);
        }

        await client.DisconnectAsync(quit: true, ct);
        await _emailSettings.MarkPolledAsync(orgId, DateTime.UtcNow, ct);
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
            stream.Position = 0;

            var contentType = string.IsNullOrWhiteSpace(part.ContentType.MimeType)
                ? "application/octet-stream"
                : part.ContentType.MimeType;

            var result = await _orders.CreateStubAsync(orgId, supplierId, stream, fileName, contentType, ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Email attachment {FileName} for org {OrgId} could not create order: {Error}",
                    fileName,
                    orgId,
                    result.Error);
                continue;
            }

            ParseOrderJob.Enqueue(_jobs, result.Value!.Id, orgId);
            processedAny = true;
        }

        return processedAny;
    }
}
