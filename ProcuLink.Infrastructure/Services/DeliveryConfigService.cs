using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

public sealed class DeliveryConfigService : IDeliveryConfigService
{
    private const string CredentialsMask = "********";

    private static readonly HashSet<string> AllowedProtocols = new(
        DeliveryProtocolConstants.All,
        StringComparer.OrdinalIgnoreCase);

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;

    public DeliveryConfigService(ProcuLinkDbContext db, DeliveryEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public Task<SupplierDeliveryConfig?> GetEntityAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

    public async Task<DeliveryConfigResponse?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var config = await _db.SupplierDeliveryConfigs
            .AsNoTracking()
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        return config is null ? null : ToResponse(config);
    }

    public async Task<DeliveryConfigResponse> UpsertAsync(
        Guid orgId,
        Guid supplierId,
        UpsertDeliveryConfigRequest request,
        CancellationToken ct)
    {
        var protocol = NormalizeProtocol(request.Protocol);
        ValidateConfigJson(request.ConfigJson);

        var now = DateTime.UtcNow;
        var existing = await _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            existing = new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplierId,
                CreatedAt = now,
            };
            _db.SupplierDeliveryConfigs.Add(existing);
        }

        existing.Protocol = protocol;
        existing.AutoDeliver = request.AutoDeliver;
        existing.ConfigJson = request.ConfigJson;
        existing.UpdatedAt = now;

        if (request.CredentialsJson is not null)
        {
            existing.EncryptedCredentials = string.IsNullOrWhiteSpace(request.CredentialsJson)
                ? string.Empty
                : _encryption.Encrypt(request.CredentialsJson);
        }

        await _db.SaveChangesAsync(ct);

        return ToResponse(existing);
    }

    public async Task DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var existing = await _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (existing is null) return;

        _db.SupplierDeliveryConfigs.Remove(existing);
        await _db.SaveChangesAsync(ct);
    }

    private static DeliveryConfigResponse ToResponse(SupplierDeliveryConfig config)
    {
        var hasCredentials = !string.IsNullOrWhiteSpace(config.EncryptedCredentials);
        return new DeliveryConfigResponse(
            config.SupplierId,
            config.Protocol,
            config.AutoDeliver,
            config.ConfigJson,
            hasCredentials,
            hasCredentials ? CredentialsMask : null,
            config.CreatedAt,
            config.UpdatedAt);
    }

    private static string NormalizeProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            throw new ArgumentException("Delivery protocol is required.", nameof(protocol));

        var normalized = protocol.Trim().ToLowerInvariant();
        if (!AllowedProtocols.Contains(normalized))
            throw new ArgumentException(
                $"Delivery protocol must be {DeliveryProtocolConstants.AllowedListForMessage}.",
                nameof(protocol));

        return normalized;
    }

    private static void ValidateConfigJson(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new ArgumentException("Delivery config JSON is required.", nameof(configJson));

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(configJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException("Delivery config JSON must be valid JSON.", nameof(configJson), ex);
        }
    }
}
