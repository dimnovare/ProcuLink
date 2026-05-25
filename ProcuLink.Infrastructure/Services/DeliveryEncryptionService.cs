using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Authenticated encryption for delivery credentials.
/// Key: IConfiguration["Delivery:EncryptionKey"] — 32-byte base64 string.
/// Format: base64(version[1] + nonce[12] + tag[16] + ciphertext).
/// </summary>
public class DeliveryEncryptionService
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private readonly byte[] _key;

    public DeliveryEncryptionService(IConfiguration configuration)
    {
        var base64Key = configuration["Delivery:EncryptionKey"]
            ?? throw new InvalidOperationException(
                "Delivery:EncryptionKey is not configured. " +
                "Set it to a 32-byte base64 string in app settings or environment variables.");

        _key = Convert.FromBase64String(base64Key);

        if (_key.Length != 32)
            throw new InvalidOperationException(
                "Delivery:EncryptionKey must decode to exactly 32 bytes (AES-256).");
    }

    /// <summary>Encrypts plaintext using AES-256-GCM with a random nonce.</summary>
    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var combined = new byte[HeaderSize + ciphertext.Length];
        combined[0] = Version;
        nonce.CopyTo(combined, 1);
        tag.CopyTo(combined, 1 + NonceSize);
        ciphertext.CopyTo(combined, HeaderSize);

        return Convert.ToBase64String(combined);
    }

    /// <summary>Decrypts base64(version+nonce+tag+ciphertext). Returns null on any error — never throws.</summary>
    public string? Decrypt(string base64)
    {
        try
        {
            var combined = Convert.FromBase64String(base64);
            if (combined.Length < HeaderSize) return null;
            if (combined[0] != Version) return null;

            var nonce = combined[1..(1 + NonceSize)];
            var tag = combined[(1 + NonceSize)..HeaderSize];
            var ciphertext = combined[HeaderSize..];
            var plaintextBytes = new byte[ciphertext.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch
        {
            return null;
        }
    }
}
