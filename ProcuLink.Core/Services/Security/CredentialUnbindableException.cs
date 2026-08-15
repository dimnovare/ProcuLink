namespace ProcuLink.Core.Services.Security;

/// <summary>Why a stored credential could not be read back.</summary>
public enum CredentialFailureReason
{
    /// <summary>Not base64, or too short to be an envelope.</summary>
    MalformedEnvelope,

    /// <summary>Version byte is neither 1 (legacy, unbound) nor 2 (bound).</summary>
    UnknownVersion,

    /// <summary>
    /// A well-formed version-1 envelope was presented for a purpose that no longer accepts one.
    /// Distinct from <see cref="AuthenticationFailed"/> on purpose: the blob may decrypt perfectly
    /// — it is refused because it carries no tenant binding, so accepting it would let a ciphertext
    /// from one row be replayed into another. Only the purposes in
    /// <see cref="CredentialPurpose.AllowsUnboundLegacyEnvelope"/> still accept version 1.
    /// </summary>
    UnboundLegacyEnvelopeRefused,

    /// <summary>
    /// The GCM tag did not verify: the wrong deployment key, a corrupted blob, or — the case this
    /// whole change exists for — a blob encrypted for a DIFFERENT organisation, purpose, or scope.
    /// </summary>
    AuthenticationFailed,
}

/// <summary>
/// Thrown when a stored credential cannot be decrypted. This is deliberately an exception rather
/// than a null return: a null silently became "no credentials" at two call sites and let an
/// unauthenticated request go out. Callers that have a safe degraded state catch it explicitly.
/// </summary>
public sealed class CredentialUnbindableException : Exception
{
    public CredentialFailureReason Reason { get; }
    public CredentialScope Scope { get; }

    public CredentialUnbindableException(
        CredentialFailureReason reason, CredentialScope scope, Exception? inner = null)
        : base(BuildMessage(reason, scope), inner)
    {
        Reason = reason;
        Scope = scope;
    }

    // Identifiers only — never any plaintext or ciphertext material.
    private static string BuildMessage(CredentialFailureReason reason, CredentialScope scope) => reason switch
    {
        CredentialFailureReason.MalformedEnvelope =>
            $"Stored credential for purpose '{scope.Purpose}' is not a well-formed envelope.",
        CredentialFailureReason.UnknownVersion =>
            $"Stored credential for purpose '{scope.Purpose}' has an unsupported envelope version.",
        CredentialFailureReason.UnboundLegacyEnvelopeRefused =>
            $"Stored credential for purpose '{scope.Purpose}' is still in the pre-binding (version 1) " +
            $"envelope, which carries no tenant binding and is no longer accepted for this purpose. " +
            $"Re-save the credential to rewrite it in the bound envelope " +
            $"(org {scope.OrgId}, scope {scope.ScopeId}).",
        _ =>
            $"Stored credential for purpose '{scope.Purpose}' failed authentication — wrong key, " +
            $"or encrypted for a different organisation or scope " +
            $"(org {scope.OrgId}, scope {scope.ScopeId}).",
    };
}
