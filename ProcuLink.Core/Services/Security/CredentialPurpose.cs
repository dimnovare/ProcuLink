namespace ProcuLink.Core.Services.Security;

/// <summary>
/// Names the KIND of credential a ciphertext blob holds. The value is part of the AES-GCM
/// associated data, so a blob encrypted under one purpose can never be decrypted under another —
/// a supplier's delivery credentials cannot be substituted for that same supplier's cXML shared
/// secret.
///
/// <para>These strings are a persisted format. Changing one makes every blob written under the old
/// value undecryptable. Add a new constant instead.</para>
/// </summary>
public static class CredentialPurpose
{
    /// <summary>Scope id: supplier id. Delivery transport credentials (SupplierDeliveryConfig.EncryptedCredentials).</summary>
    public const string SupplierDeliveryCredentials = "supplier.delivery.credentials";

    /// <summary>Scope id: supplier id. cXML sender shared secret (SupplierDeliveryConfig.EncryptedCxmlSharedSecret).</summary>
    public const string SupplierDeliveryCxmlSecret = "supplier.delivery.cxml_secret";

    /// <summary>Scope id: SupplierCatalogSource.Id. SFTP/FTPS catalog password.</summary>
    public const string SupplierCatalogPassword = "supplier.catalog.password";

    /// <summary>Scope id: SupplierCatalogSource.Id. HTTP/vendor auth-config envelope.</summary>
    public const string SupplierCatalogAuthConfig = "supplier.catalog.auth_config";

    /// <summary>Scope id: IntegrationSubscription.Id. Webhook HMAC signing secret.</summary>
    public const string OrgIntegrationWebhookSecret = "org.integration.webhook_secret";

    /// <summary>Scope id: Guid.Empty — one IMAP configuration per organisation.</summary>
    public const string OrgEmailImapPassword = "org.email.imap_password";

    /// <summary>Scope id: Guid.Empty — one SFTP ingress configuration per organisation.</summary>
    public const string OrgIngressSftpPassword = "org.ingress.sftp_password";

    /// <summary>Scope id: Guid.Empty — one S3 ingress configuration per organisation.</summary>
    public const string OrgIngressS3SecretKey = "org.ingress.s3_secret_key";

    /// <summary>
    /// Scope id: OrgInboundAddress.Id. The plaintext inbound-email address token, kept recoverable
    /// so the organisation can be shown the address it must hand to its buyers.
    ///
    /// <para>Scoped on the ROW id rather than <c>Guid.Empty</c> because an organisation holds
    /// several of these at once — a primary, a legacy address mid-retirement, an old one inside a
    /// rotation overlap. Row scoping is what stops a ciphertext lifted from one row being replayed
    /// into another.</para>
    /// </summary>
    public const string OrgInboundEmailAddress = "org.inbound.email_address";

    /// <summary>
    /// The purposes for which a version-1 (unbound, scope-inert) envelope may STILL be read.
    ///
    /// <para>A version-1 blob carries no associated data, so it decrypts under any organisation,
    /// any purpose, and any scope id. That makes it portable: a ciphertext lifted from one row —
    /// a backup, a support export, a log — decrypts if it is written into any other row. Every
    /// purpose absent from this set therefore refuses version 1 outright, which is what stops the
    /// replay. See <c>DeliveryEncryptionService.Decrypt</c>.</para>
    ///
    /// <para><b>Only <see cref="SupplierDeliveryCredentials"/> is here, and it is not an
    /// oversight.</b> Every other purpose is migrated to version 2 by
    /// <c>ICredentialBindingBackfillService</c> on each boot. That backfill deliberately cannot
    /// cover <c>SupplierDeliveryConfig.EncryptedCredentials</c>, because
    /// <c>SupplierConnectionRevision.CredentialsRef</c> is a verbatim byte-copy of it that is
    /// compared by ordinal equality and frozen on published revisions by a database trigger — a
    /// random nonce means re-encrypting either side breaks the other. Those blobs migrate only when
    /// an operator next saves the delivery config, which writes both sides together. Refusing
    /// version 1 here would take delivery down for every workspace that has not re-saved since the
    /// binding shipped.</para>
    ///
    /// <para>Remove an entry from this set only once nothing can still hold version 1 for it. The
    /// direction of a mistake matters: adding an entry re-opens the replay, removing one fails
    /// closed and is visible in the delivery failure panel.</para>
    /// </summary>
    private static readonly HashSet<string> UnboundLegacyReadablePurposes =
        new(StringComparer.Ordinal) { SupplierDeliveryCredentials };

    /// <summary>
    /// True when a version-1 envelope stored under <paramref name="purpose"/> may still be read.
    /// An unrecognised purpose answers false — an unknown credential kind has no legacy rows by
    /// definition, and guessing "yes" would be the portable-ciphertext hole again.
    /// </summary>
    public static bool AllowsUnboundLegacyEnvelope(string? purpose) =>
        purpose is not null && UnboundLegacyReadablePurposes.Contains(purpose);
}
