namespace ProcuLink.Core.Services;

/// <summary>
/// Re-encrypts credential blobs still stored in the pre-binding envelope (version 1, no associated
/// data) into the bound envelope (version 2), so they gain the tenant + purpose + scope binding.
///
/// <para>Idempotent: a blob already at version 2 is skipped, so this is safe on every boot.
/// Dual-read means a concurrent reader is correct whether it sees the old or the new value, so
/// there is no window to coordinate.</para>
///
/// <para><b>Deliberately does NOT touch <c>SupplierDeliveryConfig.EncryptedCredentials</c> or
/// <c>SupplierConnectionRevision.CredentialsRef</c>.</b> CredentialsRef is a verbatim byte-copy of
/// the live blob: it is compared by ordinal byte equality in <c>DeliverySnapshotMatches</c>, and
/// frozen on published revisions by the <c>proculink_block_published_revision_content_update</c>
/// trigger. A random nonce means any re-encryption changes the bytes, so re-encrypting one side
/// reports permanent drift and re-encrypting the other raises P0001. Those credentials migrate when
/// an operator next saves them, which writes both sides together.</para>
/// </summary>
public interface ICredentialBindingBackfillService
{
    /// <summary>
    /// Rewrites every version-1 blob in the covered columns to version 2.
    /// Returns the number of blobs rewritten.
    /// </summary>
    Task<int> RebindLegacyCredentialsAsync(CancellationToken ct);
}
