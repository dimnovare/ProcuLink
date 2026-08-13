namespace ProcuLink.Core.Constants;

/// <summary>
/// The KIND of an <c>OrgInboundAddress</c> row. Persisted values — changing one orphans every row
/// written under the old spelling, so add a constant rather than rename one.
///
/// <para>The kind is presentational and operational, never a security decision: resolution treats
/// every active, unexpired, unrevoked row identically, whatever its kind. What the kind records is
/// how much entropy the address actually has, so an operator can tell at a glance which of their
/// addresses is the weak one that is on its way out.</para>
/// </summary>
public static class InboundAddressKind
{
    /// <summary>
    /// A minted address: <c>InboundAddressService.TokenBytes</c> bytes of CSPRNG output, hex-encoded.
    /// This is the only kind ever minted going forward.
    /// </summary>
    public const string Primary = "primary";

    /// <summary>
    /// The organisation's public <c>Slug</c>, backfilled as an address row so that mail already in
    /// flight — and every buyer's address book — keeps working across the deploy that introduced
    /// this table.
    ///
    /// <para><b>This kind is weak by construction and is not minted, only backfilled.</b> A slug is
    /// a kebab-cased company name plus four hex characters (<c>TenantResolutionMiddleware</c>'s
    /// <c>GenerateSlug</c>), so it carries about 16 bits of randomness on top of a guessable stem —
    /// which is precisely the finding this table exists to close. Every backfilled row therefore
    /// carries a hard <c>ExpiresAt</c>, so the guessable addressing scheme retires itself on a
    /// schedule instead of surviving as long as nobody remembers to turn it off.</para>
    /// </summary>
    public const string LegacySlug = "legacy_slug";

    /// <summary>Every defined kind. Tests enumerate THIS rather than re-typing the strings.</summary>
    public static readonly IReadOnlyList<string> All = [Primary, LegacySlug];
}
