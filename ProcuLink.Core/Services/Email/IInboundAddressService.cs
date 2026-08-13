namespace ProcuLink.Core.Services.Email;

/// <summary>
/// Why a recipient address did or did not name an organisation.
///
/// <para><b>The zero value refuses.</b> <c>Unavailable</c> is 0 so that a default-initialised
/// <see cref="InboundAddressLookup"/> — a struct someone forgot to assign, a field that never got
/// written — denies rather than resolves. This repo has been bitten repeatedly by the opposite
/// arrangement, where an unrecognised or absent value falls through to the favourable answer; here
/// the favourable answer costs the most, because resolving is what puts a stranger's mail into a
/// tenant's inbox.</para>
/// </summary>
public enum InboundAddressLookupStatus
{
    /// <summary>
    /// The lookup could not be performed — the server-side hash secret is missing or unusable, so
    /// NO address can be recognised right now. Distinct from <see cref="NotFound"/> because the
    /// cause is ours and a redelivery will succeed once it is fixed: the caller must keep the
    /// provider's retries alive rather than settle the message.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The lookup ran and this address belongs to no organisation — never issued, revoked, or
    /// expired. A redelivery carries the same address and reaches the same verdict, so the caller
    /// should stop the retries.
    /// </summary>
    NotFound = 1,

    /// <summary>The address is a live credential for <see cref="InboundAddressLookup.OrgId"/>.</summary>
    Resolved = 2,
}

/// <summary>
/// The outcome of resolving a recipient address to the organisation that owns it.
/// </summary>
public readonly record struct InboundAddressLookup(
    InboundAddressLookupStatus Status,
    Guid? OrgId,
    Guid? AddressId)
{
    public static InboundAddressLookup Found(Guid orgId, Guid addressId) =>
        new(InboundAddressLookupStatus.Resolved, orgId, addressId);

    public static InboundAddressLookup NotFound() =>
        new(InboundAddressLookupStatus.NotFound, null, null);

    public static InboundAddressLookup Unavailable() =>
        new(InboundAddressLookupStatus.Unavailable, null, null);
}

/// <summary>A freshly minted address. The token is returned in the clear exactly once per mint.</summary>
public sealed record MintedInboundAddress(Guid Id, string Token);

/// <summary>
/// An address as shown back to its owning organisation.
/// </summary>
/// <param name="Token">
/// The plaintext token, decrypted for display. NULL when the ciphertext could not be opened (a
/// rotated deployment key, a mis-bound blob) — the row is still listed, because an address that
/// cannot be displayed can still be RESOLVING mail, and hiding it would leave an operator unable
/// to revoke something that is live.
/// </param>
public sealed record InboundAddressView(
    Guid Id,
    string Kind,
    string Label,
    string? Token,
    string TokenPrefix,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt);

/// <summary>
/// Issues, resolves, lists and revokes the per-organisation inbound-email addresses that authorise
/// delivery into an organisation.
///
/// <para>Resolution is the security-critical member: it is the ONLY thing that decides which tenant
/// an inbound message belongs to, and it takes no hint from the caller beyond the address itself.
/// </para>
/// </summary>
public interface IInboundAddressService
{
    /// <summary>
    /// Resolves a normalised address token to its owning organisation. Never falls back to a
    /// default organisation, a configuration mapping, or the organisation slug — an unrecognised
    /// token resolves to nothing at all.
    /// </summary>
    Task<InboundAddressLookup> ResolveAsync(string addressToken, CancellationToken ct);

    /// <summary>Mints a new high-entropy primary address for an organisation.</summary>
    Task<MintedInboundAddress> MintPrimaryAsync(Guid orgId, string label, CancellationToken ct);

    /// <summary>
    /// Mints a primary address only if the organisation has no live one. Lets an organisation
    /// created after the backfill ran get its address the first time it asks for it, without a
    /// second one appearing on every subsequent read.
    /// </summary>
    Task EnsurePrimaryAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// Gives every organisation that lacks one a primary address, and registers each existing
    /// organisation's slug as an expiring <c>legacy_slug</c> address so mail already addressed that
    /// way keeps arriving. Idempotent: an organisation that already has both is skipped, so this
    /// can run on every boot. Returns the number of rows inserted.
    /// </summary>
    Task<int> BackfillMissingAsync(CancellationToken ct);

    /// <summary>Lists an organisation's addresses, newest first.</summary>
    Task<IReadOnlyList<InboundAddressView>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// Revokes one address. Org-scoped: an id belonging to another organisation is a miss, not a
    /// revocation. Returns false when nothing matched.
    /// </summary>
    Task<bool> RevokeAsync(Guid orgId, Guid addressId, CancellationToken ct);
}
