using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// AES-256-GCM IXmlDecryptor for ASP.NET Core DataProtection key persistence.
/// Instantiated by the DataProtection key manager through its own activator — NOT
/// through DI, however it is registered — when reading a key previously written by
/// <see cref="AesGcmXmlEncryptor"/>. See the constructors.
/// </summary>
public sealed class AesGcmXmlDecryptor : IXmlDecryptor
{
    private const int TagSize = 16;

    private readonly byte[] _key;

    /// <summary>
    /// The constructor DataProtection can actually call, and the reason this class
    /// has two.
    ///
    /// <para>The key ring records this type BY NAME (see
    /// <see cref="AesGcmXmlEncryptor.Encrypt"/>, which returns
    /// <c>new EncryptedXmlInfo(…, typeof(AesGcmXmlDecryptor))</c>). Reading a key
    /// back therefore goes through <c>TypeForwardingActivator</c>, which supports
    /// exactly two shapes: a parameterless constructor, or this one. It does NOT
    /// resolve the type from the service collection — registering it there has no
    /// effect on this path at all.</para>
    ///
    /// <para>This class previously had only the <see cref="IConfiguration"/>
    /// constructor below, so activation fell through to the parameterless case and
    /// threw. Production, 2026-08-28 16:08:27 UTC:
    /// <c>MissingMethodException: Cannot dynamically create an instance of type
    /// 'AesGcmXmlDecryptor'. Reason: No parameterless constructor defined.</c>
    /// The key ring could not be built while encryption-at-rest was enabled.</para>
    /// </summary>
    public AesGcmXmlDecryptor(IServiceProvider services)
        : this((services ?? throw new ArgumentNullException(nameof(services)))
            .GetService(typeof(IConfiguration)) as IConfiguration
            ?? throw new InvalidOperationException(
                "No IConfiguration is available to read DataProtection:EncryptionKey."))
    {
    }

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
