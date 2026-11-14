using FluentAssertions;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-11 — <b>the behavioural half of the ladder guard: every declared feature is really
/// refused below its plan.</b>
///
/// <para><c>BillingFeatureGateCoverageTests</c> proves each <see cref="BillingFeature"/> has a
/// correct plan boundary and a registered enforcement site. This class proves those sites
/// actually fire, by calling the real controller/service with the feature denied and asserting
/// a 403 — and, crucially, calling it again with the feature GRANTED and asserting it does not
/// 403. A one-sided gate test passes just as happily against a gate that refuses everyone.</para>
///
/// <para>Every declared feature except the three ingestion channels was enforced NOWHERE
/// before this change: a Pilot org could configure a webhook, wire up an Enterprise-only ERP
/// connector, author custom acceptance rules, bulk-import mappings, and read the org-wide
/// audit trail — all sold as belonging to tiers between €149 and "contact sales".</para>
///
/// <para>Since plan 14 (2026-11-14) the ladder refuses nothing on the server: plan 13b
/// (2026-11-13) deleted eight features with the legacy order surfaces they gated, and plan 14
/// opened the workspace-wide activity list on every plan. SSO, the one member left, is
/// presentation-only. A document's own history stays open on every plan, pinned as the IL
/// scanner's negative control in <c>BillingGateEnforcementIsRealTests</c>.</para>
/// </summary>
public class BillingFeatureEnforcementTests
{
    // ═══ Sso ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Sso_IsGatedAsPresentationMetadata_NotAsAServerRefusal()
    {
        // SSO is delivered by Clerk Enterprise Connections; ProcuLink's gate is the
        // BillingStatus.SsoAvailable flag that drives the Settings tab's available/upsell
        // state. The behavioural proof that the flag follows the plan lives in
        // StripeBillingServicePricingTests.GetStatus_{Enterprise|BelowEnterprise}_*; this
        // asserts the shared predicate those tests and the service both read.
        PlanConstants.PlanHasFeature(PlanConstants.Enterprise, BillingFeature.Sso).Should().BeTrue();
        PlanConstants.PlanHasFeature(PlanConstants.Distributor, BillingFeature.Sso).Should().BeFalse(
            "Distributor outranks Integration but is still not Enterprise");
    }
}
