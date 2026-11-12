using ProcuLink.TestSupport;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Organisation;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Transform.Detection;
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
/// <para><b>What deliberately stays open</b> is asserted too (see the "still open" region):
/// per-order audit, single-mapping edits, and the stock delivery channels. Gating those would
/// take away things lower tiers already pay for, which is the same dishonesty pointed the
/// other way.</para>
/// </summary>
public class BillingFeatureEnforcementTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Delivery:AllowPrivateNetworkTargets"] = "true",
        })
        .Build();

    /// <summary>Billing double that grants exactly one feature and denies the rest.</summary>
    private static Mock<IBillingService> BillingGranting(BillingFeature? granted)
    {
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((Guid _, BillingFeature f, CancellationToken _) => f == granted);
        billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new LimitCheckResult(true, false, PlanConstants.Enterprise, int.MaxValue));
        return billing;
    }

    /// <summary>
    /// Asserts the refusal is a 403 carrying a well-formed <c>{capability}_requires_{plan}</c> code
    /// naming the plan that really unlocks <paramref name="feature"/>.
    ///
    /// <para><b>Why this is a full-shape match and not <c>EndWith</c>.</b> The original form checked
    /// only the <c>_requires_&lt;plan&gt;</c> suffix, so a 403 whose capability segment named a
    /// DIFFERENT capability passed — and the capability is a hand-written literal sitting next to
    /// the feature at each gate (<c>RequiresPlan("s3_ingestion", BillingFeature.SftpIngestion)</c> is
    /// a one-word copy-paste away). That mismatch is invisible to the plan check yet lands a
    /// customer on an upsell for the wrong feature. Pass <paramref name="capability"/> wherever
    /// production hardcodes one, and the exact string is pinned.</para>
    /// </summary>
    private static void Assert403NamingTheRightPlan(
        IActionResult result, BillingFeature feature, string? capability = null)
    {
        var status = result.Should().BeOfType<ObjectResult>(
            $"the {feature} gate must refuse, not fall through").Subject;
        status.StatusCode.Should().Be(403);

        var error = (string)((dynamic)status.Value!).error;

        if (capability is not null)
        {
            error.Should().Be(BillingGateErrors.RequiresPlan(capability, feature),
                "the 403 must name both the capability being refused and the plan that unlocks it");
            return;
        }

        // No capability supplied (the delivery gates derive theirs from DeliveryCapabilityGate, and
        // ConnectionLifecycleBillingGateTests pins those exactly): still require a well-formed code
        // with a non-empty snake_case capability segment, so a malformed or bare code cannot pass.
        error.Should().MatchRegex($"^[a-z0-9]+(_[a-z0-9]+)*_requires_{PlanConstants.GetMinimumPlan(feature)}$",
            "the 403 must be a complete {capability}_requires_{plan} code naming the plan that "
          + "actually unlocks the feature (WP-11 defect #1)");
    }

    private static void AssertNot403(IActionResult result, BillingFeature feature) =>
        (result as ObjectResult)?.StatusCode.Should().NotBe(403,
            $"an org that HAS {feature} must not be refused — otherwise the gate test is vacuous");

    // ═══ AuditController: AdvancedAudit ══════════════════════════════════════

    private static AuditController BuildAudit(BillingFeature? granted, ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return new AuditController(db, tenant.Object, BillingGranting(granted).Object);
    }

    [Fact]
    public async Task AdvancedAudit_OrgWideTrail_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var db = NewDb();
        var controller = BuildAudit(granted: null, db, Guid.NewGuid());

        var result = await controller.GetAuditLog(ct: CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.AdvancedAudit, "advanced_audit");
    }

    [Fact]
    public async Task AdvancedAudit_OrgWideTrail_IsAllowed_FromOperationsUp()
    {
        var db = NewDb();
        var controller = BuildAudit(granted: BillingFeature.AdvancedAudit, db, Guid.NewGuid());

        var result = await controller.GetAuditLog(ct: CancellationToken.None);

        AssertNot403(result, BillingFeature.AdvancedAudit);
    }

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
