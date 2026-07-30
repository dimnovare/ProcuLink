namespace ProcuLink.Core.Constants;

public enum BillingFeature
{
    Xml,
    Pdf,
    MappingLibrary,
    ValidationRules,
    BulkMapping,
    Cxml,
    DeliveryHistory,
    AdvancedAudit,
    WebhookDelivery,
    EmailIngestion,
    ErpConnectors,
    CustomSupplierRules,
    SlaOnboarding,
    SftpIngestion,
    S3Ingestion,
    // Enterprise SSO (SAML/OIDC). The auth itself is handled natively by Clerk
    // Enterprise Connections; this flag is pure plan-gating metadata that drives
    // the Settings "Single sign-on" availability/upsell — it does NOT touch JWT
    // validation or tenant resolution (the session JWT shape is identical for a
    // SAML login). See docs/strategy/2026-06-08-sso-saml-implementation.md.
    Sso,
}
