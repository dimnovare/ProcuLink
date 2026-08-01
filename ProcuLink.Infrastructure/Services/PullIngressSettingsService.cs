using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Self-serve CRUD for SFTP and S3/R2 pull-ingress configs. Mirrors
/// <see cref="EmailSettingsService"/>: secrets are encrypted via
/// <see cref="DeliveryEncryptionService"/> and responses are masked.
/// </summary>
public sealed class PullIngressSettingsService : IPullIngressSettingsService
{
    private const string Mask = "********";

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;

    public PullIngressSettingsService(ProcuLinkDbContext db, DeliveryEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    // ── SFTP ────────────────────────────────────────────────────────────────

    public async Task<SftpIngressResponse> GetSftpAsync(Guid orgId, CancellationToken ct)
    {
        var cfg = await _db.SftpIngressConfigs
            .AsNoTracking()
            .Where(x => x.OrgId == orgId)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return ToSftpResponse(cfg);
    }

    public async Task<SftpIngressResponse> UpdateSftpAsync(Guid orgId, UpdateSftpIngressRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cfg = await _db.SftpIngressConfigs
            .Where(x => x.OrgId == orgId)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (cfg is null)
        {
            cfg = new SftpIngressConfig { Id = Guid.NewGuid(), OrgId = orgId, CreatedAt = now };
            _db.SftpIngressConfigs.Add(cfg);
        }

        var newHost = Normalize(request.Host);
        var newPort = request.Port <= 0 ? 22 : request.Port;

        // A host-key pin names a SERVER. Repointing the poller at a different host or port makes the
        // stored fingerprint describe something it no longer talks to, and leaving it would refuse
        // the new server forever with a message about a rebuild that never happened. Cleared before
        // the request's own explicit value is applied below, so an operator supplying both wins.
        if (!string.Equals(cfg.Host, newHost, StringComparison.OrdinalIgnoreCase) || cfg.Port != newPort)
            cfg.HostKeyFingerprints = null;

        cfg.Host = newHost;
        cfg.Port = newPort;
        cfg.Username = Normalize(request.Username);
        cfg.RemoteDirectory = Normalize(request.RemoteDirectory);
        cfg.DefaultSupplierId = request.DefaultSupplierId;
        cfg.IsEnabled = request.Enabled;
        if (request.Password is not null)
        {
            cfg.EncryptedPassword = string.IsNullOrWhiteSpace(request.Password)
                ? string.Empty
                : _encryption.Encrypt(request.Password);
        }
        // null = keep whatever the last successful poll recorded (a client that has never heard of
        // host keys must not silently un-pin the connection); "" = the operator deliberately
        // re-trusting after a server rebuild, so the next poll pins whatever it then finds.
        if (request.HostKeyFingerprints is not null)
        {
            cfg.HostKeyFingerprints = SshHostKeyPolicy.Serialise(
                SshHostKeyPolicy.Parse(request.HostKeyFingerprints));
        }
        cfg.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return ToSftpResponse(cfg);
    }

    // ── S3 / R2 ───────────────────────────────────────────────────────────────

    public async Task<S3IngressResponse> GetS3Async(Guid orgId, CancellationToken ct)
    {
        var cfg = await _db.S3IngressConfigs
            .AsNoTracking()
            .Where(x => x.OrgId == orgId)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return ToS3Response(cfg);
    }

    public async Task<S3IngressResponse> UpdateS3Async(Guid orgId, UpdateS3IngressRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cfg = await _db.S3IngressConfigs
            .Where(x => x.OrgId == orgId)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (cfg is null)
        {
            cfg = new S3IngressConfig { Id = Guid.NewGuid(), OrgId = orgId, CreatedAt = now };
            _db.S3IngressConfigs.Add(cfg);
        }

        cfg.BucketName = Normalize(request.BucketName);
        cfg.KeyPrefix = Normalize(request.KeyPrefix);
        cfg.Region = Normalize(request.Region);
        cfg.ServiceUrl = string.IsNullOrWhiteSpace(request.ServiceUrl) ? null : request.ServiceUrl.Trim();
        cfg.AccessKeyId = Normalize(request.AccessKeyId);
        cfg.DefaultSupplierId = request.DefaultSupplierId;
        cfg.IsEnabled = request.Enabled;
        if (request.SecretKey is not null)
        {
            cfg.EncryptedSecretKey = string.IsNullOrWhiteSpace(request.SecretKey)
                ? string.Empty
                : _encryption.Encrypt(request.SecretKey);
        }
        cfg.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return ToS3Response(cfg);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static SftpIngressResponse ToSftpResponse(SftpIngressConfig? c)
    {
        if (c is null)
            return new SftpIngressResponse(false, string.Empty, 22, string.Empty, string.Empty, null, false, null, null, null);

        var has = !string.IsNullOrWhiteSpace(c.EncryptedPassword);
        return new SftpIngressResponse(
            c.IsEnabled, c.Host, c.Port, c.Username, c.RemoteDirectory, c.DefaultSupplierId,
            has, has ? Mask : null, c.UpdatedAt, c.HostKeyFingerprints);
    }

    private static S3IngressResponse ToS3Response(S3IngressConfig? c)
    {
        if (c is null)
            return new S3IngressResponse(false, string.Empty, string.Empty, string.Empty, string.Empty, null, false, null, null, null);

        var has = !string.IsNullOrWhiteSpace(c.EncryptedSecretKey);
        return new S3IngressResponse(
            c.IsEnabled, c.BucketName, c.KeyPrefix, c.Region, c.AccessKeyId, c.DefaultSupplierId,
            has, has ? Mask : null, c.UpdatedAt, c.ServiceUrl);
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
