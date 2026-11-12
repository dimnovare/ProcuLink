using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Task 6 — the three organisation-singleton credentials (IMAP password, SFTP ingress password, S3
/// ingress secret key) bound via <c>CredentialScope.ForOrg</c>. All three share Guid.Empty as their
/// scope id — one config per org — so ONLY the purpose keeps them apart. Without it an IMAP password
/// blob would decrypt as an SFTP password blob within the same org.
/// </summary>
public class OrgScopedCredentialBindingTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-2222-2222-222222222222");

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
            .UseInMemoryDatabase($"org-cred-binding-{Guid.NewGuid()}")
            .Options);

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Binding Org",
            Slug = $"binding-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    public static TheoryData<string> OrgScopedPurposes => new()
    {
        CredentialPurpose.OrgEmailImapPassword,
        CredentialPurpose.OrgIngressSftpPassword,
        CredentialPurpose.OrgIngressS3SecretKey,
    };

    [Theory]
    [MemberData(nameof(OrgScopedPurposes))]
    public void OrgScopedCredential_DoesNotDecryptForAnotherOrg(string purpose)
    {
        var enc = Encryption();
        var blob = enc.Encrypt("the-secret", CredentialScope.ForOrg(OrgA, purpose));

        var act = () => enc.Decrypt(blob, CredentialScope.ForOrg(OrgB, purpose));

        act.Should().Throw<CredentialUnbindableException>();
    }

    // The three org singletons share Guid.Empty as their scope id, so ONLY the purpose separates
    // them. Without it an IMAP password blob would decrypt as an SFTP password blob.
    [Theory]
    [InlineData(CredentialPurpose.OrgEmailImapPassword, CredentialPurpose.OrgIngressSftpPassword)]
    [InlineData(CredentialPurpose.OrgIngressSftpPassword, CredentialPurpose.OrgIngressS3SecretKey)]
    [InlineData(CredentialPurpose.OrgIngressS3SecretKey, CredentialPurpose.OrgEmailImapPassword)]
    public void OrgScopedCredential_DoesNotDecryptAsADifferentPurpose(string written, string read)
    {
        var enc = Encryption();
        var blob = enc.Encrypt("the-secret", CredentialScope.ForOrg(OrgA, written));

        var act = () => enc.Decrypt(blob, CredentialScope.ForOrg(OrgA, read));

        act.Should().Throw<CredentialUnbindableException>();
    }
}
