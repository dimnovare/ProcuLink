using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryEncryptionServiceTests
{
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

        var encrypted = svc.Encrypt(plaintext);
        var decrypted = svc.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputEachCall()
    {
        var svc = CreateService();
        var c1 = svc.Encrypt("same");
        var c2 = svc.Encrypt("same");
        c1.Should().NotBe(c2); // different random IV each time
    }

    [Fact]
    public void Decrypt_WrongKey_ReturnsNull()
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

        var encrypted = svc1.Encrypt("secret");
        var result = svc2.Decrypt(encrypted);

        result.Should().BeNull();
    }

    [Fact]
    public void Decrypt_Garbage_ReturnsNull()
    {
        var svc = CreateService();
        svc.Decrypt("not-valid-base64!!!").Should().BeNull();
    }

    [Fact]
    public void Decrypt_TamperedPayload_ReturnsNull()
    {
        var svc = CreateService();
        var encrypted = svc.Encrypt("{\"type\":\"apikey\",\"value\":\"secret\"}");
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0x01;

        svc.Decrypt(Convert.ToBase64String(bytes)).Should().BeNull();
    }

    [Fact]
    public void Constructor_MissingKey_ThrowsInvalidOperation()
    {
        var config = new ConfigurationBuilder().Build(); // empty
        var act = () => new DeliveryEncryptionService(config);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Decrypt_TooShort_ReturnsNull()
    {
        var svc = CreateService();
        svc.Decrypt(Convert.ToBase64String(new byte[20])).Should().BeNull();
    }
}
