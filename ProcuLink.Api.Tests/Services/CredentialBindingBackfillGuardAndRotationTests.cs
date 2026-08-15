using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// How the backfill composes with the two things added alongside it: the downgrade guard, which
/// refuses version-1 envelopes for every purpose the backfill covers, and the retiring-key read
/// path, which lets a key rotation drain those same columns.
///
/// <para>The guard and the backfill are load-bearing for each other. The guard is only safe because
/// the backfill migrates the columns it refuses; the backfill is only able to migrate them because
/// it reads with <see cref="LegacyEnvelopeAccess.PermitForMigration"/>, which is the one exemption
/// from the guard. Each test below therefore asserts the BEFORE state as well as the after — a
/// migration test that only checks the end state passes just as well when nothing needed
/// migrating.</para>
/// </summary>
public class CredentialBindingBackfillGuardAndRotationTests
{
    private static readonly byte[] OldKey = Filled(0x11);
    private static readonly byte[] NewKey = Filled(0x22);

    private static readonly Guid OrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static byte[] Filled(byte seed)
    {
        var key = new byte[32];
        Array.Fill(key, seed);
        return key;
    }

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"rebind-guard-{Guid.NewGuid()}")
            .Options);

    private static DeliveryEncryptionService Enc(byte[] primary, byte[]? previous = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(primary),
        };
        if (previous is not null)
            settings["Delivery:PreviousEncryptionKey"] = Convert.ToBase64String(previous);

        return new DeliveryEncryptionService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static CredentialBindingBackfillService Service(
        ProcuLinkDbContext db, DeliveryEncryptionService enc) =>
        new(db, enc, NullLogger<CredentialBindingBackfillService>.Instance);

    /// <summary>A pre-binding version-1 envelope: AES-GCM with no associated data.</summary>
    private static string LegacyBlob(string plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var combined = new byte[29 + ciphertext.Length];
        combined[0] = 1;
        nonce.CopyTo(combined, 1);
        tag.CopyTo(combined, 13);
        ciphertext.CopyTo(combined, 29);
        return Convert.ToBase64String(combined);
    }

    private static CredentialScope SftpScope =>
        CredentialScope.ForOrg(OrgId, CredentialPurpose.OrgIngressSftpPassword);

    private static async Task<Guid> SeedSftpAsync(ProcuLinkDbContext db, string blob)
    {
        var id = Guid.NewGuid();
        db.SftpIngressConfigs.Add(new SftpIngressConfig
        {
            Id = id,
            OrgId = OrgId,
            EncryptedPassword = blob,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── guard ⇄ backfill ─────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_MigratesTheVeryColumnsWhoseProductionReadsNowRefuseLegacy()
    {
        await using var db = NewDb();
        await SeedSftpAsync(db, LegacyBlob("sftp-password", OldKey));
        var enc = Enc(OldKey);

        // BEFORE — the ingress password is unreadable through the ordinary production path. This is
        // the guard, and it is exactly why the backfill has to exist rather than being optional.
        var before = await db.SftpIngressConfigs.AsNoTracking().SingleAsync();
        var beforeRead = () => enc.Decrypt(before.EncryptedPassword, SftpScope);
        beforeRead.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.UnboundLegacyEnvelopeRefused);

        var count = await Service(db, enc).RebindLegacyCredentialsAsync(default);

        count.Should().Be(1);
        var after = await db.SftpIngressConfigs.AsNoTracking().SingleAsync();
        Convert.FromBase64String(after.EncryptedPassword)[0].Should().Be(2);
        enc.Decrypt(after.EncryptedPassword, SftpScope).Should().Be("sftp-password");

        // …and the migrated blob is genuinely bound, not merely re-stamped: another tenant's scope
        // must not read it. Without this the "version 2" assertion above would pass on a blob that
        // was still portable.
        var otherTenant = () => enc.Decrypt(
            after.EncryptedPassword,
            CredentialScope.ForOrg(Guid.NewGuid(), CredentialPurpose.OrgIngressSftpPassword));
        otherTenant.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    // ── rotation drain ───────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_MovesCoveredColumnsOntoTheNewKeyDuringARotation()
    {
        await using var db = NewDb();
        var writtenUnderOldKey = Enc(OldKey).Encrypt("sftp-password", SftpScope);
        await SeedSftpAsync(db, writtenUnderOldKey);

        var rotated = Enc(NewKey, previous: OldKey);

        // BEFORE — a deployment holding only the new key cannot read it. That is the total-loss
        // state a rotation used to leave behind permanently.
        var beforeRead = () => Enc(NewKey).Decrypt(writtenUnderOldKey, SftpScope);
        beforeRead.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);

        var count = await Service(db, rotated).RebindLegacyCredentialsAsync(default);

        count.Should().Be(1, "an already-bound blob under the retiring key still needs rewriting");
        var after = await db.SftpIngressConfigs.AsNoTracking().SingleAsync();

        Enc(NewKey).Decrypt(after.EncryptedPassword, SftpScope).Should().Be("sftp-password");

        // PAIRED NEGATIVE — the old key no longer reads it, so the row really moved rather than the
        // assertion above passing on an unchanged blob.
        var oldKeyRead = () => Enc(OldKey).Decrypt(after.EncryptedPassword, SftpScope);
        oldKeyRead.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public async Task Backfill_RewritesNothingWhenEveryBlobIsAlreadyBoundUnderThePrimaryKey()
    {
        await using var db = NewDb();
        var enc = Enc(NewKey);
        await SeedSftpAsync(db, enc.Encrypt("sftp-password", SftpScope));

        var original = (await db.SftpIngressConfigs.AsNoTracking().SingleAsync()).EncryptedPassword;

        // Both with and without a rotation configured: the rotation-aware path attempts every row,
        // so it is the path most likely to churn bytes it should have left alone. A random nonce
        // means any needless re-encryption is visible as a changed blob.
        (await Service(db, enc).RebindLegacyCredentialsAsync(default)).Should().Be(0);
        (await db.SftpIngressConfigs.AsNoTracking().SingleAsync())
            .EncryptedPassword.Should().Be(original);

        (await Service(db, Enc(NewKey, previous: OldKey)).RebindLegacyCredentialsAsync(default))
            .Should().Be(0);
        (await db.SftpIngressConfigs.AsNoTracking().SingleAsync())
            .EncryptedPassword.Should().Be(original);
    }

    // ── the exclusion holds during a rotation too ────────────────────────────

    [Fact]
    public async Task Backfill_StillLeavesDeliveryCredentialsUntouchedDuringARotation()
    {
        await using var db = NewDb();
        var deliveryScope = CredentialScope.ForSupplier(
            OrgId, CredentialPurpose.SupplierDeliveryCredentials, SupplierId);

        var liveBlob = Enc(OldKey).Encrypt("delivery-credentials", deliveryScope);
        var legacyRef = LegacyBlob("delivery-credentials", OldKey);

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            SupplierId = SupplierId,
            Protocol = "http",
            EncryptedCredentials = liveBlob,
            CreatedAt = DateTime.UtcNow,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            CredentialsRef = legacyRef,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await Service(db, Enc(NewKey, previous: OldKey)).RebindLegacyCredentialsAsync(default);

        // Byte-for-byte unchanged in BOTH shapes — a bound blob under the retiring key, and an
        // unbound legacy one. The revision copy is compared by ordinal equality and frozen on
        // published rows, so rewriting either side reports permanent drift or raises P0001.
        (await db.SupplierDeliveryConfigs.AsNoTracking().SingleAsync())
            .EncryptedCredentials.Should().Be(liveBlob);
        (await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync())
            .CredentialsRef.Should().Be(legacyRef);
    }
}
