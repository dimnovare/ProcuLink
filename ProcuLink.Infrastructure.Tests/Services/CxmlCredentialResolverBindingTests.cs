using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class CxmlCredentialResolverBindingTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupX = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static DeliveryEncryptionService Encryption()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"cxml-binding-{Guid.NewGuid()}")
            .Options);

    private static async Task SeedAsync(ProcuLinkDbContext db, string? encryptedSecret)
    {
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = OrgA,
            SupplierId = SupX,
            Protocol = "http",
            CxmlConfigJson = """{"fromDomain":"DUNS","fromIdentity":"123456789"}""",
            EncryptedCxmlSharedSecret = encryptedSecret,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resolve_SecretBoundToThisSupplier_ReturnsIt()
    {
        var enc = Encryption();
        await using var db = NewDb();
        var scope = CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierDeliveryCxmlSecret, SupX);
        await SeedAsync(db, enc.Encrypt("shared-secret-value", scope));

        var result = await new CxmlCredentialResolver(db, enc)
            .ResolveAsync(OrgA, SupX, CancellationToken.None);

        result!.SenderSharedSecret.Should().Be("shared-secret-value");
    }

    // The fail-open fix. Before this change, an unreadable secret produced a config carrying
    // sharedSecret: null and the transform emitted a cXML document without one.
    [Fact]
    public async Task Resolve_SecretBoundToADifferentSupplier_Throws()
    {
        var enc = Encryption();
        await using var db = NewDb();
        var otherSupplier = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var wrongScope = CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierDeliveryCxmlSecret, otherSupplier);
        await SeedAsync(db, enc.Encrypt("shared-secret-value", wrongScope));

        var act = async () => await new CxmlCredentialResolver(db, enc)
            .ResolveAsync(OrgA, SupX, CancellationToken.None);

        await act.Should().ThrowAsync<CredentialUnbindableException>();
    }

    [Fact]
    public async Task Resolve_NoSecretStored_StillReturnsIdentitiesWithoutThrowing()
    {
        var enc = Encryption();
        await using var db = NewDb();
        await SeedAsync(db, null);

        var result = await new CxmlCredentialResolver(db, enc)
            .ResolveAsync(OrgA, SupX, CancellationToken.None);

        result!.SenderSharedSecret.Should().BeNull();
        result.FromDomain.Should().Be("DUNS");
    }
}
