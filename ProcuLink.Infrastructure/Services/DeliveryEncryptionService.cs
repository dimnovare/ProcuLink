using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services;

/// <summary>How a caller wants version-1 (unbound, scope-inert) envelopes handled.</summary>
public enum LegacyEnvelopeAccess
{
    /// <summary>
    /// The default, and what every production read uses. A version-1 envelope is accepted only for
    /// the purposes listed in <see cref="CredentialPurpose.AllowsUnboundLegacyEnvelope"/>, and
    /// refused everywhere else with <see cref="CredentialFailureReason.UnboundLegacyEnvelopeRefused"/>.
    /// </summary>
    PurposePolicy,

    /// <summary>
    /// Read a version-1 envelope regardless of purpose. Exists for exactly one caller — the
    /// credential binding backfill, whose whole job is to read version 1 and write version 2. Under
    /// <see cref="PurposePolicy"/> the backfill could never migrate the columns it covers, because
    /// its own read would be the thing refused.
    /// </summary>
    PermitForMigration,
}

/// <summary>
/// The outcome of reading a stored credential, with the two facts the backfill needs in order to
/// decide whether the row must be rewritten. Ordinary callers use
/// <see cref="DeliveryEncryptionService.Decrypt(string, CredentialScope)"/> and never see this.
/// </summary>
/// <param name="Plaintext">The decrypted credential.</param>
/// <param name="WasUnboundLegacyEnvelope">The stored blob was version 1 — no tenant binding.</param>
/// <param name="WasEncryptedUnderPreviousKey">
/// The stored blob did not verify under the primary key but did verify under
/// <c>Delivery:PreviousEncryptionKey</c>. Only ever true during a key rotation.
/// </param>
public readonly record struct CredentialReadResult(
    string Plaintext,
    bool WasUnboundLegacyEnvelope,
    bool WasEncryptedUnderPreviousKey);

/// <summary>
/// Authenticated encryption for stored credentials — delivery transport credentials, cXML shared
/// secrets, catalog auth, webhook signing secrets, IMAP passwords, and SFTP/S3 ingress secrets.
/// Key: IConfiguration["Delivery:EncryptionKey"] — 32-byte base64 string.
/// Format: base64(version[1] + nonce[12] + tag[16] + ciphertext).
/// Version 2 binds the blob to a <see cref="CredentialScope"/>; version 1 is legacy read-only.
///
/// <para><b>Writes never change shape here.</b> <see cref="Encrypt"/> has only ever emitted version
/// 2 under the primary key, and still does. Everything below is read-side, so an instance running
/// the previous build can read every blob this one writes — a rollout, or a rollback, is safe in
/// both directions.</para>
///
/// <para><b>Key rotation.</b> An optional second key, <c>Delivery:PreviousEncryptionKey</c>, is
/// accepted on READ only. With it set, the deployment key can be replaced without every stored
/// credential becoming unreadable: new writes go under the new primary, old blobs still verify
/// under the previous key, and the backfill drains the columns it covers onto the new key. It does
/// NOT complete a rotation — <c>SupplierDeliveryConfig.EncryptedCredentials</c> cannot be rewritten
/// by any migration (see <c>ICredentialBindingBackfillService</c>), so the previous key must stay
/// configured until an operator has re-saved every delivery config. Retiring the old key before
/// then breaks delivery for whoever has not.</para>
/// </summary>
public class DeliveryEncryptionService
{
    /// <summary>Envelope written before credentials were bound to a tenant. Read-only — never written.</summary>
    private const byte VersionLegacy = 1;

    /// <summary>Envelope bound to a <see cref="CredentialScope"/> via AES-GCM associated data.</summary>
    private const byte VersionBound = 2;

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    /// <summary>
    /// Emergency escape hatch: restores the pre-guard behaviour in which a version-1 envelope is
    /// accepted for EVERY purpose. It exists because the guard fails closed, and a workspace whose
    /// backfill never completed would otherwise lose ingress until a redeploy. Flipping one Railway
    /// variable and restarting is faster and less risky than that.
    ///
    /// <para>Turning it on re-opens the portable-ciphertext hole for every purpose. It is off unless
    /// explicitly set, and it should be turned back off the moment the backfill reports zero
    /// remaining legacy blobs.</para>
    /// </summary>
    private const string AllowUnboundLegacyEverywhereKey = "Delivery:AllowUnboundLegacyCredentials";

    private readonly byte[] _key;
    private readonly byte[]? _previousKey;
    private readonly bool _allowUnboundLegacyEverywhere;

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

        // Optional. A blank value is treated as absent so an empty Railway variable does not become
        // a hard boot failure; a present-but-wrong-length value IS a boot failure, because silently
        // ignoring it during a rotation would look exactly like "the rotation worked".
        var base64Previous = configuration["Delivery:PreviousEncryptionKey"];
        if (!string.IsNullOrWhiteSpace(base64Previous))
        {
            byte[] previous;
            try
            {
                previous = Convert.FromBase64String(base64Previous);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "Delivery:PreviousEncryptionKey is set but is not valid base64.", ex);
            }

            if (previous.Length != 32)
                throw new InvalidOperationException(
                    "Delivery:PreviousEncryptionKey must decode to exactly 32 bytes (AES-256).");

