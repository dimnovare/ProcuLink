namespace ProcuLink.Core.Services;

/// <summary>
/// Decrypted, transform-ready cXML network credentials for a supplier connection.
///
/// Threaded into <see cref="ITransformService.TransformAsync"/> so a generated cXML
/// <c>&lt;Header&gt;</c> carries the supplier's REAL cXML network credentials (e.g. a Coupa
/// <c>NetworkId</c> identity such as <c>TESTBUYER_SE</c> / <c>TESTSUPPLIER_SE</c>) instead of
/// ProcuLink's internal <c>OrgId</c> / <c>SupplierId</c> GUIDs — which is what an
/// unconfigured connection emits today and what the founder sees on the wire.
///
/// <para><b>Per-credential fallback:</b> every field is optional. When a credential's
/// identity is null/blank the cXML transform falls back to the legacy default for THAT
/// credential (From → <c>OrgId</c>/orgId, To → <c>SupplierId</c>/supplierId, Sender →
/// <c>NetworkUserId</c>/"proculink"). A fully-null config therefore produces output that
/// is byte-identical to the pre-feature transform, so existing orders never break.</para>
///
/// <para><see cref="SenderSharedSecret"/> is the DECRYPTED secret; it is emitted as the
/// Sender credential's <c>&lt;SharedSecret&gt;</c> element only when non-blank.</para>
///
/// <para><b>Configurable DOCTYPE (T7):</b> <see cref="DtdSystemId"/> / <see cref="DtdPublicId"/> are
/// the OPTIONAL cXML <c>&lt;!DOCTYPE&gt;</c> the supplier requires (free-text DTD URI — the founder
/// doesn't yet know which version). They are init-only so the positional constructor and every
/// existing caller stay byte-identical. A null/blank <see cref="DtdSystemId"/> emits NO DOCTYPE,
/// so an unconfigured supplier is byte-identical to the pre-feature output. SYSTEM form when only
/// <see cref="DtdSystemId"/> is set; PUBLIC form when <see cref="DtdPublicId"/> is also set.</para>
/// </summary>
public sealed record CxmlCredentialConfig(
    string? FromDomain,
    string? FromIdentity,
    string? ToDomain,
    string? ToIdentity,
    string? SenderDomain,
    string? SenderIdentity,
    string? SenderSharedSecret)
{
    /// <summary>Optional cXML DOCTYPE SYSTEM id (the DTD URI). Null/blank → no DOCTYPE emitted.</summary>
    public string? DtdSystemId { get; init; }

    /// <summary>Optional cXML DOCTYPE PUBLIC id. Only meaningful alongside <see cref="DtdSystemId"/>
    /// (PUBLIC form requires both); ignored when <see cref="DtdSystemId"/> is blank.</summary>
    public string? DtdPublicId { get; init; }
}

/// <summary>
/// Resolves a supplier's <see cref="CxmlCredentialConfig"/> (decrypting the sender shared
/// secret) for the transform path. Implemented in Infrastructure against the supplier
/// delivery-config row.
///
/// <para>An OPTIONAL dependency of the transform sub-service: a null resolver — or a supplier
/// with no cXML config — behaves exactly like the legacy path (returns null), so older
/// positional test ctors and unconfigured suppliers stay byte-identical.</para>
/// </summary>
public interface ICxmlCredentialResolver
{
    /// <summary>
    /// Returns the supplier's cXML credentials, or <c>null</c> when the supplier has no
    /// delivery-config row or no cXML credentials configured (→ legacy GUID output).
    /// </summary>
    Task<CxmlCredentialConfig?> ResolveAsync(Guid organisationId, Guid supplierId, CancellationToken ct);
}
