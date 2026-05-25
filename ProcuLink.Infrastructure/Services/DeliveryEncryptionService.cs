using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// AES-256-CBC encryption for delivery credentials.
/// Key: IConfiguration["Delivery:EncryptionKey"] — 32-byte base64 string.
/// Format: base64(iv[16] + ciphertext).
/// </summary>
public class DeliveryEncryptionService
{
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

    /// <summary>Encrypts plaintext using AES-256-CBC with a random IV. Returns base64(iv+ciphertext).</summary>
    public string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        var combined = new byte[16 + ciphertext.Length];
        aes.IV.CopyTo(combined, 0);
        ciphertext.CopyTo(combined, 16);

        return Convert.ToBase64String(combined);
    }

    /// <summary>Decrypts base64(iv+ciphertext). Returns null on any error — never throws.</summary>
    public string? Decrypt(string base64)
    {
        try
        {
            var combined = Convert.FromBase64String(base64);
            if (combined.Length < 32) return null; // must contain IV (16 bytes) + at least one full CBC block (16 bytes)

            var iv         = combined[..16];
            var ciphertext = combined[16..];

            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV  = iv;

            using var decryptor = aes.CreateDecryptor();
            var plaintextBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch
        {
            return null;
        }
    }
}
