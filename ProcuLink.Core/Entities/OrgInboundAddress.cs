namespace ProcuLink.Core.Entities;

/// <summary>
/// One inbound-email address an organisation may receive purchase orders at — and, because the
/// mail relay carries no other per-tenant proof, the CREDENTIAL that authorises delivery into that
/// organisation.
///
/// <para><b>Why the address is the credential.</b> Postmark Inbound fans every message for the whole
/// domain into ONE webhook URL, is not HMAC-signed, and cannot attach a custom header
/// (see <c>docs/infra/postmark-inbound-verify-worker/README.md</c>). The recipient address is
/// therefore the only field that differs per tenant, so it is the only thing that CAN carry tenant
/// identity. Before this table that address was the organisation's public <c>Slug</c>, which meant
/// anyone who could guess a slug could post orders into that organisation's inbox by sending it an
/// email. Making the local part a high-entropy secret turns "guess the tenant" into "present the
/// tenant's credential" — the same bearer-token property <see cref="TenantApiKey"/> has, delivered
/// over the only channel a mail relay has.</para>
///
/// <para><b>Stored twice, on purpose.</b> <see cref="TokenHash"/> is a deterministic keyed hash, so
/// it can carry a unique index and resolve a recipient in one indexed lookup;
/// <see cref="EncryptedToken"/> is the AES-GCM ciphertext, so the org can be SHOWN its own address
/// again later. An inbound address is unlike an API key in exactly that way: the user has to hand it
/// to their buyers, so "shown once at creation, never again" would not survive contact with
/// support. Neither column alone would do both jobs — AES-GCM is randomised (no stable index) and a
/// hash is one-way (nothing to display).</para>
/// </summary>
public class OrgInboundAddress
{
    public Guid Id { get; set; }

    public Guid OrganisationId { get; set; }

    /// <summary>
    /// HMAC-SHA256 of the normalised address token, keyed with the server-side
    /// <c>Security:ApiKeyHashSecret</c> — the same construction and the same secret
    /// <see cref="TenantApiKey.KeyHash"/> uses, via <c>ApiKeyHasher.ComputeHash</c>, with a
    /// domain-separating prefix so the two namespaces can never collide. Read-only database access
    /// does not yield working addresses without that secret.
    ///
    /// <para>UNIQUE across ALL organisations, which is what makes "the caller cannot pick the
    /// tenant" a database invariant rather than a code convention: one token can never name two
    /// organisations.</para>
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// AES-GCM ciphertext of the plaintext token, written through
    /// <c>DeliveryEncryptionService.Encrypt</c> and AAD-bound to
    /// <c>(OrganisationId, CredentialPurpose.OrgInboundEmailAddress, Id)</c>. Scoped on the ROW id,
    /// not <c>Guid.Empty</c>, because an org legitimately holds several of these at once (a primary,
    /// a legacy address mid-retirement, an old one inside a rotation overlap) — row scoping means a
    /// ciphertext lifted from one row cannot be replayed into another.
    /// </summary>
    public string EncryptedToken { get; set; } = string.Empty;

    /// <summary>
    /// First few characters of the token, for list UIs and log correlation. Deliberately a
    /// fragment: enough to tell two rows apart, never enough to reconstruct the address.
    /// </summary>
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>See <c>InboundAddressKind</c>. Distinguishes a minted high-entropy address from a
    /// backfilled legacy slug address that is on its way out.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Operator-facing label, e.g. "Primary" or "Legacy slug address (retiring)".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Cleared to revoke. An inactive row NEVER resolves — see the lookup predicate in
    /// <c>InboundAddressService</c>.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Hard stop. Null = no expiry. A row past its expiry stops resolving without anyone having to
    /// act, which is what bounds the legacy-slug overlap window: the migration backfills every
    /// existing org's slug address with an expiry, so the guessable addressing scheme retires
    /// itself instead of depending on a founder remembering to switch it off.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Set when an operator revokes the address. Kept (rather than deleting the row) so a
    /// refused delivery can still be explained after the fact.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Best-effort "last seen", throttled on write like <see cref="TenantApiKey.LastUsedAt"/>.</summary>
    public DateTime? LastUsedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
