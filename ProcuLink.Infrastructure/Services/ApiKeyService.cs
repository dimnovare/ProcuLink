using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class ApiKeyService : IApiKeyService
{
    private readonly ProcuLinkDbContext _db;
    private readonly string _hashSecret;

    public ApiKeyService(ProcuLinkDbContext db, IConfiguration configuration)
    {
        _db = db;
        _hashSecret = configuration["Security:ApiKeyHashSecret"]
                      ?? throw new InvalidOperationException(
                          "Security:ApiKeyHashSecret is not configured. " +
                          "Set this via environment variable SECURITY__APIKEYHASHSECRET or appsettings.");
    }

    public async Task<(TenantApiKey Key, string RawKey)> CreateAsync(
        Guid organisationId, string label, DateTime? expiresAt, CancellationToken ct)
    {
        // plk_ prefix + 48 URL-safe base64 chars = ~52 chars total
        var rawBytes = new byte[36];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rawBytes);
        var rawKey = "plk_" + Convert.ToBase64String(rawBytes)
                                     .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var hash   = ApiKeyHasher.ComputeHash(rawKey, _hashSecret);
        var prefix = rawKey[..Math.Min(8, rawKey.Length)];

        var entity = new TenantApiKey
        {
            Id             = Guid.NewGuid(),
            OrganisationId = organisationId,
            Label          = label.Trim(),
            KeyHash        = hash,
            KeyPrefix      = prefix,
            IsActive       = true,
            CreatedAt      = DateTime.UtcNow,
            ExpiresAt      = expiresAt,
        };

        _db.TenantApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);

        return (entity, rawKey);
    }

    public async Task<IReadOnlyList<TenantApiKey>> ListAsync(Guid organisationId, CancellationToken ct)
        => await _db.TenantApiKeys
                    .Where(k => k.OrganisationId == organisationId)
                    .OrderByDescending(k => k.CreatedAt)
                    .ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid organisationId, Guid keyId, CancellationToken ct)
    {
        var key = await _db.TenantApiKeys
                           .Where(k => k.OrganisationId == organisationId && k.Id == keyId)
                           .FirstOrDefaultAsync(ct);
        if (key is null) return false;
        key.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
