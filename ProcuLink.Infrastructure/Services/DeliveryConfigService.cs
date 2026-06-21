using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

public sealed class DeliveryConfigService : IDeliveryConfigService
{
    private const string CredentialsMask = "********";

    private static readonly JsonSerializerOptions CxmlJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> AllowedProtocols = new(
        DeliveryProtocolConstants.All,
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedOutputFormats = new(
        new[] { "xml", "csv", "cxml", "json", "ubl", "x12" },
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
        existing.OutputFormat = NormalizeOutputFormat(request.OutputFormat);
        existing.UpdatedAt = now;

        if (request.CredentialsJson is not null)
        {
            existing.EncryptedCredentials = string.IsNullOrWhiteSpace(request.CredentialsJson)
                ? string.Empty
                : _encryption.Encrypt(request.CredentialsJson);
        }

        ApplyCxmlCredentials(existing, request.CxmlCredentials);

        await _db.SaveChangesAsync(ct);

        return ToResponse(existing);
    }

    /// <summary>
    /// Applies the optional cXML credential block. Null/omitted leaves the saved cXML credentials
    /// untouched. When present, the non-secret identities are persisted as cleartext JSON and the
    /// Sender SharedSecret follows leave-blank-to-keep semantics: null = keep, blank = clear,
    /// non-blank = (re)encrypt with the SAME AES-GCM service as the delivery transport credentials.
    /// </summary>
    private void ApplyCxmlCredentials(SupplierDeliveryConfig existing, CxmlCredentialsInput? cxml)
    {
        if (cxml is null) return;

        existing.CxmlConfigJson = JsonSerializer.Serialize(
            new CxmlIdentityFields(
                Trimmed(cxml.FromDomain), Trimmed(cxml.FromIdentity),
                Trimmed(cxml.ToDomain), Trimmed(cxml.ToIdentity),
                Trimmed(cxml.SenderDomain), Trimmed(cxml.SenderIdentity),
                // Configurable cXML DOCTYPE (T7) — persisted as camelCase dtdSystemId/dtdPublicId.
                Trimmed(cxml.DtdSystemId), Trimmed(cxml.DtdPublicId)),
            CxmlJsonOptions);

        if (cxml.SenderSharedSecret is not null)
        {
            existing.EncryptedCxmlSharedSecret = string.IsNullOrWhiteSpace(cxml.SenderSharedSecret)
                ? null
                : _encryption.Encrypt(cxml.SenderSharedSecret);
        }
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
            config.UpdatedAt,
            config.OutputFormat,
            CxmlCredentials: BuildCxmlResponse(config));
    }

    /// <summary>
    /// Surfaces the cleartext cXML identities + a HasSharedSecret flag (never the secret itself).
    /// Returns null when the supplier has neither cXML identities nor a stored shared secret.
    /// </summary>
    private static CxmlCredentialsResponse? BuildCxmlResponse(SupplierDeliveryConfig config)
    {
        var hasSecret = !string.IsNullOrWhiteSpace(config.EncryptedCxmlSharedSecret);
        if (string.IsNullOrWhiteSpace(config.CxmlConfigJson) && !hasSecret)
            return null;

        CxmlIdentityFields? ids = null;
        if (!string.IsNullOrWhiteSpace(config.CxmlConfigJson))
        {
            try { ids = JsonSerializer.Deserialize<CxmlIdentityFields>(config.CxmlConfigJson, CxmlJsonOptions); }
            catch (JsonException) { ids = null; }
        }

        return new CxmlCredentialsResponse(
            ids?.FromDomain, ids?.FromIdentity,
            ids?.ToDomain, ids?.ToIdentity,
            ids?.SenderDomain, ids?.SenderIdentity,
            HasSharedSecret: hasSecret,
            DtdSystemId: ids?.DtdSystemId, DtdPublicId: ids?.DtdPublicId);
    }

    private static string? NormalizeOutputFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        var normalized = format.Trim().ToLowerInvariant();
        if (!AllowedOutputFormats.Contains(normalized))
            throw new ArgumentException(
                "Output format must be one of: xml, csv, cxml, json, ubl, x12.", nameof(format));
        return normalized;
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