            if (previous.AsSpan().SequenceEqual(_key))
                throw new InvalidOperationException(
                    "Delivery:PreviousEncryptionKey is identical to Delivery:EncryptionKey. " +
                    "That is not a rotation — clear it, or set it to the key being retired.");

            _previousKey = previous;
        }

        _allowUnboundLegacyEverywhere =
            configuration.GetValue(AllowUnboundLegacyEverywhereKey, defaultValue: false);
    }

    /// <summary>
    /// True when a retiring key is configured, so a stored blob may legitimately be encrypted under
    /// it. The backfill reads this to decide whether it must attempt every row rather than only the
    /// version-1 ones.
    /// </summary>
    public bool HasPreviousKey => _previousKey is not null;

    /// <summary>
    /// Encrypts with AES-256-GCM under the PRIMARY key, binding the ciphertext to
    /// <paramref name="scope"/> as associated data. Always writes envelope version 2. A blob
    /// produced here decrypts ONLY when the same organisation, purpose, and scope id are presented
    /// back.
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
    /// <para>Version 2 requires the scope to match. Version 1 predates binding, carries no
    /// associated data, and would therefore decrypt under ANY scope — so it is accepted only for
    /// the purposes in <see cref="CredentialPurpose.AllowsUnboundLegacyEnvelope"/>, which today is
    /// delivery credentials alone. Every other purpose is migrated by the credential binding
    /// backfill and refuses version 1.</para>
    ///
    /// <para>Throws rather than returning null. A null silently became "no credentials" at two call
    /// sites and let an unsigned webhook go out.</para>
    /// </summary>
    /// <exception cref="CredentialUnbindableException">
    /// The envelope is malformed, its version is unsupported, it is an unbound version-1 blob for a
    /// purpose that no longer accepts one, or the GCM tag did not verify — wrong key, corruption,
    /// or a blob belonging to a different tenant, purpose, or scope.
    /// </exception>
    public string Decrypt(string base64, CredentialScope scope) =>
        DecryptDetailed(base64, scope).Plaintext;

    /// <summary>
    /// <see cref="Decrypt(string, CredentialScope)"/>, additionally reporting the envelope version
    /// and which key verified the blob. Only the credential binding backfill needs those facts, and
    /// only it passes <see cref="LegacyEnvelopeAccess.PermitForMigration"/>.
    /// </summary>
    public CredentialReadResult DecryptDetailed(
        string base64,
        CredentialScope scope,
        LegacyEnvelopeAccess legacyAccess = LegacyEnvelopeAccess.PurposePolicy)
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

        // ── the downgrade guard ──────────────────────────────────────────────
        // Refused BEFORE any decrypt is attempted. A version-1 blob has no associated data, so it
        // verifies under whatever scope it is handed; the only place that can be stopped is here,
        // by looking at the version byte and the purpose rather than at the result.
        if (version == VersionLegacy && !LegacyPermitted(scope.Purpose, legacyAccess))
            throw new CredentialUnbindableException(
                CredentialFailureReason.UnboundLegacyEnvelopeRefused, scope);

        var nonce = combined[1..(1 + NonceSize)];
        var tag = combined[(1 + NonceSize)..HeaderSize];
        var ciphertext = combined[HeaderSize..];

        // Version 1 carries no associated data; passing the scope's AAD to it would never verify.
        var associatedData = version == VersionBound ? scope.ToAssociatedData() : null;

        if (TryDecryptUnder(_key, nonce, tag, ciphertext, associatedData, out var plaintext, out var failure))
            return new CredentialReadResult(
                plaintext, version == VersionLegacy, WasEncryptedUnderPreviousKey: false);

        // The tag did not verify under the primary key. During a rotation that is the expected
        // answer for every blob written before the new key was installed, so try the retiring key
        // before declaring the credential unreadable. With no previous key configured this branch
        // is not entered and behaviour is byte-identical to before.
        if (_previousKey is not null &&
            TryDecryptUnder(_previousKey, nonce, tag, ciphertext, associatedData, out plaintext, out _))
            return new CredentialReadResult(
                plaintext, version == VersionLegacy, WasEncryptedUnderPreviousKey: true);

        // The primary key's failure is the one worth reporting: with no rotation in progress it is
        // the only attempt made, and during one the retiring key's failure is the less informative
        // of the two.
        throw new CredentialUnbindableException(
            CredentialFailureReason.AuthenticationFailed, scope, failure);
    }

    private bool LegacyPermitted(string? purpose, LegacyEnvelopeAccess legacyAccess) =>
        legacyAccess == LegacyEnvelopeAccess.PermitForMigration
        || _allowUnboundLegacyEverywhere
        || CredentialPurpose.AllowsUnboundLegacyEnvelope(purpose);

    private static bool TryDecryptUnder(
        byte[] key,
        byte[] nonce,
        byte[] tag,
        byte[] ciphertext,
        byte[]? associatedData,
        out string plaintext,
        out CryptographicException? failure)
    {
        var plaintextBytes = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            if (associatedData is null)
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            else
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, associatedData);
        }
        catch (CryptographicException ex)
        {
            plaintext = string.Empty;
            failure = ex;
            return false;
        }

        plaintext = Encoding.UTF8.GetString(plaintextBytes);
        failure = null;
        return true;
    }
}
