using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The downgrade guard: a version-1 envelope carries no associated data, so it decrypts under ANY
/// organisation, purpose, and scope id. That makes the ciphertext portable — one lifted from a
/// backup, a support export, or a log can be written into another row and read back. The guard
/// refuses version 1 for every purpose except the one that provably still holds legacy rows.
///
/// <para><b>Every refusal below is paired with a proof that the same blob IS readable</b> — under
/// the migration access mode, or under the exempt purpose. Without that pairing a refusal test
/// passes just as well when the blob was never decryptable in the first place, and would still pass
/// if the guard were deleted and replaced by a typo in the fixture.</para>
/// </summary>
public class DeliveryEncryptionServiceLegacyGuardTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SupX = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly byte[] Key = new byte[32];

    private const string Secret = "unbound-legacy-plaintext";

    private static DeliveryEncryptionService CreateService(bool allowLegacyEverywhere = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(Key),
        };
        if (allowLegacyEverywhere)
            settings["Delivery:AllowUnboundLegacyCredentials"] = "true";

        return new DeliveryEncryptionService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    /// <summary>
    /// Builds a version-1 envelope the way rows were written before binding existed: AES-GCM with
    /// NO associated data. Deliberately not produced by <c>Encrypt</c>, which has only ever written
    /// version 2 — going through Encrypt would test the guard against a blob the guard is not for.
    /// </summary>
    internal static string LegacyBlob(string plaintext, byte[]? key = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key ?? Key, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var combined = new byte[29 + ciphertext.Length];
        combined[0] = 1;
        nonce.CopyTo(combined, 1);
        tag.CopyTo(combined, 13);
        ciphertext.CopyTo(combined, 29);
        return Convert.ToBase64String(combined);
    }

    /// <summary>Every purpose constant, read off the type so a new one cannot be missed here.</summary>
    internal static IReadOnlyList<string> AllPurposes() =>
        typeof(CredentialPurpose)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    public static TheoryData<string> NonExemptPurposes()
    {
        var data = new TheoryData<string>();
        foreach (var purpose in AllPurposes().Where(p => !CredentialPurpose.AllowsUnboundLegacyEnvelope(p)))
            data.Add(purpose);
        return data;
    }

    // ── the policy itself ────────────────────────────────────────────────────

    [Fact]
    public void ExactlyOnePurpose_StillAcceptsAnUnboundLegacyEnvelope()
    {
        var all = AllPurposes();

        // Anti-vacuity: if reflection stopped finding the constants, the exemption assertions below
        // would be checking an empty list and pass for the wrong reason.
        all.Should().HaveCountGreaterThanOrEqualTo(9, "every credential purpose must be discovered");
        all.Should().Contain(CredentialPurpose.SupplierDeliveryCredentials);
        all.Should().Contain(CredentialPurpose.OrgEmailImapPassword);

        all.Where(CredentialPurpose.AllowsUnboundLegacyEnvelope).Should()
            .ContainSingle(
                "delivery credentials are the only blobs the binding backfill cannot rewrite, because " +
                "SupplierConnectionRevision.CredentialsRef is a frozen byte-copy of them")
            .Which.Should().Be(CredentialPurpose.SupplierDeliveryCredentials);
    }

    [Fact]
    public void AnUnrecognisedPurpose_DoesNotAcceptAnUnboundLegacyEnvelope()
    {
        CredentialPurpose.AllowsUnboundLegacyEnvelope("something.invented").Should().BeFalse();
        CredentialPurpose.AllowsUnboundLegacyEnvelope(null).Should().BeFalse();
        CredentialPurpose.AllowsUnboundLegacyEnvelope("").Should().BeFalse();
    }

    // ── refusal, each paired with a readability proof ────────────────────────

    [Theory]
    [MemberData(nameof(NonExemptPurposes))]
    public void LegacyEnvelope_IsRefused_ForEveryNonExemptPurpose(string purpose)
    {
        var svc = CreateService();
        var blob = LegacyBlob(Secret);
        var scope = CredentialScope.ForSupplier(OrgA, purpose, SupX);

        var act = () => svc.Decrypt(blob, scope);

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.UnboundLegacyEnvelopeRefused);

        // PAIRED PROOF — the blob is perfectly decryptable. The refusal above is the guard doing its
        // job, not a broken fixture, a wrong key, or a corrupt envelope.
        svc.DecryptDetailed(blob, scope, LegacyEnvelopeAccess.PermitForMigration)
            .Plaintext.Should().Be(Secret);
    }

    /// <summary>
    /// The finding in one test. Before the guard, a version-1 blob minted for one tenant read back
    /// under a completely different tenant, purpose, and scope — so a stolen ciphertext could be
    /// replayed into a row the attacker controls and the plaintext exfiltrated through an ordinary
    /// delivery. Every non-exempt purpose now refuses it.
    /// </summary>
    [Fact]
    public void LegacyEnvelope_CannotBeReplayedIntoAnotherTenantsRow()
    {
        var svc = CreateService();
        var stolen = LegacyBlob(Secret);

        var act = () => svc.Decrypt(
            stolen, CredentialScope.ForOrg(OrgB, CredentialPurpose.OrgEmailImapPassword));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.UnboundLegacyEnvelopeRefused);

        // …and the bound envelope is refused across tenants too, for a different reason: the AAD.
        // Both directions have to hold, or "scope-inert" simply moved rather than closed.
        var bound = svc.Encrypt(
            Secret, CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgEmailImapPassword));

        var boundAct = () => svc.Decrypt(
            bound, CredentialScope.ForOrg(OrgB, CredentialPurpose.OrgEmailImapPassword));

        boundAct.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void RefusalMessage_DoesNotLeakThePlaintext()
    {
        var svc = CreateService();
        var blob = LegacyBlob(Secret);

        var act = () => svc.Decrypt(
            blob, CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgIngressS3SecretKey));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Message.Should().NotContain(Secret);
    }

    // ── the exemption, and what keeps it honest ──────────────────────────────

    [Fact]
    public void LegacyEnvelope_IsStillAccepted_ForDeliveryCredentials()
    {
        var svc = CreateService();
        var blob = LegacyBlob(Secret);
        var scope = CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);

        svc.Decrypt(blob, scope).Should().Be(Secret);
    }

    /// <summary>
    /// The exemption is the residual the audit named, pinned so it stays a decision. A version-1
    /// delivery credential reads under ANY org and ANY supplier, because it carries no binding at
    /// all. It cannot be closed in code — only by an operator re-saving the delivery config, which
    /// rewrites the live blob and the revision byte-copy together.
    /// </summary>
    [Fact]
    public void LegacyDeliveryCredential_IsStillTenantPortable_TheKnownResidual()
    {
        var svc = CreateService();
        var blob = LegacyBlob(Secret);

        svc.Decrypt(blob, CredentialScope.ForSupplier(
            OrgB, CredentialPurpose.SupplierDeliveryCredentials, Guid.NewGuid()))
            .Should().Be(Secret);
    }

    [Fact]
    public void BoundDeliveryCredential_IsNotPortable_SoTheExemptionEndsAtVersionOne()
    {
        var svc = CreateService();
        var bound = svc.Encrypt(Secret, CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX));

        var act = () => svc.Decrypt(bound, CredentialScope.ForSupplier(
            OrgB, CredentialPurpose.SupplierDeliveryCredentials, SupX));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    // ── the escape hatch ─────────────────────────────────────────────────────

    [Fact]
    public void EscapeHatch_RestoresLegacyReadsForEveryPurpose()
    {
        var blob = LegacyBlob(Secret);
        var scope = CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgEmailImapPassword);

        // Off by default — the refusal is what has to be the default, not the tolerance.
        var strict = () => CreateService().Decrypt(blob, scope);
        strict.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.UnboundLegacyEnvelopeRefused);

        CreateService(allowLegacyEverywhere: true).Decrypt(blob, scope).Should().Be(Secret);
    }

    [Fact]
    public void EscapeHatch_DoesNotWeakenTheBoundEnvelope()
    {
        var svc = CreateService(allowLegacyEverywhere: true);
        var bound = svc.Encrypt(Secret, CredentialScope.ForOrg(OrgA, CredentialPurpose.OrgEmailImapPassword));

        var act = () => svc.Decrypt(bound, CredentialScope.ForOrg(OrgB, CredentialPurpose.OrgEmailImapPassword));

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    // ── DecryptDetailed reports the envelope version ─────────────────────────

    [Fact]
    public void DecryptDetailed_ReportsWhetherTheEnvelopeWasUnbound()
    {
        var svc = CreateService();
        var scope = CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);

        svc.DecryptDetailed(LegacyBlob(Secret), scope)
            .WasUnboundLegacyEnvelope.Should().BeTrue();

        svc.DecryptDetailed(svc.Encrypt(Secret, scope), scope)
            .WasUnboundLegacyEnvelope.Should().BeFalse();
    }
}
