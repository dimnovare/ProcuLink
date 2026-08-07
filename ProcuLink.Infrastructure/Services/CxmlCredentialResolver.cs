using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Resolves a supplier's <see cref="CxmlCredentialConfig"/> from its delivery-config row for the
/// transform path: reads the cleartext cXML identities and decrypts the Sender SharedSecret with
/// the SAME AES-GCM service used for delivery credentials.
///
/// <para>Returns null — meaning "use the legacy GUID identities" — when the supplier has no
/// delivery-config row, or has neither cXML identities nor a stored shared secret. Org-scoped:
/// only ever reads the (org, supplier) row.</para>
/// </summary>
public sealed class CxmlCredentialResolver : ICxmlCredentialResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;

    public CxmlCredentialResolver(ProcuLinkDbContext db, DeliveryEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<CxmlCredentialConfig?> ResolveAsync(Guid organisationId, Guid supplierId, CancellationToken ct)
    {
        var row = await _db.SupplierDeliveryConfigs
            .AsNoTracking()
            .Where(x => x.OrgId == organisationId && x.SupplierId == supplierId)
            .Select(x => new { x.CxmlConfigJson, x.EncryptedCxmlSharedSecret })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return null;

        CxmlIdentityFields? ids = null;
        if (!string.IsNullOrWhiteSpace(row.CxmlConfigJson))
        {
            try { ids = JsonSerializer.Deserialize<CxmlIdentityFields>(row.CxmlConfigJson, JsonOptions); }
            catch (JsonException) { ids = null; } // malformed config must never break the transform
        }

        // No catch: a secret that will not decrypt must NOT degrade into "no secret". The tolerance
        // at line 49 is for unparseable identity JSON, which is not a credential.
        var sharedSecret = string.IsNullOrWhiteSpace(row.EncryptedCxmlSharedSecret)
            ? null
            : _encryption.Decrypt(
                row.EncryptedCxmlSharedSecret,
                CredentialScope.ForSupplier(
                    organisationId, CredentialPurpose.SupplierDeliveryCxmlSecret, supplierId));

        // Nothing usable → legacy identities (null is the "unconfigured" signal the transform expects).
        // A configured DTD counts as "configured" too: a supplier may set ONLY a DOCTYPE (no network
        // identity, no secret) and that DTD must still reach the transform.
        var hasAnyIdentity = ids is not null && (
            !string.IsNullOrWhiteSpace(ids.FromDomain) || !string.IsNullOrWhiteSpace(ids.FromIdentity) ||
            !string.IsNullOrWhiteSpace(ids.ToDomain) || !string.IsNullOrWhiteSpace(ids.ToIdentity) ||
            !string.IsNullOrWhiteSpace(ids.SenderDomain) || !string.IsNullOrWhiteSpace(ids.SenderIdentity));
        var hasDtd = ids is not null && !string.IsNullOrWhiteSpace(ids.DtdSystemId);

        if (!hasAnyIdentity && !hasDtd && string.IsNullOrWhiteSpace(sharedSecret))
            return null;

        return new CxmlCredentialConfig(
            ids?.FromDomain, ids?.FromIdentity,
            ids?.ToDomain, ids?.ToIdentity,
            ids?.SenderDomain, ids?.SenderIdentity,
            sharedSecret)
        {
            DtdSystemId = ids?.DtdSystemId,
            DtdPublicId = ids?.DtdPublicId,
        };
    }
}
