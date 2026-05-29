using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// AES-256-GCM IXmlDecryptor for ASP.NET Core DataProtection key persistence.
/// Instantiated by the DataProtection key manager via DI when reading a key
/// previously written by <see cref="AesGcmXmlEncryptor"/>.
/// </summary>
public sealed class AesGcmXmlDecryptor : IXmlDecryptor
{
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmXmlDecryptor(IConfiguration configuration)
    {
        var base64Key = configuration["DataProtection:EncryptionKey"]
            ?? throw new InvalidOperationException(
                "DataProtection:EncryptionKey is not configured. " +
                "A previously-encrypted DataProtection key cannot be read without it.");

        _key = Convert.FromBase64String(base64Key);

        if (_key.Length != 32)
            throw new InvalidOperationException(
                "DataProtection:EncryptionKey must decode to exactly 32 bytes (AES-256).");
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var nonce = Convert.FromBase64String(
            encryptedElement.Element("nonce")?.Value
                ?? throw new InvalidOperationException("Encrypted key XML missing <nonce>."));
        var tag = Convert.FromBase64String(
            encryptedElement.Element("tag")?.Value
                ?? throw new InvalidOperationException("Encrypted key XML missing <tag>."));
        var ciphertext = Convert.FromBase64String(
            encryptedElement.Element("ciphertext")?.Value
                ?? throw new InvalidOperationException("Encrypted key XML missing <ciphertext>."));

        var plaintextBytes = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);

        return XElement.Parse(Encoding.UTF8.GetString(plaintextBytes));
    }
}
