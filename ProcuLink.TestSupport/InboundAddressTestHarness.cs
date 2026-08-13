using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Email;

namespace ProcuLink.TestSupport;

/// <summary>
/// Builds a real <see cref="InboundAddressService"/> over a test context, and registers inbound
/// addresses for a test organisation.
///
/// <para>Deliberately the REAL service and the REAL encryption service, never a stub. The thing
/// under test in every inbound-email test is "which organisation does this message belong to", and
/// a stubbed resolver would answer that question by fiat — which is exactly the property that was
/// broken before this table existed. Tests that want a NON-resolving address simply do not seed
/// one.</para>
///
/// <para>The two constants below are test fixtures, not secrets: an all-zero AES key and a fixed
/// HMAC pepper, neither of which exists in any deployment. Production supplies both from the
/// environment (<c>DELIVERY__ENCRYPTIONKEY</c>, <c>SECURITY__APIKEYHASHSECRET</c>).</para>
/// </summary>
public static class InboundAddressTestHarness
{
    /// <summary>All-zero 32-byte AES-GCM key. Test fixture — the same literal every other
    /// encryption test in this repo uses.</summary>
    public const string TestEncryptionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    /// <summary>Fixed HMAC pepper for address hashing. Test fixture; must clear the 16-character
    /// minimum the production startup validator enforces.</summary>
    public const string TestHashSecret = "test-inbound-address-hash-secret";

    /// <summary>
    /// The configuration an inbound-address service needs, merged with any extra settings the
    /// caller wants (e.g. <c>Inbound:Postmark:InboundDomain</c>).
    /// </summary>
    public static IConfiguration Configuration(IDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Security:ApiKeyHashSecret"] = TestHashSecret,
            ["Delivery:EncryptionKey"] = TestEncryptionKey,
        };

        if (extra is not null)
            foreach (var (key, value) in extra)
                settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>Builds the real service over <paramref name="db"/>.</summary>
    public static IInboundAddressService Create(ProcuLinkDbContext db, IConfiguration? config = null)
    {
        var effective = config ?? Configuration();
        return new InboundAddressService(
            db,
            new DeliveryEncryptionService(effective),
            effective,
            NullLogger<InboundAddressService>.Instance);
    }

    /// <summary>
    /// Registers <paramref name="token"/> as a live inbound address for <paramref name="orgId"/>, so
    /// mail addressed to it resolves to that organisation. Returns the address row id.
    /// </summary>
    public static async Task<Guid> SeedAddressAsync(
        ProcuLinkDbContext db,
        Guid orgId,
        string token,
        IConfiguration? config = null,
        string kind = InboundAddressKind.Primary,
        DateTime? expiresAt = null,
        bool isActive = true,
        DateTime? revokedAt = null)
    {
        var effective = config ?? Configuration();
        var id = Guid.NewGuid();

        db.OrgInboundAddresses.Add(new ProcuLink.Core.Entities.OrgInboundAddress
        {
            Id = id,
            OrganisationId = orgId,
            TokenHash = InboundAddressService.ComputeHash(token, TestHashSecret),
            EncryptedToken = new DeliveryEncryptionService(effective).Encrypt(
                token,
                ProcuLink.Core.Services.Security.CredentialScope.ForSupplier(
                    orgId, ProcuLink.Core.Services.Security.CredentialPurpose.OrgInboundEmailAddress, id)),
            TokenPrefix = token[..Math.Min(6, token.Length)],
            Kind = kind,
            Label = "Test address",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
        });

        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Synchronous form, for test helpers that are not async.</summary>
    public static Guid SeedAddress(
        ProcuLinkDbContext db,
        Guid orgId,
        string token,
        IConfiguration? config = null) =>
        SeedAddressAsync(db, orgId, token, config).GetAwaiter().GetResult();
}
