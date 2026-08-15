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
///
/// <para>Because that exclusion is permanent, delivery credentials are also the ONE purpose for
/// which <c>CredentialPurpose.AllowsUnboundLegacyEnvelope</c> still accepts a version-1 blob. Every
/// other purpose refuses one, which is what makes the columns below worth migrating. The pass
/// therefore also COUNTS the delivery-credential blobs it cannot migrate and logs the number, so
/// the size of that residual is visible rather than merely documented.</para>
///
/// <para><b>Key rotation.</b> When <c>Delivery:PreviousEncryptionKey</c> is configured, the pass
/// additionally rewrites any covered blob that verified under the retiring key, re-encrypting it
/// under the new primary. That drains the covered columns onto the new key. It cannot drain the two
/// excluded columns, so the retiring key must stay configured until every delivery config has been
/// re-saved by hand.</para>
/// </summary>
public interface ICredentialBindingBackfillService
{
    /// <summary>
    /// Rewrites every version-1 blob in the covered columns to version 2.
    /// Returns the number of blobs rewritten.
    /// </summary>
    Task<int> RebindLegacyCredentialsAsync(CancellationToken ct);
}
