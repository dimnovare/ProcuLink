using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryEncryptionServiceAadTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SupX = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SupY = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // A version-1 envelope PINNED AS A LITERAL. Written before associated data existed, under a
    // 32-zero-byte key and a 12-zero-byte nonce. It is a literal on purpose: it proves the
    // dual-read path against the real on-disk format rather than against whatever Encrypt
    // happens to produce today. NEVER regenerate this by calling Encrypt.
    private const string LegacyV1Blob =
        "AQAAAAAAAAAAAAAAADztyRtrM1kUslvtWAblj+q1hTREPQVJVCUvtbrRluQ6XkJrr1bCTwbzmNfW" +
        "WEdF5/AB1FFvZsMNIIQsQWU2FJsiJqqC+JYJAUwPeIDZaU49rQ==";

    private const string LegacyV1Plaintext =
        """{"type":"apikey","header":"X-Api-Key","value":"legacy-v1-secret"}""";

    private static DeliveryEncryptionService CreateService(byte[]? key = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(key ?? new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static CredentialScope DeliveryScope(Guid orgId, Guid supplierId) =>
        CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId);

    // ── the two directions the binding must hold in ──────────────────────────

    [Fact]
    public void Decrypt_SameOrgSameSupplier_RoundTrips()
    {
        var svc = CreateService();
        var scope = DeliveryScope(OrgA, SupX);

        var blob = svc.Encrypt("supplier-api-key", scope);

        svc.Decrypt(blob, scope).Should().Be("supplier-api-key");
    }

    [Fact]
    public void Decrypt_DifferentOrg_Throws()
    {
        var svc = CreateService();
        var blob = svc.Encrypt("supplier-api-key", DeliveryScope(OrgA, SupX));

        var act = () => svc.Decrypt(blob, DeliveryScope(OrgB, SupX));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void Decrypt_DifferentSupplier_Throws()
    {
        var svc = CreateService();
        var blob = svc.Encrypt("supplier-api-key", DeliveryScope(OrgA, SupX));

        var act = () => svc.Decrypt(blob, DeliveryScope(OrgA, SupY));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void Decrypt_DifferentPurposeSameSupplier_Throws()
    {
        var svc = CreateService();
        var blob = svc.Encrypt("supplier-api-key", DeliveryScope(OrgA, SupX));
        var cxmlScope = CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierDeliveryCxmlSecret, SupX);

        var act = () => svc.Decrypt(blob, cxmlScope);

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void Decrypt_OrgScopedBlob_DoesNotDecryptAsADifferentOrgScopedPurpose()
    {
        var svc = CreateService();
        var blob = svc.Encrypt(
            "imap-password", CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgEmailImapPassword));

        var act = () => svc.Decrypt(
            blob, CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgIngressSftpPassword));

        act.Should().Throw<CredentialUnbindableException>();
    }

    // ── envelope versioning ──────────────────────────────────────────────────

    [Fact]
    public void Encrypt_WritesEnvelopeVersion2()
    {
        var svc = CreateService();

        var blob = svc.Encrypt("anything", DeliveryScope(OrgA, SupX));

        Convert.FromBase64String(blob)[0].Should().Be(2);
    }

    [Fact]
    public void Decrypt_LegacyVersion1Blob_ReadsUnderAnyScope()
    {
        var svc = CreateService();

        svc.Decrypt(LegacyV1Blob, DeliveryScope(OrgA, SupX)).Should().Be(LegacyV1Plaintext);
    }

    // A version-1 blob carries no binding at all, so it reads under any scope. That is the
    // accepted residual, pinned here so it is a decision rather than a surprise.
    [Fact]
    public void Decrypt_LegacyVersion1Blob_IsNotBoundToAnyOrg()
    {
        var svc = CreateService();

        svc.Decrypt(LegacyV1Blob, DeliveryScope(OrgB, SupY)).Should().Be(LegacyV1Plaintext);
    }

    [Fact]
    public void Decrypt_UnknownVersionByte_Throws()
    {
        var svc = CreateService();
        var bytes = Convert.FromBase64String(svc.Encrypt("x", DeliveryScope(OrgA, SupX)));
        bytes[0] = 7;

        var act = () => svc.Decrypt(Convert.ToBase64String(bytes), DeliveryScope(OrgA, SupX));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.UnknownVersion);
    }

    // ── malformed and wrong-key inputs ───────────────────────────────────────

    [Fact]
    public void Decrypt_NotBase64_ThrowsMalformed()
    {
        var svc = CreateService();

        var act = () => svc.Decrypt("not-valid-base64!!!", DeliveryScope(OrgA, SupX));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.MalformedEnvelope);
    }

    [Fact]
    public void Decrypt_TooShort_ThrowsMalformed()
    {
        var svc = CreateService();

        var act = () => svc.Decrypt(Convert.ToBase64String(new byte[20]), DeliveryScope(OrgA, SupX));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.MalformedEnvelope);
    }

    [Fact]
    public void Decrypt_WrongKey_ThrowsAuthenticationFailed()
    {
        var scope = DeliveryScope(OrgA, SupX);
        var blob = CreateService().Encrypt("secret", scope);

        var otherKey = new byte[32];
        otherKey[0] = 9;
        var act = () => CreateService(otherKey).Decrypt(blob, scope);

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsAuthenticationFailed()
    {
        var svc = CreateService();
        var scope = DeliveryScope(OrgA, SupX);
        var bytes = Convert.FromBase64String(svc.Encrypt("secret", scope));
        bytes[^1] ^= 0x01;

        var act = () => svc.Decrypt(Convert.ToBase64String(bytes), scope);

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void Exception_MessageDoesNotLeakPlaintext()
    {
        var svc = CreateService();
        var blob = svc.Encrypt("super-secret-value", DeliveryScope(OrgA, SupX));

        var act = () => svc.Decrypt(blob, DeliveryScope(OrgB, SupX));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Message.Should().NotContain("super-secret-value");
    }
}
