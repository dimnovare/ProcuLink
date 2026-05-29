using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// AES-256-GCM IXmlEncryptor for ASP.NET Core DataProtection key persistence.
/// Key: IConfiguration["DataProtection:EncryptionKey"] — 32-byte base64 string.
/// Output XML: &lt;aesGcmEncryptedKey version="1"&gt;&lt;nonce/&gt;&lt;tag/&gt;&lt;ciphertext/&gt;&lt;/aesGcmEncryptedKey&gt;
/// Pairs with <see cref="AesGcmXmlDecryptor"/>.
/// </summary>
public sealed class AesGcmXmlEncryptor : IXmlEncryptor
{
    internal const string ElementName = "aesGcmEncryptedKey";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmXmlEncryptor(IConfiguration configuration)
    {
        var base64Key = configuration["DataProtection:EncryptionKey"]
            ?? throw new InvalidOperationException(
                "DataProtection:EncryptionKey is not configured. " +
                "Set it to a 32-byte base64 string in app settings or environment variables.");

        _key = Convert.FromBase64String(base64Key);

        if (_key.Length != 32)
            throw new InvalidOperationException(
                "DataProtection:EncryptionKey must decode to exactly 32 bytes (AES-256).");
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var encryptedElement = new XElement(
            ElementName,
            new XAttribute("version", "1"),
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("ciphertext", Convert.ToBase64String(ciphertext)));

        return new EncryptedXmlInfo(encryptedElement, typeof(AesGcmXmlDecryptor));
    }
}
