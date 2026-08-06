using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Authenticated encryption for stored credentials — delivery transport credentials, cXML shared
/// secrets, catalog auth, webhook signing secrets, IMAP passwords, and SFTP/S3 ingress secrets.
/// Key: IConfiguration["Delivery:EncryptionKey"] — 32-byte base64 string.
/// Format: base64(version[1] + nonce[12] + tag[16] + ciphertext).
/// Version 2 binds the blob to a <see cref="CredentialScope"/>; version 1 is legacy read-only.
/// </summary>
public class DeliveryEncryptionService
{
    /// <summary>Envelope written before credentials were bound to a tenant. Read-only — never written.</summary>
    private const byte VersionLegacy = 1;

    /// <summary>Envelope bound to a <see cref="CredentialScope"/> via AES-GCM associated data.</summary>
    private const byte VersionBound = 2;

    /// <summary>Kept so the pre-binding overloads below still compile until they are deleted.</summary>
    private const byte Version = VersionLegacy;

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

    /// <summary>
    /// Encrypts with AES-256-GCM, binding the ciphertext to <paramref name="scope"/> as associated
    /// data. Always writes envelope version 2. A blob produced here decrypts ONLY when the same
    /// organisation, purpose, and scope id are presented back.
    /// </summary>
    public string Encrypt(string plaintext, CredentialScope scope)
    {
        var associatedData = scope.ToAssociatedData();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);

        var combined = new byte[HeaderSize + ciphertext.Length];
        combined[0] = VersionBound;
        nonce.CopyTo(combined, 1);
        tag.CopyTo(combined, 1 + NonceSize);
        ciphertext.CopyTo(combined, HeaderSize);

        return Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts an envelope, verifying it was encrypted for <paramref name="scope"/>.
    ///
    /// <para>Reads BOTH versions: version 1 predates binding and carries no associated data, so it
    /// decrypts under any scope; version 2 requires the scope to match. Version 1 exists because
    /// delivery credentials cannot be re-encrypted — <c>SupplierConnectionRevision.CredentialsRef</c>
    /// is a verbatim byte-copy that published-revision immutability freezes.</para>
    ///
    /// <para>Throws rather than returning null. A null silently became "no credentials" at two call
    /// sites and let an unsigned webhook go out.</para>
    /// </summary>
    /// <exception cref="CredentialUnbindableException">
    /// The envelope is malformed, its version is unsupported, or the GCM tag did not verify —
    /// wrong key, corruption, or a blob belonging to a different tenant, purpose, or scope.
    /// </exception>
    public string Decrypt(string base64, CredentialScope scope)
    {
        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new CredentialUnbindableException(
                CredentialFailureReason.MalformedEnvelope, scope, ex);
        }

        if (combined.Length < HeaderSize)
            throw new CredentialUnbindableException(CredentialFailureReason.MalformedEnvelope, scope);

        var version = combined[0];
        if (version is not (VersionLegacy or VersionBound))
            throw new CredentialUnbindableException(CredentialFailureReason.UnknownVersion, scope);

        var nonce = combined[1..(1 + NonceSize)];
        var tag = combined[(1 + NonceSize)..HeaderSize];
        var ciphertext = combined[HeaderSize..];
        var plaintextBytes = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, TagSize);

            if (version == VersionBound)
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, scope.ToAssociatedData());
            else
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }
        catch (CryptographicException ex)
        {
            throw new CredentialUnbindableException(
                CredentialFailureReason.AuthenticationFailed, scope, ex);
        }

        return Encoding.UTF8.GetString(plaintextBytes);
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
