using System.Text;

namespace ProcuLink.Core.Services.Security;

/// <summary>
/// Identifies WHO a credential belongs to and WHAT it is, so the pair can be bound into the
/// ciphertext as AES-GCM associated data. A blob then decrypts only when presented with the same
/// organisation, purpose, and owning record.
///
/// <para><b>Delivery credentials scope on SUPPLIER ID, not on the config row id.</b>
/// <c>SupplierConnectionRevision.CredentialsRef</c> is a verbatim byte-copy of
/// <c>SupplierDeliveryConfig.EncryptedCredentials</c>, and both are decrypted through the same
/// call. The two rows share an (org, supplier) pair but have different row ids, so scoping on the
/// row id would break delivery for every pinned order.</para>
/// </summary>
public readonly record struct CredentialScope(Guid OrgId, string Purpose, Guid ScopeId)
{
    /// <summary>
    /// Prefix stamped into every AAD. Domain-separates this use of the deployment key from any
    /// future one, and pins the AAD layout: change the layout, change this string.
    /// </summary>
    private const string DomainSeparator = "proculink.cred.v2";

    /// <summary>Credential owned by a specific supplier or record.</summary>
    public static CredentialScope ForSupplier(Guid orgId, string purpose, Guid supplierId) =>
        new(orgId, purpose, supplierId);

    /// <summary>Credential owned by the organisation itself (one per org — IMAP, ingress).</summary>
    public static CredentialScope ForOrg(Guid orgId, string purpose) =>
        new(orgId, purpose, Guid.Empty);

    /// <summary>
    /// The associated-data bytes: <c>domain SP orgId SP purpose SP scopeId</c>, SP being a space.
    /// Guids are written as 32 fixed-length hex characters ("N"), and Purpose is the only
    /// variable-length field, so the field boundaries are unambiguous and no two distinct tuples can
    /// produce the same bytes. The space separators are defence in depth: they keep the AAD readable
    /// in a log, and they become load-bearing the moment a future credential kind uses a
    /// variable-length scope identifier instead of a Guid. The Purpose guard below is what keeps
    /// that promise true.
    /// </summary>
    public byte[] ToAssociatedData()
    {
        if (OrgId == Guid.Empty)
            throw new ArgumentException(
                "CredentialScope.OrgId must be set — an unscoped credential defeats the binding.",
                nameof(OrgId));

        if (string.IsNullOrWhiteSpace(Purpose))
            throw new ArgumentException(
                "CredentialScope.Purpose must be set — use a CredentialPurpose constant.",
                nameof(Purpose));

        if (Purpose.Contains(' '))
            throw new ArgumentException(
                "CredentialScope.Purpose must not contain a space — it is the AAD field separator.",
                nameof(Purpose));

        return Encoding.UTF8.GetBytes(
            $"{DomainSeparator} {OrgId:N} {Purpose} {ScopeId:N}");
    }
}
