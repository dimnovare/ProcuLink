using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services;

public sealed class EmailSettingsService : IEmailSettingsService
{
    private const string PasswordMask = "********";

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;

    public EmailSettingsService(ProcuLinkDbContext db, DeliveryEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<EmailSettingsResponse> GetAsync(Guid orgId, CancellationToken ct)
    {
        var json = await _db.Organisations
            .AsNoTracking()
            .Where(x => x.Id == orgId)
            .Select(x => x.EmailConfigJson)
            .FirstOrDefaultAsync(ct);

        return ToResponse(EmailPollingConfig.FromJson(json));
    }

    public async Task<EmailSettingsResponse> UpdateAsync(
        Guid orgId,
        UpdateEmailSettingsRequest request,
        CancellationToken ct)
    {
        var org = await _db.Organisations
            .Where(x => x.Id == orgId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");

        var current = EmailPollingConfig.FromJson(org.EmailConfigJson);
        var passwordCiphertext = current.PasswordCiphertext;

        if (request.Password is not null)
        {
            passwordCiphertext = string.IsNullOrWhiteSpace(request.Password)
                ? null
                : _encryption.Encrypt(request.Password);
        }

        var updated = new EmailPollingConfig(
            Enabled: request.Enabled,
            Host: Normalize(request.Host),
            Port: request.Port <= 0 ? 993 : request.Port,
            UseSsl: request.UseSsl,
            Username: Normalize(request.Username),
            Folder: string.IsNullOrWhiteSpace(request.Folder) ? "INBOX" : request.Folder.Trim(),
            DefaultSupplierId: request.DefaultSupplierId,
            PasswordCiphertext: passwordCiphertext,
            LastPolledAt: current.LastPolledAt,
            UpdatedAt: DateTime.UtcNow);

        org.EmailConfigJson = updated.ToJson();
        // Keep the indexed poller-candidate flag in lock-step with the jsonb config
        // (same set as the legacy "email_config <> '{}'" predicate).
        org.EmailPollingEnabled = org.EmailConfigJson != "{}";
        await _db.SaveChangesAsync(ct);

        return ToResponse(updated);
    }

    public async Task MarkPolledAsync(Guid orgId, DateTime polledAt, CancellationToken ct)
    {
        var org = await _db.Organisations
            .Where(x => x.Id == orgId)
            .FirstOrDefaultAsync(ct);

        if (org is null) return;

        var current = EmailPollingConfig.FromJson(org.EmailConfigJson);
        org.EmailConfigJson = (current with
        {
            LastPolledAt = polledAt,
            UpdatedAt = current.UpdatedAt ?? polledAt
        }).ToJson();
        org.EmailPollingEnabled = org.EmailConfigJson != "{}";
        await _db.SaveChangesAsync(ct);
    }

    private static EmailSettingsResponse ToResponse(EmailPollingConfig config)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(config.PasswordCiphertext);
        return new EmailSettingsResponse(
            config.Enabled,
            config.Host,
            config.Port,
            config.UseSsl,
            config.Username,
            config.Folder,
            config.DefaultSupplierId,
            hasPassword,
            hasPassword ? PasswordMask : null,
            config.LastPolledAt,
            config.UpdatedAt);
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
