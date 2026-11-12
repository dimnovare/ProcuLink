using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;

namespace ProcuLink.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// Task 4 — catalog source credentials (file-channel password, http/vendor auth-config envelope)
/// bound to <c>SupplierCatalogSource.Id</c>. A catalog source is never snapshotted anywhere, so the
/// row id is the tightest available binding (unlike delivery credentials, which must scope on
/// supplier id because a pinned revision holds a verbatim byte-copy — see
/// <see cref="CredentialScope"/>'s doc comment).
/// </summary>
public class CatalogCredentialBindingTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceOne = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SourceTwo = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid SupplierId = Guid.Parse("77777777-7777-7777-7777-777777777777");

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
            .UseInMemoryDatabase($"catalog-cred-binding-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public void CatalogPassword_DoesNotDecryptForAnotherSource()
    {
        var enc = Encryption();
        var blob = enc.Encrypt("sftp-password", CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogPassword, SourceOne));

        var act = () => enc.Decrypt(blob, CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogPassword, SourceTwo));

        act.Should().Throw<CredentialUnbindableException>();
    }

    [Fact]
    public void CatalogPassword_DoesNotDecryptAsAuthConfig()
    {
        var enc = Encryption();
        var blob = enc.Encrypt("sftp-password", CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogPassword, SourceOne));

        var act = () => enc.Decrypt(blob, CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogAuthConfig, SourceOne));

        act.Should().Throw<CredentialUnbindableException>();
    }
}
