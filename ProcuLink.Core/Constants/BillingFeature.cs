namespace ProcuLink.Core.Constants;

/// <summary>
/// A capability the paid ladder actually gates.
///
/// <para><b>Membership rule (WP-11).</b> A member may exist here only if production code
/// really refuses it below its minimum plan AND the published pricing copy sells it as
/// belonging to a specific tier. Anything else is a claim about the price list that nothing
/// keeps true -- which is what this enum had become: sixteen declared capabilities, three
/// enforced. Adding a member without an enforcement site now fails
/// <c>BillingFeatureGateCoverageTests.EveryFeature_HasANamedEnforcementSite</c>.</para>
///
/// <para><b>Removed in WP-11</b>, each because there was nothing honest to gate:
/// <c>Xml</c> and <c>Pdf</c> (gated at Growth, but the published Pilot card sells
/// "CSV/XLSX/PDF/XML upload" -- input formats are not a tier differentiator);
/// <c>MappingLibrary</c> (no such surface exists anywhere in the product -- the declaration
/// and its plan-map row were the only two references in the repo);
/// <c>DeliveryHistory</c> (no plan card sells it, and withholding "did my purchase order
/// actually go out?" from a paying customer is not a differentiator); and
/// <c>SlaOnboarding</c> (an SLA and named onboarding are commitments fulfilled by people --
/// no code path could ever check them). <c>CustomTemplates</c> went the same way: it gated
/// the output-template editor that WP-06 retired, so by the time this enum was audited there
/// was no surface left behind the gate.</para>
///
/// <para><b>Removed in WP-07</b> (this PR): the flag that gated the second, never-run rule engine.
/// Like <c>CustomTemplates</c>, there is no surface left behind the gate once its subsystem is
/// retired. (Its identifier is deliberately not written here -- naming a retired symbol in a comment
/// is what <c>RetiredSubsystemsStayRetiredTests</c> exists to catch, and it caught this line.) The
/// engine that did evaluate (<c>SupplierAcceptanceRule</c> / <c>RuleDefinition</c>) was gated by
/// <c>CustomSupplierRules</c>, removed in plan 13b below.</para>
///
/// <para><b>Removed in plan 13b</b> (2026-11-13): <c>BulkMapping</c>, <c>Cxml</c>,
/// <c>WebhookDelivery</c>, <c>EmailIngestion</c>, <c>ErpConnectors</c>, <c>CustomSupplierRules</c>,
/// <c>SftpIngestion</c> and <c>S3Ingestion</c>. Every one of them gated a surface of the legacy
/// purchase-order pipeline (supplier delivery config, mapping import, acceptance profiles, the
/// order intake settings), and those surfaces were deleted with it. The document engine gates
/// none of them, so nothing was left to refuse.</para>
///
/// <para>Deleting members here is ordinal-safe: <c>BillingFeature</c> is never persisted or
/// serialized -- re-verified repo-wide for plan 13b: no entity, migration, DTO or Stripe metadata
/// key carries it. Re-verify that before removing any future member, or stored data silently
/// changes meaning.</para>
/// </summary>
public enum BillingFeature
{
    AdvancedAudit,
    // Enterprise SSO (SAML/OIDC). The auth itself is handled natively by Clerk
    // Enterprise Connections; this flag is pure plan-gating metadata that drives
    // the Settings "Single sign-on" availability/upsell — it does NOT touch JWT
    // validation or tenant resolution (the session JWT shape is identical for a
    // SAML login). See docs/strategy/2026-06-08-sso-saml-implementation.md.
    Sso,
}
