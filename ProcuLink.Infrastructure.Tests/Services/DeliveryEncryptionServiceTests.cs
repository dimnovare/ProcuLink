using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryEncryptionServiceTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupX = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static CredentialScope Scope =>
        CredentialScope.ForSupplier(OrgA, CredentialPurpose.SupplierDeliveryCredentials, SupX);

    private static DeliveryEncryptionService CreateService()
    {
        // 32 zero bytes encoded as base64 — valid test key only
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = key
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    [Fact]
    public void Encrypt_ThenDecrypt_ReturnsOriginalPlaintext()
    {
        var svc = CreateService();
        var plaintext = "{ \"type\": \"apikey\", \"header\": \"X-Api-Key\", \"value\": \"sk-test\" }";

        var encrypted = svc.Encrypt(plaintext, Scope);
        var decrypted = svc.Decrypt(encrypted, Scope);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputEachCall()
    {
        var svc = CreateService();
        var c1 = svc.Encrypt("same", Scope);
        var c2 = svc.Encrypt("same", Scope);
        c1.Should().NotBe(c2); // different random IV each time
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var svc1 = CreateService();
        var key2 = Convert.ToBase64String(new byte[] {
            1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,
            17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32
        });
        var config2 = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key2 })
            .Build();
        var svc2 = new DeliveryEncryptionService(config2);

        var encrypted = svc1.Encrypt("secret", Scope);
        var act = () => svc2.Decrypt(encrypted, Scope);

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void Decrypt_Garbage_Throws()
    {
        var svc = CreateService();
        var act = () => svc.Decrypt("not-valid-base64!!!", Scope);
        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.MalformedEnvelope);
    }

    [Fact]
    public void Decrypt_TamperedPayload_Throws()
    {
        var svc = CreateService();
        var encrypted = svc.Encrypt("{\"type\":\"apikey\",\"value\":\"secret\"}", Scope);
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0x01;

        var act = () => svc.Decrypt(Convert.ToBase64String(bytes), Scope);

        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.AuthenticationFailed);
    }

    [Fact]
    public void Constructor_MissingKey_ThrowsInvalidOperation()
    {
        var config = new ConfigurationBuilder().Build(); // empty
        var act = () => new DeliveryEncryptionService(config);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Decrypt_TooShort_Throws()
    {
        var svc = CreateService();
        var act = () => svc.Decrypt(Convert.ToBase64String(new byte[20]), Scope);
        act.Should().Throw<CredentialUnbindableException>()
            .Which.Reason.Should().Be(CredentialFailureReason.MalformedEnvelope);
    }
}
