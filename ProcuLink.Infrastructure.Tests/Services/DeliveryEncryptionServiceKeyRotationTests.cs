using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The two-key read path. Before it, replacing <c>Delivery:EncryptionKey</c> made every stored
/// credential permanently unreadable — the plaintext is not recoverable outside a decrypt, so there
/// was no migration to run and no way back.
///
/// <para>The fallback is READ-ONLY and must not become a hole: a blob that verifies under the
/// retiring key still has to satisfy its scope binding, and the escape from that is what these
/// tests pin. <b>Every "it reads" assertion is paired with the case that must still fail</b>, or
/// the fallback would be indistinguishable from decrypting with no checks at all.</para>
/// </summary>
public class DeliveryEncryptionServiceKeyRotationTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SupX = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static byte[] KeyOf(byte seed)
    {
        var key = new byte[32];
        Array.Fill(key, seed);
        return key;
    }

    private static readonly byte[] OldKey = KeyOf(0x11);
    private static readonly byte[] NewKey = KeyOf(0x22);

    private static DeliveryEncryptionService Service(byte[] primary, byte[]? previous = null)
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

    private static CredentialScope Scope(Guid orgId, Guid supplierId) =>
        CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId);

    // ── the rotation itself ──────────────────────────────────────────────────

    [Fact]
    public void BlobFromTheRetiringKey_ReadsAfterRotation_AndDoesNotWithoutTheFallback()
    {
        var scope = Scope(OrgA, SupX);
        var written = Service(OldKey).Encrypt("supplier-api-key", scope);

        // PAIRED NEGATIVE — this is the total-loss event the audit described. Without the previous
        // key configured, the new deployment cannot read anything written under the old one.
        var withoutFallback = () => Service(NewKey).Decrypt(written, scope);
        withoutFallback.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);

        Service(NewKey, previous: OldKey).Decrypt(written, scope).Should().Be("supplier-api-key");
    }

    [Fact]
    public void TheFallbackKey_DoesNotBypassTheScopeBinding()
    {
        var written = Service(OldKey).Encrypt("supplier-api-key", Scope(OrgA, SupX));
        var rotated = Service(NewKey, previous: OldKey);

        // Right key generation, wrong tenant. The retiring key must buy the caller a key, never a scope.
        var wrongOrg = () => rotated.Decrypt(written, Scope(OrgB, SupX));
        wrongOrg.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);

        var wrongPurpose = () => rotated.Decrypt(written, CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierDeliveryCxmlSecret, SupX));
        wrongPurpose.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);

        // PAIRED POSITIVE — the correct scope still reads, so the two refusals above are the AAD
        // biting and not the fallback being unreachable.
        rotated.Decrypt(written, Scope(OrgA, SupX)).Should().Be("supplier-api-key");
    }

    [Fact]
    public void TheFallbackKey_DoesNotReopenTheLegacyGuard()
    {
        var legacy = DeliveryEncryptionServiceLegacyGuardTests.LegacyBlob("imap-password", OldKey);
        var rotated = Service(NewKey, previous: OldKey);
        var scope = CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgEmailImapPassword);

        var act = () => rotated.Decrypt(legacy, scope);
        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.UnboundLegacyEnvelopeRefused);

        // PAIRED PROOF — the blob is readable under the retiring key via the migration path, so the
        // refusal is the purpose policy and not the key fallback failing.
        rotated.DecryptDetailed(legacy, scope, LegacyEnvelopeAccess.PermitForMigration)
            .Should().BeEquivalentTo(new
            {
                Plaintext = "imap-password",
                WasUnboundLegacyEnvelope = true,
                WasEncryptedUnderPreviousKey = true,
            });
    }

    [Fact]
    public void Encrypt_AlwaysWritesUnderThePrimaryKey()
    {
        var scope = Scope(OrgA, SupX);
        var rotated = Service(NewKey, previous: OldKey);

        var written = rotated.Encrypt("fresh-secret", scope);

        // A deployment holding only the NEW key reads it — so the write went to the new key.
        Service(NewKey).Decrypt(written, scope).Should().Be("fresh-secret");

        // PAIRED NEGATIVE — a deployment holding only the OLD key cannot, which is what makes the
        // assertion above about the key rather than about the ciphertext being valid in general.
        var oldOnly = () => Service(OldKey).Decrypt(written, scope);
        oldOnly.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void DecryptDetailed_DistinguishesTheKeyThatVerifiedTheBlob()
    {
        var scope = Scope(OrgA, SupX);
        var rotated = Service(NewKey, previous: OldKey);

        var underOld = Service(OldKey).Encrypt("old", scope);
        var underNew = rotated.Encrypt("new", scope);

        rotated.DecryptDetailed(underOld, scope).WasEncryptedUnderPreviousKey.Should().BeTrue();
        rotated.DecryptDetailed(underNew, scope).WasEncryptedUnderPreviousKey.Should().BeFalse();
    }

    // ── configuration ────────────────────────────────────────────────────────

    [Fact]
    public void HasPreviousKey_IsFalseUntilARotationIsConfigured()
    {
        Service(NewKey).HasPreviousKey.Should().BeFalse();
        Service(NewKey, previous: OldKey).HasPreviousKey.Should().BeTrue();
    }

    [Fact]
    public void ABlankPreviousKey_IsTreatedAsAbsentRatherThanAsABootFailure()
    {
        var svc = new DeliveryEncryptionService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(NewKey),
                ["Delivery:PreviousEncryptionKey"] = "   ",
            })
            .Build());

        svc.HasPreviousKey.Should().BeFalse();
    }

    [Fact]
    public void APreviousKeyOfTheWrongLength_FailsTheBootRatherThanBeingIgnored()
    {
        var act = () => new DeliveryEncryptionService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(NewKey),
                ["Delivery:PreviousEncryptionKey"] = Convert.ToBase64String(new byte[16]),
            })
            .Build());

        // Ignoring it would look exactly like a rotation that worked, right up until the first
        // credential written before the rotation failed to decrypt in production.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PreviousEncryptionKey*32 bytes*");
    }

    [Fact]
    public void APreviousKeyThatIsNotBase64_FailsTheBoot()
    {
        var act = () => new DeliveryEncryptionService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(NewKey),
                ["Delivery:PreviousEncryptionKey"] = "not-base64!!!",
            })
            .Build());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PreviousEncryptionKey*base64*");
    }

    [Fact]
    public void APreviousKeyIdenticalToThePrimary_FailsTheBoot()
    {
        var act = () => Service(NewKey, previous: NewKey);

        // Not a rotation, and leaving it configured would keep the "a rotation is in progress" flag
        // permanently true — which makes the backfill decrypt every credential on every boot forever.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*identical*");
    }
}
