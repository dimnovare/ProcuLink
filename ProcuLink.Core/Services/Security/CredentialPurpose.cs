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
}
