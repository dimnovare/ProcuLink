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

    // ── production write-path round-trips ────────────────────────────────────
    // Each writes through the REAL production write path, then decrypts with the exact tuple the
    // matching read site constructs (EmailPollOrgJob.cs:145, SftpIngressService.cs:103,
    // S3IngressService.cs:109). A wrong purpose on either side shows up here rather than as a
    // silently skipped poll in production.
    //
    // These document that each settings service writes something the matching read site can read,
    // but they do NOT go red before the change: a version-1 envelope never passes scope into
    // AesGcm.Decrypt at all, so scope is cryptographically inert for v1, and a positive plaintext
    // assertion cannot tell bound from unbound. The wrong-org tests below are the red-green driver.

    [Fact]
    public async Task SaveEmailSettings_ThenDecryptWithThePollerScope_RoundTrips()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var orgId = await SeedOrgAsync(db);

        await new EmailSettingsService(db, enc).UpdateAsync(
            orgId,
            new UpdateEmailSettingsRequest(
                Enabled: true,
                Host: "imap.example.com",
                Port: 993,
                UseSsl: true,
                Username: "orders@example.com",
                Password: "imap-password",
                Folder: "INBOX",
                DefaultSupplierId: null),
            CancellationToken.None);

        var org = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == orgId);
        var config = EmailPollingConfig.FromJson(org.EmailConfigJson);

        enc.Decrypt(config.PasswordCiphertext!, CredentialScope.ForOrg(
            orgId, CredentialPurpose.OrgEmailImapPassword)).Should().Be("imap-password");
    }

    [Fact]
    public async Task SaveSftpIngress_ThenDecryptWithThePollerScope_RoundTrips()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var orgId = await SeedOrgAsync(db);

        await new PullIngressSettingsService(db, enc).UpdateSftpAsync(
            orgId,
            new UpdateSftpIngressRequest(
                Enabled: true,
                Host: "sftp.supplier.example",
                Port: 22,
                Username: "buyer",
                Password: "sftp-password",
                RemoteDirectory: "/incoming/orders",
                DefaultSupplierId: null),
            CancellationToken.None);

        var cfg = await db.SftpIngressConfigs.AsNoTracking().SingleAsync(c => c.OrgId == orgId);

        enc.Decrypt(cfg.EncryptedPassword, CredentialScope.ForOrg(
            orgId, CredentialPurpose.OrgIngressSftpPassword)).Should().Be("sftp-password");
    }

    [Fact]
    public async Task SaveS3Ingress_ThenDecryptWithThePollerScope_RoundTrips()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var orgId = await SeedOrgAsync(db);

        await new PullIngressSettingsService(db, enc).UpdateS3Async(
            orgId,
            new UpdateS3IngressRequest(
                Enabled: true,
                BucketName: "orders-bucket",
                KeyPrefix: "incoming/",
                Region: "eu-west-1",
                AccessKeyId: "AKIA123",
                SecretKey: "s3-secret-key",
                DefaultSupplierId: null),
            CancellationToken.None);

        var cfg = await db.S3IngressConfigs.AsNoTracking().SingleAsync(c => c.OrgId == orgId);

        enc.Decrypt(cfg.EncryptedSecretKey, CredentialScope.ForOrg(
            orgId, CredentialPurpose.OrgIngressS3SecretKey)).Should().Be("s3-secret-key");
    }

    // ── the red-green drivers ────────────────────────────────────────────────
    // These are what actually fail before the change. A v1 blob decrypts under ANY scope, so
    // "decrypting under the wrong org succeeds" is exactly the pre-change state, and the expected
    // throw never arrives. After the change the blob is v2 and bound, so the wrong org is refused.

    [Fact]
    public async Task SaveEmailSettings_ThenDecryptUnderAWrongOrg_Throws()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var orgId = await SeedOrgAsync(db);

        await new EmailSettingsService(db, enc).UpdateAsync(
            orgId,
            new UpdateEmailSettingsRequest(
                Enabled: true,
                Host: "imap.example.com",
                Port: 993,
                UseSsl: true,
                Username: "orders@example.com",
                Password: "imap-password",
                Folder: "INBOX",
                DefaultSupplierId: null),
            CancellationToken.None);

        var org = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == orgId);
        var config = EmailPollingConfig.FromJson(org.EmailConfigJson);

        var act = () => enc.Decrypt(config.PasswordCiphertext!, CredentialScope.ForOrg(
            Guid.NewGuid(), CredentialPurpose.OrgEmailImapPassword));

        act.Should().Throw<CredentialUnbindableException>();
    }

    [Fact]
    public async Task SaveSftpIngress_ThenDecryptUnderAWrongOrg_Throws()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var orgId = await SeedOrgAsync(db);

        await new PullIngressSettingsService(db, enc).UpdateSftpAsync(
            orgId,
            new UpdateSftpIngressRequest(
                Enabled: true,
                Host: "sftp.supplier.example",
                Port: 22,
                Username: "buyer",
                Password: "sftp-password",
                RemoteDirectory: "/incoming/orders",
                DefaultSupplierId: null),
            CancellationToken.None);

        var cfg = await db.SftpIngressConfigs.AsNoTracking().SingleAsync(c => c.OrgId == orgId);

        var act = () => enc.Decrypt(cfg.EncryptedPassword, CredentialScope.ForOrg(
            Guid.NewGuid(), CredentialPurpose.OrgIngressSftpPassword));

        act.Should().Throw<CredentialUnbindableException>();
    }

    [Fact]
    public async Task SaveS3Ingress_ThenDecryptUnderAWrongOrg_Throws()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var orgId = await SeedOrgAsync(db);

        await new PullIngressSettingsService(db, enc).UpdateS3Async(
            orgId,
            new UpdateS3IngressRequest(
                Enabled: true,
                BucketName: "orders-bucket",
                KeyPrefix: "incoming/",
                Region: "eu-west-1",
                AccessKeyId: "AKIA123",
                SecretKey: "s3-secret-key",
                DefaultSupplierId: null),
            CancellationToken.None);

        var cfg = await db.S3IngressConfigs.AsNoTracking().SingleAsync(c => c.OrgId == orgId);

        var act = () => enc.Decrypt(cfg.EncryptedSecretKey, CredentialScope.ForOrg(
            Guid.NewGuid(), CredentialPurpose.OrgIngressS3SecretKey));

        act.Should().Throw<CredentialUnbindableException>();
    }
}
