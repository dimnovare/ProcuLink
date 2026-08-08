using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Services;

public class CredentialBindingBackfillTests
{
    private static readonly byte[] Key = new byte[32];

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"rebind-{Guid.NewGuid()}")
            .Options);

    private static DeliveryEncryptionService Enc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(Key),
            })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static CredentialBindingBackfillService Service(ProcuLinkDbContext db) =>
        new(db, Enc(), NullLogger<CredentialBindingBackfillService>.Instance);

    /// <summary>
    /// Builds a version-1 envelope the way rows were written before binding existed. Deliberately
    /// does NOT go through Encrypt, which now always writes version 2 — that is what makes this a
    /// real migration test rather than a round-trip.
    /// </summary>
    private static string LegacyBlob(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag); // no associated data — the point

        var combined = new byte[29 + ciphertext.Length];
        combined[0] = 1;
        nonce.CopyTo(combined, 1);
        tag.CopyTo(combined, 13);
        ciphertext.CopyTo(combined, 29);
        return Convert.ToBase64String(combined);
    }

    private static byte VersionOf(string blob) => Convert.FromBase64String(blob)[0];

    private static readonly Guid OrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SubId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid CatalogId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>
    /// Seeds one row per covered column, each holding a version-1 blob, plus the two EXCLUDED
    /// delivery-credential blobs so their exclusion can be asserted.
    /// Add any further non-nullable properties the compiler demands, copying the values the
    /// neighbouring tests in this folder use for the same entity.
    /// </summary>
    private static async Task SeedAsync(ProcuLinkDbContext db, string deliveryCredsBlob)
    {
        var now = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = OrgId,
            ClerkOrgId = $"org_{OrgId:N}",
            Name = "Rebind Org",
            Slug = $"rebind-{OrgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = now,
            EmailConfigJson = (EmailPollingConfig.Empty with
            {
                Enabled = true,
                Host = "imap.example.com",
                Port = 993,
                Username = "poller@example.com",
                Folder = "INBOX",
                PasswordCiphertext = LegacyBlob("imap-password"),
            }).ToJson(),
        });

        db.IntegrationSubscriptions.Add(new IntegrationSubscription
        {
            Id = SubId,
            OrganisationId = OrgId,
            Platform = "custom",
            EventType = "order.delivered",
            TargetUrl = "https://hooks.example.com/webhook",
            EncryptedSecret = LegacyBlob("webhook-secret"),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.SftpIngressConfigs.Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            EncryptedPassword = LegacyBlob("sftp-password"),
            CreatedAt = now,
        });

        db.S3IngressConfigs.Add(new S3IngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            EncryptedSecretKey = LegacyBlob("s3-secret-key"),
            CreatedAt = now,
        });

        db.SupplierCatalogSources.Add(new SupplierCatalogSource
        {
            Id = CatalogId,
            OrgId = OrgId,
            SupplierId = SupplierId,
            Protocol = "sftp",
            FileFormat = "auto",
            EncryptedPassword = LegacyBlob("catalog-password"),
            AuthConfigEncrypted = LegacyBlob("""{"token":"catalog-auth"}"""),
            CreatedAt = now,
        });

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            SupplierId = SupplierId,
            Protocol = "http",
            EncryptedCxmlSharedSecret = LegacyBlob("cxml-shared-secret"),
            EncryptedCredentials = deliveryCredsBlob,   // EXCLUDED from the backfill
            CreatedAt = now,
        });

        await db.SaveChangesAsync();
    }

    // ── the seven covered columns ────────────────────────────────────────────

    [Fact]
    public async Task Rebind_ConvertsEveryCoveredColumnToVersion2()
    {
        await using var db = NewDb();
        await SeedAsync(db, LegacyBlob("delivery-credentials"));
        var enc = Enc();

        var count = await Service(db).RebindLegacyCredentialsAsync(default);

        count.Should().Be(7, "webhook, sftp, s3, imap, catalog password, catalog auth — cXML makes 7");

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync();
        VersionOf(sub.EncryptedSecret!).Should().Be(2);
        enc.Decrypt(sub.EncryptedSecret!, CredentialScope.ForSupplier(
            OrgId, CredentialPurpose.OrgIntegrationWebhookSecret, SubId))
            .Should().Be("webhook-secret");

        var sftp = await db.SftpIngressConfigs.AsNoTracking().SingleAsync();
        enc.Decrypt(sftp.EncryptedPassword, CredentialScope.ForOrg(
            OrgId, CredentialPurpose.OrgIngressSftpPassword)).Should().Be("sftp-password");

        var s3 = await db.S3IngressConfigs.AsNoTracking().SingleAsync();
        enc.Decrypt(s3.EncryptedSecretKey, CredentialScope.ForOrg(
            OrgId, CredentialPurpose.OrgIngressS3SecretKey)).Should().Be("s3-secret-key");

        var catalog = await db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        enc.Decrypt(catalog.EncryptedPassword!, CredentialScope.ForSupplier(
            OrgId, CredentialPurpose.SupplierCatalogPassword, CatalogId)).Should().Be("catalog-password");
        enc.Decrypt(catalog.AuthConfigEncrypted!, CredentialScope.ForSupplier(
            OrgId, CredentialPurpose.SupplierCatalogAuthConfig, CatalogId))
            .Should().Be("""{"token":"catalog-auth"}""");

        var delivery = await db.SupplierDeliveryConfigs.AsNoTracking().SingleAsync();
        enc.Decrypt(delivery.EncryptedCxmlSharedSecret!, CredentialScope.ForSupplier(
            OrgId, CredentialPurpose.SupplierDeliveryCxmlSecret, SupplierId))
            .Should().Be("cxml-shared-secret");
    }

    [Fact]
    public async Task Rebind_RewritesTheNestedEmailPasswordAndPreservesEveryOtherField()
    {
        await using var db = NewDb();
        await SeedAsync(db, LegacyBlob("delivery-credentials"));

        await Service(db).RebindLegacyCredentialsAsync(default);

        var org = await db.Organisations.AsNoTracking().SingleAsync();
        var config = EmailPollingConfig.FromJson(org.EmailConfigJson);

        VersionOf(config.PasswordCiphertext!).Should().Be(2);
        Enc().Decrypt(config.PasswordCiphertext!, CredentialScope.ForOrg(
            OrgId, CredentialPurpose.OrgEmailImapPassword)).Should().Be("imap-password");

        // Everything around the one rewritten field must survive the JSON round-trip.
        config.Enabled.Should().BeTrue();
        config.Host.Should().Be("imap.example.com");
        config.Port.Should().Be(993);
        config.Username.Should().Be("poller@example.com");
        config.Folder.Should().Be("INBOX");
    }

    [Fact]
    public async Task Rebind_IsIdempotent()
    {
        await using var db = NewDb();
        await SeedAsync(db, LegacyBlob("delivery-credentials"));

        await Service(db).RebindLegacyCredentialsAsync(default);
        var afterFirst = (await db.IntegrationSubscriptions.AsNoTracking().SingleAsync()).EncryptedSecret;

        var secondCount = await Service(db).RebindLegacyCredentialsAsync(default);

        secondCount.Should().Be(0);
        (await db.IntegrationSubscriptions.AsNoTracking().SingleAsync())
            .EncryptedSecret.Should().Be(afterFirst, "a version-2 blob must be skipped, not re-encrypted");
    }

    // ── the exclusion, which is the regression guard for finding 2 ───────────

    [Fact]
    public async Task Rebind_LeavesDeliveryCredentialsByteIdentical()
    {
        await using var db = NewDb();
        var seeded = LegacyBlob("delivery-credentials");
        await SeedAsync(db, seeded);

        await Service(db).RebindLegacyCredentialsAsync(default);

        var delivery = await db.SupplierDeliveryConfigs.AsNoTracking().SingleAsync();
        delivery.EncryptedCredentials.Should().Be(seeded,
            "re-encrypting these breaks the ordinal byte-equality drift check against a revision's " +
            "verbatim CredentialsRef copy, and trips the published-revision immutability trigger");
        VersionOf(delivery.EncryptedCredentials).Should().Be(1);
    }

    // ── the per-row skip path (regression guard for TryRebind's failure isolation) ─────────────

    /// <summary>
    /// One row's SCOPE is unreadable, not its blob: <c>OrganisationId</c> is unset (a data-
    /// integrity anomaly), while <c>EncryptedSecret</c> is a perfectly good legacy envelope.
    /// <c>IsLegacy</c> says yes, so <c>TryRebind</c> runs; <c>Decrypt</c> succeeds (a version-1
    /// blob carries no associated data, so it never inspects the scope); it is the re-<c>Encrypt</c>
    /// call's <c>scope.ToAssociatedData()</c> that rejects <c>OrgId == Guid.Empty</c> and throws
    /// <see cref="ArgumentException"/> — the exact failure the widened <c>TryRebind</c> catch
    /// exists for. Before that widening, this one row's <see cref="ArgumentException"/> escaped
    /// <c>TryRebind</c>, escaped the per-column loop, and unwound
    /// <c>RebindLegacyCredentialsAsync</c> entirely, costing every OTHER row — in every OTHER
    /// table — its migration too, not just the one anomalous row.
    /// </summary>
    [Fact]
    public async Task Rebind_SkipsARowWhoseScopeIsUnreadableWithoutAbortingTheRestOfTheBatch()
    {
        await using var db = NewDb();
        var now = DateTime.UtcNow;

        // Two good rows, in two different covered columns, under the real org id.
        db.SftpIngressConfigs.Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            EncryptedPassword = LegacyBlob("sftp-password"),
            CreatedAt = now,
        });

        db.S3IngressConfigs.Add(new S3IngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            EncryptedSecretKey = LegacyBlob("s3-secret-key"),
            CreatedAt = now,
        });

        // The bad row: a THIRD covered column, with OrganisationId unset. EncryptedSecret is a
        // completely valid legacy envelope — this is not a corrupt blob.
        var badSubId = Guid.NewGuid();
        db.IntegrationSubscriptions.Add(new IntegrationSubscription
        {
            Id = badSubId,
            OrganisationId = Guid.Empty,
            Platform = "custom",
            EventType = "order.delivered",
            TargetUrl = "https://hooks.example.com/webhook",
            EncryptedSecret = LegacyBlob("webhook-secret"),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();

        var count = await Service(db).RebindLegacyCredentialsAsync(default);

        count.Should().Be(2, "the two good rows rebind; the row with no org id is skipped, not counted");

        var sftp = await db.SftpIngressConfigs.AsNoTracking().SingleAsync();
        VersionOf(sftp.EncryptedPassword).Should().Be(2, "a good row in a different column must still migrate");

        var s3 = await db.S3IngressConfigs.AsNoTracking().SingleAsync();
        VersionOf(s3.EncryptedSecretKey).Should().Be(2, "a good row in a different column must still migrate");

        var badSub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync();
        VersionOf(badSub.EncryptedSecret!).Should()
            .Be(1, "the unreadable row must be left untouched, not partially rewritten");
    }
}
