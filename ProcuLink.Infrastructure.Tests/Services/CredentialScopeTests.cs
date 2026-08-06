using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services;

public class CredentialScopeTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SupX = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SupY = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void ToAssociatedData_IsStableForTheSameTuple()
    {
        var a = CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);
        var b = CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);

        a.ToAssociatedData().Should().Equal(b.ToAssociatedData());
    }

    [Fact]
    public void ToAssociatedData_DiffersByOrg()
    {
        var a = CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);
        var b = CredentialScope.ForSupplier(OrgB, CredentialPurpose.SupplierDeliveryCredentials, SupX);

        a.ToAssociatedData().Should().NotEqual(b.ToAssociatedData());
    }

    [Fact]
    public void ToAssociatedData_DiffersByScopeId()
    {
        var a = CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);
        var b = CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupY);

        a.ToAssociatedData().Should().NotEqual(b.ToAssociatedData());
    }

    [Fact]
    public void ToAssociatedData_DiffersByPurpose()
    {
        var a = CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);
        var b = CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCxmlSecret, SupX);

        a.ToAssociatedData().Should().NotEqual(b.ToAssociatedData());
    }

    [Fact]
    public void ToAssociatedData_StartsWithTheDomainSeparator()
    {
        var scope = CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgEmailImapPassword);

        Encoding.UTF8.GetString(scope.ToAssociatedData())
            .Should().StartWith("proculink.cred.v2 ");
    }

    [Fact]
    public void ForOrg_UsesEmptyScopeId()
    {
        CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgEmailImapPassword)
            .ScopeId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ToAssociatedData_EmptyOrg_Throws()
    {
        var scope = new CredentialScope(Guid.Empty, CredentialPurpose.OrgEmailImapPassword, Guid.Empty);

        var act = () => scope.ToAssociatedData();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToAssociatedData_PurposeContainingSpace_Throws()
    {
        var scope = new CredentialScope(OrgA, "bad purpose", Guid.Empty);

        var act = () => scope.ToAssociatedData();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Purposes_AreAllDistinct()
    {
        var all = new[]
        {
            CredentialPurpose.SupplierDeliveryCredentials,
            CredentialPurpose.SupplierDeliveryCxmlSecret,
            CredentialPurpose.SupplierCatalogPassword,
            CredentialPurpose.SupplierCatalogAuthConfig,
            CredentialPurpose.OrgIntegrationWebhookSecret,
            CredentialPurpose.OrgEmailImapPassword,
            CredentialPurpose.OrgIngressSftpPassword,
            CredentialPurpose.OrgIngressS3SecretKey,
        };

        all.Should().OnlyHaveUniqueItems();
        all.Should().HaveCount(8);
    }
}
